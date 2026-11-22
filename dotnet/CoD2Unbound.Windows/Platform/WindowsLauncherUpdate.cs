using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using CoD2Unbound.Core;
using Microsoft.Win32;

namespace CoD2Unbound.Windows.Platform;

internal enum UpdateOutcome { None, Waiting, Failed, Installed }

internal sealed class WindowsLauncherUpdate(string installRoot, string home, string currentVersion,
    WindowsEngineInventory inventory, Func<LauncherActivity> observe, Func<Task> flush) : IWindowsLauncherUpdate
{
    private const string SetupName = "setup.exe";
    private const string PendingName = "pending.txt";
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CoD2Unbound_is1";
    private const string LocationProblem = "This copy of CoD2 Unbound cannot be updated here. Open the download page to get Setup.";
    private readonly string target = installRoot;
    private readonly Func<Task> flushSettings = flush;
    private readonly string staging = Path.Combine(home, "launcher", "updates");
    private string PendingPath => Path.Combine(staging, PendingName);
    internal string LogsPath => Path.Combine(home, "launcher", "logs", "updates");
    internal bool InstallNeedsRepair => WindowsInstallRecovery.RestoreFailed(target);
    private bool reportedUnreadableMarker;
    private bool reportedRestoreFailure;

    public void CheckInstallLocation()
    {
        string standard = WindowsInstallRecovery.StandardRoot;
        using var registry = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var installed = registry.OpenSubKey(RegistryPath);
        if (!PathsMatch(target, standard) || installed?.GetValue("InstallLocation") is not string registered ||
            !PathsMatch(target, registered))
            throw new LauncherUpdateException(LocationProblem, issue: UpdateIssue.ProtectedLocation);

        try
        {
            foreach (string relative in new[] { "", "launcher", "original", "cod2x", "licenses" })
            {
                string folder = Path.Combine(target, relative);
                using var parents = new WindowsFiles.DirectoryHandles(folder);
                using var probe = new FileStream(Path.Combine(folder, $".update-{Guid.NewGuid():N}"), FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new LauncherUpdateException(LocationProblem, error, UpdateIssue.ProtectedLocation);
        }
        if (!SetupPublisher.Configured)
            throw new LauncherUpdateException("This build has no trusted Setup publisher. Open the download page to get Setup.");
    }

    internal LauncherActivity Observe()
    {
        var activity = observe();
        return new(activity.GameRunning || !inventory.FindRunning(EngineRole.Client, default).IsEmpty,
            activity.ServersRunning || !inventory.FindRunning(EngineRole.Dedicated, default).IsEmpty);
    }

    public async ValueTask<IUpdateInstallLease> AcquireInstallLeaseAsync(CancellationToken cancellationToken)
    {
        await CapturePauseAsync(cancellationToken);
        var client = await WindowsGameSessionHost.OpenLeaseAsync(home, ".launcher-start.lock", cancellationToken);
        FileStream? server = null;
        try
        {
            server = await WindowsGameSessionHost.OpenLeaseAsync(home, "hosting/.hosting-start.lock", cancellationToken);
            return new InstallLease(client, server, Observe());
        }
        catch
        {
            server?.Dispose();
            client.Dispose();
            throw;
        }
    }

    public async ValueTask<IStagedLauncherUpdate> DownloadAsync(ReleaseOffer offer, Action<long> progress,
        CancellationToken cancellationToken)
    {
        if (!WindowsReleaseAssets.IsNewer(offer.Version, currentVersion))
            throw new LauncherUpdateException("That version cannot replace the current version.");
        string id = Guid.NewGuid().ToString("N");
        EnsureStaging();
        using var root = new WindowsFiles.DirectoryHandles(staging);
        string path = Path.Combine(staging, id);
        EnsurePrivateDirectory(path);
        var attempt = new StagedUpdate(this, id, offer, new WindowsFiles.DirectoryHandles(path));
        try
        {
            await attempt.DownloadAsync(progress, cancellationToken);
            return attempt;
        }
        catch
        {
            await attempt.DisposeAsync();
            throw;
        }
    }

    internal UpdateOutcome ReadOutcome()
    {
        if (InstallNeedsRepair)
        {
            if (reportedRestoreFailure)
                return UpdateOutcome.None;
            reportedRestoreFailure = true;
            Log("Setup could not restore the previous installation. The backup and update record are retained.");
            return UpdateOutcome.Failed;
        }
        if (!Directory.Exists(staging))
            return UpdateOutcome.None;

        try
        {
            EnsureStaging();
            using var root = new WindowsFiles.DirectoryHandles(staging);
            PendingUpdate pending;
            try { pending = ReadPending(staging); }
            catch (FileNotFoundException)
            {
                CleanAttempts();
                return UpdateOutcome.None;
            }
            if (pending.Running)
                return UpdateOutcome.Waiting;

            bool installed = currentVersion == pending.Version && !WindowsInstallRecovery.NeedsRestore(target);
            File.Delete(PendingPath);
            CleanAttempts(includeLegacyBackups: installed);
            Log(installed ? "Setup installed the offered version." : "Setup did not complete the offered update.");
            return installed ? UpdateOutcome.Installed : UpdateOutcome.Failed;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            try
            {
                // Validate the parent before touching an unreadable record.
                using var root = new WindowsFiles.DirectoryHandles(staging);
                File.Delete(PendingPath);
                CleanAttempts();
            }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException) { }
            if (reportedUnreadableMarker)
                return UpdateOutcome.None;
            reportedUnreadableMarker = true;
            Log($"Could not read the pending update record ({error.HResult:X8}).");
            return UpdateOutcome.Failed;
        }
    }

    private sealed record PendingUpdate(string Id, string Version, long Size, string? Checksum, bool Running);

    private static PendingUpdate ReadPending(string folder)
    {
        using var stream = WindowsFiles.Open(folder, PendingName);
        if (stream.Length > 256)
            throw new IOException("The pending update record was too large.");
        using var reader = new StreamReader(stream);
        var fields = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length is not (2 or 4 or 6) || !Guid.TryParseExact(fields[0], "N", out _) ||
            WindowsReleaseAssets.ParseVersion(fields[1]) is null)
            throw new IOException("The pending update record was invalid.");

        long size = 0;
        string? checksum = null;
        if (fields.Length >= 4 && fields[3].Length == 64 && fields[3].All(char.IsAsciiHexDigit) &&
            long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out size) &&
            size is > 0 and <= WindowsReleaseAssets.SetupLimit)
            checksum = fields[3];

        int process = checksum is null ? 2 : 4;
        if (fields.Length != process && fields.Length != process + 2)
            throw new IOException("The pending update record was invalid.");
        bool running = fields.Length == process + 2 &&
            int.TryParse(fields[process], NumberStyles.None, CultureInfo.InvariantCulture, out int pid) &&
            long.TryParse(fields[process + 1], NumberStyles.None, CultureInfo.InvariantCulture, out long started) &&
            IsSetupRunning(pid, started);
        return new(fields[0], fields[1], size, checksum, running);
    }

    internal static bool StartRecovery(string target, string home, string attempt)
    {
        string staging = Path.Combine(home, "launcher", "updates");
        using var root = new WindowsFiles.DirectoryHandles(staging);
        CheckPrivateDirectory(staging, create: false);
        var pending = ReadPending(staging);
        if (pending.Id != attempt || pending.Checksum is null)
            return false;
        if (pending.Running)
            return true;

        string folder = Path.Combine(staging, pending.Id);
        using var directories = new WindowsFiles.DirectoryHandles(folder);
        CheckPrivateDirectory(folder, create: false);
        using var pinned = WindowsFiles.Open(folder, SetupName, FileShare.Read);
        string path = Path.Combine(folder, SetupName);
        VerifySetup(path, pinned, pending.Version, pending.Size, pending.Checksum, default, restore: true);
        string logs = Path.Combine(home, "launcher", "logs", "updates");
        EnsurePrivateDirectory(logs);
        using var process = StartSetup(path, target, Path.Combine(logs, $"restore-{pending.Id}.log"), pending.Id, restore: true);
        return true;
    }

    private static void VerifySetup(string path, FileStream pinned, string expectedVersion, long size,
        string checksum, CancellationToken token, bool restore = false)
    {
        if (!ReleasePackage.VerifyArchive(pinned, size, checksum, token))
            throw new LauncherUpdateException("The download did not match the release checksum.");

        token.ThrowIfCancellationRequested();
        pinned.Position = 0;
        SetupPublisher.Verify(path, pinned.SafeFileHandle);
        var details = FileVersionInfo.GetVersionInfo(path);
        var version = new Version(details.FileMajorPart, details.FileMinorPart, details.FileBuildPart);
        // Inno reserves room in its version resource by padding the product name.
        if (details.ProductName?.TrimEnd(' ') != "CoD2 Unbound" || details.FilePrivatePart != 0 ||
            version != WindowsReleaseAssets.ParseVersion(expectedVersion))
            throw new LauncherUpdateException("Setup did not contain the offered version of CoD2 Unbound.");
        if (restore && details.FileDescription?.TrimEnd(' ') != WindowsInstallRecovery.RestoreDescription)
            throw new LauncherUpdateException("Setup did not advertise restore-only support.");
    }

    private static Process StartSetup(string path, string target, string log, string attempt, bool restore = false)
    {
        using var launcher = Process.GetCurrentProcess();
        var start = new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path)! };
        foreach (string arg in new[] { "/SP-", "/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CURRENTUSER",
            "/NOCLOSEAPPLICATIONS", "/NORESTARTAPPLICATIONS", "/UNBOUNDUPDATE=1",
            $"/UNBOUNDLAUNCHERPID={launcher.Id}", $"/UNBOUNDLAUNCHERSTART={launcher.StartTime.ToUniversalTime().ToFileTimeUtc()}",
            $"/UNBOUNDATTEMPT={attempt}", $"/DIR={target}", $"/LOG={log}" })
            start.ArgumentList.Add(arg);
        if (restore)
            start.ArgumentList.Add("/UNBOUNDRESTORE=1");
        return Process.Start(start) ?? throw new IOException("Windows did not start Setup.");
    }

    private static bool IsSetupRunning(int pid, long started)
    {
        if (pid <= 0 || started <= 0)
            return false;
        long now = DateTime.UtcNow.ToFileTimeUtc();
        if (started > now || now - started > TimeSpan.FromMinutes(2).Ticks)
            return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.StartTime.ToUniversalTime().ToFileTimeUtc() == started && !process.HasExited;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    internal void Log(string message)
    {
        try
        {
            EnsurePrivateDirectory(LogsPath);
            using var parents = new WindowsFiles.DirectoryHandles(LogsPath);
            string file = Path.Combine(LogsPath, "update.log");
            if (File.Exists(file))
            {
                using var current = WindowsFiles.Open(LogsPath, "update.log");
                if (current.Length > 128 * 1024)
                    File.Move(file, Path.Combine(LogsPath, "update.previous.log"), overwrite: true);
            }
            File.AppendAllText(file, $"{DateTimeOffset.UtcNow:O} {message}\n");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static Task CapturePauseAsync(CancellationToken token)
    {
#if LOCAL_UPDATE_FEED
        if (WindowsReleaseSource.LocalOrigin is not null &&
            Environment.GetEnvironmentVariable("COD2_UNBOUND_TEST_UPDATE_VISUAL") == "1")
            return Task.Delay(TimeSpan.FromMilliseconds(1200), token);
#endif
        return Task.CompletedTask;
    }

    private void CleanAttempts(bool includeLegacyBackups = false)
    {
        foreach (string path in Directory.EnumerateDirectories(staging))
            if (Guid.TryParseExact(Path.GetFileName(path), "N", out _))
                DeleteAttempt(path, includeLegacyBackups);
    }

    private static void DeleteAttempt(string path, bool includeLegacyBackups = false)
    {
        try
        {
            if (!Directory.Exists(path) || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                return;
            File.Delete(Path.Combine(path, SetupName));
            if (!includeLegacyBackups && Directory.Exists(Path.Combine(path, "previous-install")))
                return; // A pre-transaction Setup may still have a useful saved copy.
            Directory.Delete(path, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static bool PathsMatch(string left, string right) => string.Equals(Path.TrimEndingDirectorySeparator(left),
        Path.TrimEndingDirectorySeparator(right), StringComparison.OrdinalIgnoreCase);

    private void EnsureStaging()
    {
        using var parent = new WindowsFiles.DirectoryHandles(Path.GetDirectoryName(staging)!, create: true);
        try { EnsurePrivateDirectory(staging); }
        catch (Exception error) when (error is LauncherUpdateException or UnauthorizedAccessException)
        {
            // Move the directory entry itself. Reading its ACL may be denied.
            Directory.Move(staging, staging + ".quarantined-" + Guid.NewGuid().ToString("N"));
            EnsurePrivateDirectory(staging);
            Log("Recreated the private update folder after its permissions changed.");
        }

        using var root = new WindowsFiles.DirectoryHandles(staging);
        foreach (string path in Directory.EnumerateDirectories(Path.GetDirectoryName(staging)!, "updates.quarantined-*"))
        {
            if (!Guid.TryParseExact(Path.GetFileName(path)["updates.quarantined-".Length..], "N", out _))
                continue;
            try { Directory.Delete(path, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void EnsurePrivateDirectory(string path) => CheckPrivateDirectory(path, create: true);

    private static void CheckPrivateDirectory(string path, bool create)
    {
        using var user = WindowsIdentity.GetCurrent();
        var sid = user.User ?? throw new IOException("Windows did not identify the current user.");
        using var parent = new WindowsFiles.DirectoryHandles(Path.GetDirectoryName(path)!, create);
        var directory = new DirectoryInfo(path);
        var expected = new DirectorySecurity();
        expected.SetOwner(sid);
        expected.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        expected.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        if (create && !directory.Exists)
            directory.Create(expected);
        using var opened = new WindowsFiles.DirectoryHandles(path);
        var actual = directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        if (!sid.Equals(actual.GetOwner(typeof(SecurityIdentifier))) || !actual.AreAccessRulesProtected ||
            actual.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>().Any(rule => !sid.Equals(rule.IdentityReference)))
            throw new LauncherUpdateException("The update folder is not private to your Windows account.");
    }

    private sealed class StagedUpdate(WindowsLauncherUpdate owner, string id, ReleaseOffer offer,
        WindowsFiles.DirectoryHandles directories) : IStagedLauncherUpdate
    {
        private string PathTo(string name) => Path.Combine(owner.staging, id, name);
        private FileStream? pinned;
        private string? checksum;
        private bool verified;
        private bool handedOff;

        internal async Task DownloadAsync(Action<long> progress, CancellationToken token)
        {
            var download = WindowsReleaseSource.Download;
            try
            {
                checksum = await download.ReadSetupChecksumAsync(offer, token);
                await using (var output = new FileStream(PathTo(SetupName), FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 64 * 1024, FileOptions.Asynchronous))
                    await download.CopySetupAsync(offer, output, progress, token);
                pinned = WindowsFiles.Open(Path.Combine(owner.staging, id), SetupName, FileShare.Read);
            }
            catch (Exception error) when (error is HttpRequestException or HttpIOException ||
                error is OperationCanceledException && !token.IsCancellationRequested)
            {
                throw new LauncherUpdateException("The download stopped before it finished.", error, UpdateIssue.Interrupted);
            }
        }

        public async ValueTask VerifyAsync(CancellationToken cancellationToken)
        {
            await CapturePauseAsync(cancellationToken);
            await Task.Run(() =>
            {
                if (pinned is null || checksum is null)
                    throw new LauncherUpdateException("The download did not match the release checksum.");
                VerifySetup(PathTo(SetupName), pinned, offer.Version, offer.Archive.Size, checksum, cancellationToken);
                verified = true;
            }, cancellationToken);
        }

        public async ValueTask InstallAsync(CancellationToken cancellationToken)
        {
            if (!verified)
                throw new LauncherUpdateException("Setup could not be verified.");
            await CapturePauseAsync(cancellationToken);
            await owner.flushSettings();
            EnsurePrivateDirectory(owner.LogsPath);
            owner.CheckInstallLocation();
            var activity = owner.Observe();
            if (ReleasePackage.Block(activity.GameRunning, activity.ServersRunning) is { } block)
                throw new UpdateHeldException(block);
            string log = Path.Combine(owner.LogsPath, $"setup-{id}.log");
            string[] pending = [id, offer.Version, offer.Archive.Size.ToString(CultureInfo.InvariantCulture), checksum!];
            WritePending(pending);
            try
            {
                using var setup = StartSetup(PathTo(SetupName), owner.target, log, id);
                handedOff = true;
                try
                {
                    WritePending([.. pending, setup.Id.ToString(CultureInfo.InvariantCulture),
                        setup.StartTime.ToUniversalTime().ToFileTimeUtc().ToString(CultureInfo.InvariantCulture)]);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
                {
                    owner.Log($"Could not record Setup's process identity ({error.HResult:X8}).");
                }
            }
            catch
            {
                File.Delete(owner.PendingPath);
                throw;
            }
        }

        private void WritePending(string[] contents)
        {
            string temporary = PathTo("pending.tmp");
            using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using var writer = new StreamWriter(output, leaveOpen: true);
                foreach (string line in contents)
                    writer.WriteLine(line);
                writer.Flush();
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, owner.PendingPath, overwrite: true);
        }

        public ValueTask DisposeAsync()
        {
            pinned?.Dispose();
            directories.Dispose();
            if (!handedOff)
                DeleteAttempt(Path.Combine(owner.staging, id));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class InstallLease(FileStream client, FileStream server, LauncherActivity activity) : IUpdateInstallLease
    {
        public LauncherActivity Activity => activity;

        public ValueTask DisposeAsync()
        {
            server.Dispose();
            client.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

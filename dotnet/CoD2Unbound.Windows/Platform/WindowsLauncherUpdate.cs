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
    private bool reportedUnreadableMarker;

    public void CheckInstallLocation()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string standard = Path.Combine(local, "Programs", "CoD2 Unbound");
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
        if (!Directory.Exists(staging))
            return UpdateOutcome.None;

        try
        {
            EnsureStaging();
            using var root = new WindowsFiles.DirectoryHandles(staging);
            string[] pending;
            try
            {
                using var stream = WindowsFiles.Open(staging, PendingName);
                if (stream.Length > 256)
                    throw new IOException("The pending update record was too large.");
                using var reader = new StreamReader(stream);
                pending = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
            catch (FileNotFoundException)
            {
                CleanAttempts();
                return UpdateOutcome.None;
            }
            if (pending is [_, _, var process, var fileTime] &&
                int.TryParse(process, NumberStyles.None, CultureInfo.InvariantCulture, out int pid) &&
                long.TryParse(fileTime, NumberStyles.None, CultureInfo.InvariantCulture, out long started) &&
                IsSetupRunning(pid, started))
                return UpdateOutcome.Waiting;

            bool installed = pending.Length is 2 or 4 && Guid.TryParseExact(pending[0], "N", out _) &&
                currentVersion == pending[1] && !WindowsInstallRecovery.NeedsRestore(target);
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
        catch (LauncherUpdateException) when (Directory.Exists(staging))
        {
            // Quarantine a changed ACL. Never follow an update folder junction.
            using (new WindowsFiles.DirectoryHandles(staging)) { }
            Directory.Move(staging, staging + ".quarantined-" + Guid.NewGuid().ToString("N"));
            EnsurePrivateDirectory(staging);
            Log("Recreated the private update folder after its permissions changed.");
        }
    }

    private static void EnsurePrivateDirectory(string path)
    {
        using var user = WindowsIdentity.GetCurrent();
        var sid = user.User ?? throw new IOException("Windows did not identify the current user.");
        using var parent = new WindowsFiles.DirectoryHandles(Path.GetDirectoryName(path)!, create: true);
        var directory = new DirectoryInfo(path);
        var expected = new DirectorySecurity();
        expected.SetOwner(sid);
        expected.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        expected.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        if (!directory.Exists)
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
            using var sums = new MemoryStream();
            var download = WindowsReleaseSource.Download;
            try
            {
                await download.CopyAsync(offer.Checksums, sums, WindowsReleaseAssets.ChecksumsLimit, offer.ChecksumsSize,
                    null, token);
                checksum = ReleasePackage.Checksum(sums.GetBuffer().AsSpan(0, (int)sums.Length), offer.Archive.Name);
                if (checksum is null)
                    throw new LauncherUpdateException("The release did not provide a usable checksum for Setup.");
                await using (var output = new FileStream(PathTo(SetupName), FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 64 * 1024, FileOptions.Asynchronous))
                    await download.CopyAsync(offer.Archive.Url, output, WindowsReleaseAssets.SetupLimit, offer.Archive.Size,
                        progress, token);
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
                if (pinned is null || checksum is null ||
                    !ReleasePackage.VerifyArchive(pinned, offer.Archive.Size, checksum, cancellationToken))
                    throw new LauncherUpdateException("The download did not match the release checksum.");

                cancellationToken.ThrowIfCancellationRequested();
                pinned.Position = 0;
                SetupPublisher.Verify(PathTo(SetupName), pinned.SafeFileHandle);
                var details = FileVersionInfo.GetVersionInfo(PathTo(SetupName));
                var version = new Version(details.FileMajorPart, details.FileMinorPart, details.FileBuildPart);
                // Inno reserves room in its version resource by padding the product name.
                if (details.ProductName?.TrimEnd(' ') != "CoD2 Unbound" || details.FilePrivatePart != 0 ||
                    version != WindowsReleaseAssets.ParseVersion(offer.Version))
                    throw new LauncherUpdateException("Setup did not contain the offered version of CoD2 Unbound.");
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
            using var launcher = Process.GetCurrentProcess();
            var start = new ProcessStartInfo(PathTo(SetupName)) { UseShellExecute = false, WorkingDirectory = Path.Combine(owner.staging, id) };
            foreach (string arg in new[] { "/SP-", "/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CURRENTUSER",
                "/NOCLOSEAPPLICATIONS", "/NORESTARTAPPLICATIONS", "/UNBOUNDUPDATE=1",
                $"/UNBOUNDLAUNCHERPID={launcher.Id}", $"/UNBOUNDLAUNCHERSTART={launcher.StartTime.ToUniversalTime().ToFileTimeUtc()}",
                $"/DIR={owner.target}", $"/LOG={log}" })
                start.ArgumentList.Add(arg);
            WritePending([id, offer.Version]);
            try
            {
                using var setup = Process.Start(start) ?? throw new IOException("Windows did not start Setup.");
                handedOff = true;
                try
                {
                    WritePending([id, offer.Version, setup.Id.ToString(CultureInfo.InvariantCulture),
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

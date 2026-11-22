using System.ComponentModel;
using System.Diagnostics;
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
    private const string DoneName = "installed.ok";
    private const string PendingName = "pending.txt";
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CoD2Unbound_is1";
    private const string LocationProblem = "This copy of CoD2 Unbound cannot be updated here. Open the download page to get Setup.";
    private readonly string target = installRoot;
    private readonly Func<Task> flushSettings = flush;
    private readonly string staging = Path.Combine(home, "launcher", "updates");
    internal string LogsPath => Path.Combine(staging, "logs");

    public void CheckInstallLocation()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string standard = Path.Combine(local, "Programs", "CoD2 Unbound");
        using var registry = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var installed = registry.OpenSubKey(RegistryPath);
        if (!PathsMatch(target, standard) || installed?.GetValue("InstallLocation") is not string registered ||
            !PathsMatch(target, registered))
            throw new LauncherUpdateException(LocationProblem);

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
            throw new LauncherUpdateException(LocationProblem, error);
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
        EnsurePrivateDirectory(staging);
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
            EnsurePrivateDirectory(staging);
            using var root = new WindowsFiles.DirectoryHandles(staging);
            string marker = Path.Combine(staging, PendingName);
            string[] pending = File.Exists(marker) ? File.ReadAllLines(marker) : [];
            bool valid = pending is [var id, var version] && Guid.TryParseExact(id, "N", out _) &&
                WindowsReleaseAssets.ParseVersion(version) is not null;
            if (!valid)
            {
                CleanAttempts();
                return UpdateOutcome.None;
            }

            string attempt = Path.Combine(staging, pending[0]);
            string setup = Path.Combine(attempt, SetupName);
            if (File.Exists(setup))
            {
                try
                {
                    using var directory = new WindowsFiles.DirectoryHandles(attempt);
                    using var inactive = new FileStream(setup, FileMode.Open, FileAccess.Write, FileShare.None);
                }
                catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 or 5)
                {
                    return UpdateOutcome.Waiting;
                }
            }
            bool installed = currentVersion == pending[1] && File.Exists(Path.Combine(attempt, DoneName));
            File.Delete(marker);
            CleanAttempts();
            return installed ? UpdateOutcome.Installed : UpdateOutcome.Failed;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return UpdateOutcome.Failed;
        }
    }

    private void PrepareLogs() => EnsurePrivateDirectory(LogsPath);

    private static Task CapturePauseAsync(CancellationToken token)
    {
#if LOCAL_UPDATE_FEED
        if (WindowsReleaseSource.LocalOrigin is not null &&
            Environment.GetEnvironmentVariable("COD2_UNBOUND_TEST_UPDATE_VISUAL") == "1")
            return Task.Delay(TimeSpan.FromMilliseconds(1200), token);
#endif
        return Task.CompletedTask;
    }

    private void CleanAttempts()
    {
        foreach (string path in Directory.EnumerateDirectories(staging))
            if (Guid.TryParseExact(Path.GetFileName(path), "N", out _))
                DeleteAttempt(path);
    }

    private static void DeleteAttempt(string path)
    {
        try
        {
            if (!Directory.Exists(path) || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                return;
            File.Delete(Path.Combine(path, SetupName));
            if (!File.Exists(Path.Combine(path, DoneName)) && Directory.Exists(Path.Combine(path, "previous-install")))
                return; // Keep recoverable files if Setup could not restore them.
            File.Delete(Path.Combine(path, DoneName));
            File.Delete(Path.Combine(path, "pending.tmp"));
            Directory.Delete(path, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static bool PathsMatch(string left, string right) => string.Equals(Path.TrimEndingDirectorySeparator(left),
        Path.TrimEndingDirectorySeparator(right), StringComparison.OrdinalIgnoreCase);

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
            catch (Exception error) when (error is HttpRequestException or HttpIOException)
            {
                throw new LauncherUpdateException("The download was interrupted. Try again.", error);
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
            owner.CheckInstallLocation();
            await owner.flushSettings();
            owner.PrepareLogs();
            string log = Path.Combine(owner.LogsPath, $"setup-{id}.log");
            string marker = Path.Combine(owner.staging, PendingName);
            string temporary = PathTo("pending.tmp");
            await File.WriteAllLinesAsync(temporary, [id, offer.Version], cancellationToken);
            File.Move(temporary, marker, overwrite: true);

            var start = new ProcessStartInfo(PathTo(SetupName)) { UseShellExecute = false, WorkingDirectory = Path.Combine(owner.staging, id) };
            foreach (string arg in new[] { "/SP-", "/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CURRENTUSER",
                "/NOCLOSEAPPLICATIONS", "/NORESTARTAPPLICATIONS", "/UNBOUNDUPDATE=1",
                $"/DIR={owner.target}", $"/LOG={log}" })
                start.ArgumentList.Add(arg);
            try
            {
                using var setup = Process.Start(start) ?? throw new IOException("Windows did not start Setup.");
                handedOff = true;
            }
            catch (Exception error) when (error is Win32Exception or IOException)
            {
                File.Delete(marker);
                throw new LauncherUpdateException("Windows could not start Setup. Try again or open the download page.", error);
            }
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

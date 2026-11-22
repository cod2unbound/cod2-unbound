using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CoD2Unbound.Windows.Platform;

internal static class WindowsInstallRecovery
{
    // Keep these sibling names and the mutex in sync with unbound.iss. A previous
    // directory is the journal. Renaming it to completed is Setup's commit point.
    private const string MutexName = @"Local\CoD2Unbound.InstallTransaction";
    private const string Previous = ".previous-install";
    private const string Incomplete = ".incomplete-install";

    internal static bool NeedsRestore(string root) => Directory.Exists(root + Previous);

    internal static bool RedirectInterruptedStart()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string root = Path.Combine(local, "Programs", "CoD2 Unbound");
        string actual = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        if (!root.Equals(actual, StringComparison.OrdinalIgnoreCase) ||
            (!NeedsRestore(root) && !Directory.Exists(root + Incomplete)))
            return false;

        try
        {
            using (new WindowsFiles.DirectoryHandles(Path.GetDirectoryName(root)!)) { }
            using var transaction = new Mutex(false, MutexName);
            try
            {
                if (!transaction.WaitOne(0))
                    return true; // Setup or the recovery process will reopen the launcher.
            }
            catch (AbandonedMutexException) { } // Ownership was handed to us.
            try
            {
                using var current = Process.GetCurrentProcess();
                var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
                var start = new ProcessStartInfo(Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe"))
                {
                    UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(root)!
                };
                foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand",
                    Convert.ToBase64String(Encoding.Unicode.GetBytes(
                        "$launcherPid = " + current.Id + "\n$launcherStarted = " +
                        current.StartTime.ToUniversalTime().ToFileTimeUtc() + "\n" + RestoreScript)) })
                    start.ArgumentList.Add(argument);
                using var process = Process.Start(start) ?? throw new IOException("Windows did not start recovery.");
            }
            finally { transaction.ReleaseMutex(); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            _ = MessageBox(0, "CoD2 Unbound needs repair. Close the game and run Setup again.", "CoD2 Unbound", 0x10);
        }
        return true;
    }

    private const string RestoreScript = """
        $ErrorActionPreference = 'Stop'
        $root = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\CoD2 Unbound'
        $previous = $root + '.previous-install'
        $incomplete = $root + '.incomplete-install'
        $mutex = [Threading.Mutex]::new($false, 'Local\CoD2Unbound.InstallTransaction')
        $held = $false
        $restored = $false
        try {
            $launcher = Get-Process -Id $launcherPid -ErrorAction SilentlyContinue
            if ($launcher -and $launcher.StartTime.ToUniversalTime().ToFileTimeUtc() -eq $launcherStarted) {
                if (!$launcher.WaitForExit(30000)) { throw 'Launcher did not exit.' }
            }
            try { $held = $mutex.WaitOne(30000) }
            catch [Threading.AbandonedMutexException] { $held = $true }
            if (!$held) { throw 'Another installation is active.' }

            foreach ($path in @((Split-Path -Parent $root), $root, $previous, $incomplete)) {
                if ((Test-Path -LiteralPath $path) -and
                    ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                    throw 'Unexpected link in installation.'
                }
            }
            if ([IO.Directory]::Exists($previous)) {
                if ([IO.Directory]::Exists($root)) { [IO.Directory]::Move($root, $incomplete) }
                [IO.Directory]::Move($previous, $root)
            }
            if ([IO.Directory]::Exists($incomplete)) {
                $version = [IO.File]::ReadAllText((Join-Path $root 'RELEASE_VERSION')).Trim()
                if ($version -match '^\d+\.\d+\.\d+$') {
                    $key = [Microsoft.Win32.RegistryKey]::OpenBaseKey('CurrentUser', 'Registry64')
                    try {
                        $installed = $key.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Uninstall\CoD2Unbound_is1', $true)
                        if ($installed) {
                            try { $installed.SetValue('DisplayVersion', $version) }
                            finally { $installed.Dispose() }
                        }
                    } finally { $key.Dispose() }
                }
                [IO.Directory]::Delete($incomplete, $true)
                $restored = $true
            }
        } catch {
            Add-Type -AssemblyName System.Windows.Forms
            [void][Windows.Forms.MessageBox]::Show('CoD2 Unbound needs repair. Close the game and run Setup again.', 'CoD2 Unbound')
            exit 1
        } finally {
            if ($held) { $mutex.ReleaseMutex() }
            $mutex.Dispose()
        }
        if ($restored) {
            Add-Type -AssemblyName System.Windows.Forms
            [void][Windows.Forms.MessageBox]::Show('The previous CoD2 Unbound installation was restored.', 'CoD2 Unbound')
            Start-Process -FilePath (Join-Path $root 'launcher\CoD2Unbound.Windows.exe') -WorkingDirectory $root
        }
        """;

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBox(nint owner, string message, string caption, uint type);
}

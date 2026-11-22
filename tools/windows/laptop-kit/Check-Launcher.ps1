function Save-LauncherPicture($Process, [string]$Evidence, [string]$Name) {
    $Process.Refresh()
    if ($Process.HasExited -or $Process.MainWindowHandle -eq [IntPtr]::Zero) {
        throw 'The launcher window is no longer open.'
    }
    $window = $Process.MainWindowHandle
    $shell = New-Object -ComObject WScript.Shell
    try { [void]$shell.AppActivate($Process.Id) }
    finally { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell) }
    Start-Sleep -Milliseconds 500
    $screen = [KitLauncherPicture]::Save($window, (Join-Path $Evidence "$Name.png"))
    $theme = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' -ErrorAction SilentlyContinue
    $highContrast = [Windows.Forms.SystemInformation]::HighContrast
    "$Name $screen; app light=$($theme.AppsUseLightTheme); system light=$($theme.SystemUsesLightTheme); contrast=$highContrast" |
        Add-Content -LiteralPath (Join-Path $Evidence 'display.txt')
    Write-Host "Saved $Name.png ($screen)."
}

function Invoke-LauncherCheck([string]$Root, [string]$StartMenu, [string]$GamePath) {
    Assert-Offline
    Add-Type -AssemblyName System.Windows.Forms,System.Drawing
    Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
    if (-not ('KitLauncherPicture' -as [type])) {
        Add-Type -ReferencedAssemblies System.Drawing,System.Windows.Forms -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public static class KitLauncherPicture
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    public static string Save(IntPtr window, string destination)
    {
        IntPtr previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            Rect rect;
            if (GetForegroundWindow() != window || !GetWindowRect(window, out rect))
                throw new InvalidOperationException("Bring CoD2 Unbound to the front and leave its window open for the picture.");
            Rectangle screen = Screen.FromHandle(window).Bounds;
            Rectangle bounds = Rectangle.Intersect(screen, Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom));
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
                throw new InvalidOperationException("Move the launcher onto the screen for the picture.");
            using (var bitmap = new Bitmap(bounds.Width, bounds.Height))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
                bitmap.Save(destination, ImageFormat.Png);
            }
            return string.Format("{0}x{1} display, {2}x{3} visible window", screen.Width, screen.Height, bounds.Width, bounds.Height);
        }
        finally
        {
            if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous);
        }
    }
}
'@
    }

    $entry = New-RunContext 'launcher'
    $exe = Join-Path $Root 'launcher\CoD2Unbound.Windows.exe'
    $link = Join-Path $StartMenu 'CoD2 Unbound.lnk'
    if (-not (Test-Path -LiteralPath $link -PathType Leaf)) { throw 'CoD2 Unbound is missing from Start.' }
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $null
    try {
        $shortcut = $shell.CreateShortcut($link)
        if ($shortcut.TargetPath -ine $exe -or $shortcut.Arguments -or -not (Test-Path -LiteralPath $exe -PathType Leaf)) {
            throw 'The Start shortcut does not point to the installed launcher.'
        }
    } finally {
        if ($shortcut) { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shortcut) }
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
    }
    $homePath = New-Folder (Join-Path $entry.Folder 'game')
    $settings = New-Folder (Join-Path $homePath 'launcher')
    # Keep both engines on loopback, just as the kit's portable runs do.
    @{ advanced = "net_ip 127.0.0.1`nnet_noipx 1"; introFilmsPlayed = $false } | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $settings 'launcher-settings.json') -Encoding ASCII
    $releases = Join-Path $entry.Folder 'releases.json'
    '[]' | Set-Content -LiteralPath $releases -Encoding ASCII
    $run = Start-KitProcess $exe '' $homePath @{
        COD2_UNBOUND_TEST_HOME = $homePath
        COD2_UNBOUND_TEST_MASTER_PORT = '9'
        COD2_UNBOUND_TEST_RELEASES = $releases
        COD2_UNBOUND_TEST_CONFIG_DIRECTORY = Join-Path $Kit 'launcher-fixtures\directory.json'
    }
    $process = $run.Process
    $failure = $null
    try {
        $deadline = (Get-Date).AddSeconds(60)
        do {
            Start-Sleep -Milliseconds 400
            $process.Refresh()
        } until ($process.HasExited -or $process.MainWindowHandle -ne [IntPtr]::Zero -or (Get-Date) -ge $deadline)
        if ($process.HasExited -or $process.MainWindowHandle -eq [IntPtr]::Zero) {
            throw 'The installed launcher did not open a window.'
        }
        Start-Sleep -Seconds 2
        Save-LauncherPicture $process $entry.Evidence '01-setup'
        Write-Host "In the launcher choose the game folder $GamePath if Setup did not already find it."
        Write-Host 'On the CD key page enter the sample key 000000000000000086D3. Finish Setup and open Home.'
        [void](Read-Host 'When Home is open, maximize it, then switch back to PowerShell and press Enter')
        if (-not (Test-Path -LiteralPath (Join-Path $settings '.launcher-setup-complete') -PathType Leaf) -or
            (Get-DataRoot (Get-Content -LiteralPath (Join-Path $settings 'data-path.txt') -Raw)) -ine $GamePath) {
            throw 'The launcher did not finish Setup with the selected game folder.'
        }
        Save-LauncherPicture $process $entry.Evidence '02-home-native'
        [void](Read-Host 'Double-click Sample config.cfg in the kit folder. If asked, choose Open with CoD2 Unbound. Leave Review import open and press Enter here')
        $window = [Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
        $review = New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::NameProperty), 'Review import'
        if (-not $window.FindFirst([Windows.Automation.TreeScope]::Descendants, $review)) {
            throw 'The sample config did not open Review import in the launcher window already running.'
        }
        Save-LauncherPicture $process $entry.Evidence '02b-sample-config'
        [void](Read-Host 'Cancel the import review in the launcher and press Enter here')
        $loadouts = Join-Path $homePath 'main\players'
        $beforeSetupLink = (Get-TreeHashes $loadouts) -join "`n"
        Start-Process 'cod2unbound://setup/sample'
        $deadline = (Get-Date).AddSeconds(20)
        $detail = New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::NameProperty), 'Try this setup'
        do {
            Start-Sleep -Milliseconds 250
            $setupPage = $window.FindFirst([Windows.Automation.TreeScope]::Descendants, $detail)
        } until ($setupPage -or (Get-Date) -ge $deadline)
        if (-not $setupPage -or $beforeSetupLink -ne ((Get-TreeHashes $loadouts) -join "`n")) {
            throw 'The setup link did not open the setup in the existing window without changing a loadout.'
        }
        Save-LauncherPicture $process $entry.Evidence '02c-setup-link'
        [void](Read-Host 'Return to Home in the launcher and then press Enter here')
        if (Confirm-Step 'If 1440 x 900 is offered, select it in Windows Display settings, then come back here') {
            Save-LauncherPicture $process $entry.Evidence '03-home-1440x900'
        } else {
            '1440x900 skipped or unavailable.' | Add-Content -LiteralPath (Join-Path $entry.Evidence 'display.txt')
        }
        foreach ($mode in 'light', 'dark', 'high contrast') {
            [void](Read-Host "Restore your usual resolution and select $mode in Windows Settings. Press Enter to picture Home")
            Save-LauncherPicture $process $entry.Evidence "home-$($mode.Replace(' ', '-'))"
        }
        Write-Host 'Restore your usual Windows appearance. In the launcher go to Settings to choose each game version, then return to Home and select Launch.'
        $notes = Join-Path $entry.Evidence 'what-you-saw.txt'
        foreach ($edition in 'Original 1.3', 'CoD2x') {
            $observed = Read-Host "Open $edition from Home, wait for its game menu, then quit the game. Did it open?"
            "$edition opened? $observed" | Add-Content -LiteralPath $notes
        }
        foreach ($question in 'Could you see the Mica background?', 'Did the Home title look like stencil lettering?', 'Does anything look wrong or unlike Windows?') {
            $observed = Read-Host $question
            "$question $observed" | Add-Content -LiteralPath $notes
        }
    } catch {
        $failure = $_
    } finally {
        if (-not $process.HasExited) { [void]$process.CloseMainWindow() }
        $closed = Complete-KitProcess $run $entry.Evidence 20
        $logs = Join-Path $homePath 'launcher\logs'
        if (Test-Path -LiteralPath $logs) {
            foreach ($log in Get-ChildItem -LiteralPath $logs -Filter '*.log' -File) {
                Save-Text $log.FullName (Join-Path $entry.Evidence $log.Name)
            }
        }
    }
    if ($failure) { throw $failure }
    if ($closed.TimedOut -or $closed.ExitCode -ne 0) { throw 'The launcher did not close cleanly.' }
}

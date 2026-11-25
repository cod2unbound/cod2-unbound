function Get-KitLauncherWindow($Process) {
    Wait-KitCondition {
        $Process.Refresh()
        if ($Process.HasExited) { throw "The launcher closed with exit code $($Process.ExitCode)." }
        if ($Process.MainWindowHandle -ne [IntPtr]::Zero) { $Process.MainWindowHandle }
    } 55 'the launcher window'
}

function Get-KitControls($Window, [string]$Value, [switch]$Name, [switch]$AllowOffscreen) {
    $root = if ($Window -is [Windows.Automation.AutomationElement]) { $Window }
        else { [Windows.Automation.AutomationElement]::FromHandle($Window) }
    $property = if ($Name) { [Windows.Automation.AutomationElement]::NameProperty }
        else { [Windows.Automation.AutomationElement]::AutomationIdProperty }
    $condition = [Windows.Automation.PropertyCondition]::new($property, $Value)
    $scope = [Windows.Automation.TreeScope]::Descendants
    foreach ($control in $root.FindAll($scope, $condition)) {
        if ($control.Current.IsEnabled -and ($AllowOffscreen -or -not $control.Current.IsOffscreen)) { $control }
    }
}

function Find-KitControl($Window, [string]$Value, [switch]$Name, [switch]$AllowOffscreen) {
    Get-KitControls $Window $Value -Name:$Name -AllowOffscreen:$AllowOffscreen | Select-Object -First 1
}

function Invoke-KitControl($Window, [string]$Value, [switch]$Name, [switch]$AllowOffscreen) {
    if ($Value -match '^(?:Use key from disc install|UseDiscKey|ImportInstall|Import.*(?:install|disc|retail)|From your Call of Duty 2 install)') {
        throw [ArgumentException]::new('Retail action is forbidden during kit automation.')
    }
    $action = Wait-KitCondition {
        foreach ($control in Get-KitControls $Window $Value -Name:$Name -AllowOffscreen:$AllowOffscreen) {
            foreach ($pattern in [Windows.Automation.InvokePattern]::Pattern, [Windows.Automation.SelectionItemPattern]::Pattern) {
                $candidate = $null
                if ($control.TryGetCurrentPattern($pattern, [ref]$candidate)) { return $candidate }
            }
        }
    } 12 "an action for launcher control $Value"
    if ($action -is [Windows.Automation.InvokePattern]) { $action.Invoke() } else { $action.Select() }
}

function Wait-KitHome([IntPtr]$Window) {
    Wait-KitCondition {
        (Find-KitControl $Window 'FindServerButton' -AllowOffscreen) -or
            (Find-KitControl $Window 'LaunchButton' -AllowOffscreen) -or
            (Find-KitControl $Window 'OpenGameMenuButton' -AllowOffscreen)
    } 45 'Home to open' | Out-Null
}

function Open-KitGameMenu([IntPtr]$Window, [switch]$UseJump) {
    Wait-KitHome $Window
    if (Find-KitControl $Window 'OpenGameMenuButton') {
        Invoke-KitControl $Window 'OpenGameMenuButton'
        'First-run OpenGameMenuButton' | Set-Content -LiteralPath (Join-Path $Results 'game-menu-route.txt')
    } elseif ($UseJump) {
        Open-KitJump $Window 'open game menu' 'Open Game Menu'
        [KitDesktop]::Tap(13)
        'Returning Home Jump to > Open Game Menu' | Set-Content -LiteralPath (Join-Path $Results 'game-menu-route.txt')
    } else {
        Focus-KitWindow $Window
        [KitDesktop]::Chord(17, 16, 13)
        'Returning Home Ctrl+Shift+Enter' | Set-Content -LiteralPath (Join-Path $Results 'game-menu-route.txt')
    }
}

function Open-KitConnect([IntPtr]$Window) {
    Focus-KitWindow $Window
    [KitDesktop]::Chord(17, 76)
    Wait-KitCondition { Find-KitControl $Window 'AddressBox' } 12 'the Connect to Address sheet' | Out-Null
}

function Open-KitJump([IntPtr]$Window, [string]$Query, [string]$Result) {
    Focus-KitWindow $Window
    [KitDesktop]::Chord(17, 75)
    Set-KitEdit $Window 'JumpBox' $Query
    Wait-KitCondition {
        $list = Find-KitControl $Window 'JumpList'
        if ($list) {
            $pattern = $list.GetCurrentPattern([Windows.Automation.SelectionPattern]::Pattern)
            $pattern.Current.GetSelection() | Where-Object { $_.Current.Name -eq $Result } | Select-Object -First 1
        }
    } 12 "the selected Jump result $Result" | Out-Null
}

function Open-KitSettings([IntPtr]$Window) {
    Invoke-KitControl $Window 'SettingsItem'
    $owner = [KitDesktop]::WindowPid($Window)
    Wait-KitCondition {
        foreach ($handle in [KitDesktop]::VisibleProcessWindows($owner)) {
            if ($handle -ne $Window -and (Find-KitControl $handle 'Panes')) { return $handle }
        }
    } 12 'the separate Settings window'
}

function Complete-KitLauncherSetup([IntPtr]$Window, [string]$Evidence) {
    $complete = Join-Path $LauncherHome 'launcher\.launcher-setup-complete'
    if (Test-Path -LiteralPath $complete) { return }
    foreach ($step in 'welcome','game-data','key','personalize','crash','usage','ready') {
        switch ($step) {
            'key' {
                Wait-KitCondition {
                    (Find-KitControl $Window 'CD key' -Name) -or
                    (Find-KitControl $Window ('Replace' + [char]0x2026) -Name)
                } 12 'the CD key page'
                $box = Find-KitControl $Window 'CD key' -Name
                if ($box) {
                    Focus-KitWindow $Window
                    $box.SetFocus()
                    [Windows.Forms.SendKeys]::SendWait('000000000000000086D3')
                }
                Focus-KitWindow $Window
                Save-KitWindow $Window (Join-Path $Evidence 'setup-key.png')
                Invoke-KitControl $Window 'SetupNext'
            }
            'personalize' {
                Wait-KitCondition {
                    Find-KitControl $Window 'How the game fills your screen' -Name -AllowOffscreen
                } 12 'personalization choices' | Out-Null
                Focus-KitWindow $Window
                Save-KitWindow $Window (Join-Path $Evidence 'setup-personalize.png')
                Invoke-KitControl $Window 'SetupNext'
            }
            { $_ -in 'usage','crash' } {
                Wait-KitCondition { Find-KitControl $Window 'DeclineReport' } 12 'the reporting choice' | Out-Null
                Focus-KitWindow $Window
                Save-KitWindow $Window (Join-Path $Evidence "setup-$step.png")
                Invoke-KitControl $Window 'DeclineReport'
            }
            default {
                Wait-KitCondition { Find-KitControl $Window 'SetupNext' } 12 'Setup Continue' | Out-Null
                Focus-KitWindow $Window
                Save-KitWindow $Window (Join-Path $Evidence "setup-$step.png")
                Invoke-KitControl $Window 'SetupNext'
            }
        }
        Start-Sleep -Milliseconds 550
    }
    Wait-KitCondition { Test-Path -LiteralPath $complete } 15 'setup completion' | Out-Null
}

function Set-KitLauncherProfile([string]$Edition, [string]$Renderer, [string]$Display = 'windowed', [string]$Resolution = '800x600') {
    $settings = New-Folder (Join-Path $LauncherHome 'launcher')
    [IO.File]::WriteAllText((Join-Path $settings 'data-path.txt'), $GamePath)
    [IO.File]::WriteAllText((Join-Path $KitProfile 'empty-releases.json'), '[]')
    $mode = if ($Renderer -eq 'dx7') { 'DirectX7' } else { 'Direct3D9' }
    @{
        engine=$Edition; windowsRenderer=$mode; fullscreen=$Display; resolution=$Resolution
        hideWhilePlaying=$false; rawMouse=$true
        advanced="logfile 2`nnet_ip 127.0.0.1`nnet_noipx 1`nnet_lanauthorize 0`ncl_allowDownload 0`nsv_allowDownload 0"
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $settings 'launcher-settings.json') -Encoding Ascii
    $activeProfile = Join-Path $LauncherHome 'main\players\active.txt'
    if (-not (Test-Path -LiteralPath $activeProfile)) { New-TestProfile $LauncherHome }
    $main = New-Folder (Join-Path $LauncherHome 'main')
    @('set developer 0','set logfile 2','set com_introPlayed 1',
        'set g_gametype dm','set r_aaSamples 1','set cl_punkbuster 0','set sv_punkbuster 0') |
        Set-Content -LiteralPath (Join-Path $main 'autoexec.cfg') -Encoding Ascii
}

function New-KitLauncherTestHome([string]$Source, [string]$PrivateRun) {
    $complete = Join-Path $Source 'launcher\.launcher-setup-complete'
    Stop-KitIfMissing $complete 'The launcher setup did not finish.'
    $testHome = Join-Path $PrivateRun 'launcher-profile'
    $settings = New-Folder (Join-Path $testHome 'launcher')
    Copy-Item -LiteralPath $complete -Destination $settings
    $testHome
}

function Stop-KitLauncher($Run, [string]$Evidence) {
    if (-not $Run.Process.HasExited) { [void]$Run.Process.CloseMainWindow() }
    Complete-KitProcess $Run $Evidence 12 | Out-Null
    $logRoot = Join-Path $LauncherHome 'launcher\logs'
    if (Test-Path -LiteralPath $logRoot) {
        foreach ($file in Get-ChildItem -LiteralPath $logRoot -File -Filter '*.log') {
            Save-Text $file.FullName (Join-Path $Evidence $file.Name)
        }
    }
}

function Invoke-InstalledKitClient([string]$Edition, [string]$Renderer) {
    Initialize-KitDesktop
    Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
    $exe = Get-KitStartTarget
    Set-KitLauncherProfile $Edition $Renderer
    $useJump = $Edition -eq 'original' -and $Renderer -eq 'dx7'
    if ($useJump -or ($Edition -eq 'cod2x' -and $Renderer -eq 'dx9')) {
        [IO.File]::WriteAllText((Join-Path $LauncherHome 'launcher\launcher-library.json'),
            '{"recent":["127.0.0.1:9"]}')
    }
    $console = Join-Path $LauncherHome 'main\console_mp.log'
    if (Test-Path -LiteralPath $console) { Remove-Item -LiteralPath $console }
    $screens = Join-Path $LauncherHome 'main\screenshots'
    if (Test-Path -LiteralPath $screens) {
        Get-ChildItem -LiteralPath $screens -Filter 'kit-*.jpg' -File | Remove-Item
    }
    $trace = New-KitFrameTrace $Results
    $settings = Get-KitLauncherSettings
    $settings.COD2_FRAME_TRACE_FILE = $trace.Path
    $run = Start-KitProcess $exe '' $LauncherHome $settings
    $game = $null
    $window = [IntPtr]::Zero
    try {
        $launcher = Get-KitLauncherWindow $run.Process
        Complete-KitLauncherSetup $launcher $Results
        $binary = Get-Binary $Edition -Root $InstallRoot
        $start = Get-Date
        Open-KitGameMenu $launcher -UseJump:$useJump
        $game = Wait-KitCondition {
            Get-Process -Name 'cod2_win32' -ErrorAction SilentlyContinue |
                Where-Object { $_.Path -ieq $binary -and $_.StartTime -ge $start -and $_.SessionId -eq $run.Process.SessionId } |
                Select-Object -First 1
        } 40 'the installed game process'
        $window = Get-KitGameWindow $game
        Invoke-MapPictures $LauncherHome $Results $window $Renderer $trace $game -FromLauncher
    } finally {
        if ($game -and -not $game.HasExited) {
            if ($window -ne [IntPtr]::Zero) {
                try { Send-KitCommand $window '/quit' } catch { [void]$game.CloseMainWindow() }
            }
            if (-not $game.WaitForExit(12000)) {
                $game.Kill()
                [void]$game.WaitForExit(5000)
            }
        }
        Save-GameEvidence $LauncherHome $Results
        $adapterLog = Join-Path $Results 'boot-console.txt'
        if (-not (Test-Path -LiteralPath $adapterLog)) { $adapterLog = $console }
        Save-KitFrameTrace $trace $Results $adapterLog
        Stop-KitLauncher $run $Results
        [void](Save-KitFirewall 'after-installed-client')
    }
}

function Invoke-KitFaults {
    Stop-KitIfNoGame
    $exe = Get-KitStartTarget
    $LauncherHome = New-KitLauncherTestHome $LauncherHome $Private
    Initialize-KitDesktop
    Wait-KitCondition { [KitDesktop]::QuietDesktop(2500) } 30 'a quiet desktop for the freeze check' | Out-Null
    Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
    Set-KitLauncherProfile 'original' 'dx9'
    $settingsPath = Join-Path $LauncherHome 'launcher\launcher-settings.json'
    $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    $settings | Add-Member -NotePropertyName sendCrashReports -NotePropertyValue $true -Force
    $settings | Add-Member -NotePropertyName crashReportsAsked -NotePropertyValue $true -Force
    # The engine registers its developer commands only at startup.
    $settings.advanced += "`ndeveloper 1"
    $settings | ConvertTo-Json | Set-Content -LiteralPath $settingsPath -Encoding Ascii
    $log = Join-Path $LauncherHome 'main\console_mp.log'
    if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log }
    $entry = New-RunContext 'report-sink'
    $portFile = Join-Path $entry.Folder 'port.txt'
    $ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $argsText = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}" -Ready "{1}"' -f (Join-Path $Kit 'Local-Report.ps1'),$portFile
    $sink = Start-KitProcess $ps $argsText $entry.Folder -NoWindow
    $launcher = $game = $null
    $window = [IntPtr]::Zero
    try {
        $port = Wait-KitCondition {
            if (Test-Path -LiteralPath $portFile) { Get-Content -LiteralPath $portFile -Raw }
        } 12 'the local report receiver'
        $overrides = Get-KitLauncherSettings
        $overrides.COD2_CRASH_URL = "http://127.0.0.1:$port/"
        $overrides.COD2_USAGE_URL = $overrides.COD2_CRASH_URL
        $overrides.COD2_CRASH_NOPOPUP = '1'
        $launcher = Start-KitProcess $exe '' $LauncherHome $overrides
        $app = Get-KitLauncherWindow $launcher.Process
        $since = Get-Date
        Open-KitGameMenu $app
        $game = Get-KitOwnedGame 'original' $since $launcher.Process.SessionId
        $window = Get-KitGameWindow $game
        Start-Sleep -Seconds 4
        try { Focus-KitWindow $window }
        catch {
            Wait-KitCondition { [KitDesktop]::QuietDesktop(2500) } 20 'a quiet desktop before retrying focus' | Out-Null
            try { Focus-KitWindow $window }
            catch {
                $front = [KitDesktop]::GetForegroundWindow()
                if ($front -eq $app) { Save-KitWindow $app (Join-Path $Results 'fault-foreground-launcher.png') }
                "Foreground window class: $([KitDesktop]::WindowClass($front)). A picture is kept only for the kit launcher." |
                    Set-Content -LiteralPath (Join-Path $Results 'fault-foreground.txt') -Encoding UTF8
                throw
            }
        }
        Enable-KitGameConsole $window $LauncherHome $Results | Out-Null
        Send-KitCommand $window '/toggleconsole; devmap mp_toujane'
        Wait-KitCondition {
            (Test-Path -LiteralPath $log) -and (Select-String -LiteralPath $log -SimpleMatch 'CL_InitCGame:' -Quiet)
        } 50 'Toujane for the fault check' | Out-Null
        $shot = Join-Path $LauncherHome 'main\screenshots\kit-freeze.jpg'
        if (Test-Path -LiteralPath $shot) { Remove-Item -LiteralPath $shot }
        $clock = [Diagnostics.Stopwatch]::StartNew()
        Send-KitCommand $window '/toggleconsole; freeze 3; screenshotJpeg kit-freeze'
        Wait-KitCondition { Test-Path -LiteralPath $shot } 16 'a picture after the freeze' | Out-Null
        "Freeze recovered and wrote a picture in $($clock.ElapsedMilliseconds) ms." |
            Set-Content -LiteralPath (Join-Path $Results 'fault.txt')
        Send-KitCommand $window '/crash'
        if (-not $game.WaitForExit(18000)) { throw 'The crash command did not end the game within 18 seconds.' }
        "Game exit $($game.ExitCode)" | Add-Content -LiteralPath (Join-Path $Results 'fault.txt')
        Start-Sleep -Seconds 5
        foreach ($file in Get-ChildItem -LiteralPath $LauncherHome -File -Filter '*crash*.txt') {
            if ($file.LastWriteTime -ge $since) { Save-Text $file.FullName (Join-Path $Results $file.Name) }
        }
    } finally {
        Stop-KitOwnedGame $game $window
        if ($launcher) { Stop-KitLauncher $launcher $Results }
        if (-not $sink.Process.HasExited) { $sink.Process.Kill() }
        Complete-KitProcess $sink $entry.Evidence 8 | Out-Null
        Save-GameEvidence $LauncherHome $Results
    }
    if (-not (Select-String -LiteralPath (Join-Path $entry.Evidence 'stdout.txt') -Pattern '^POST loopback' -Quiet)) {
        throw 'The test game exited, but the local receiver recorded no report.'
    }
}

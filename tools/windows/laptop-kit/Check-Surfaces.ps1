function Focus-KitPictureWindow([IntPtr]$Window, [uint32]$Owner) {
    Wait-KitCondition { [KitDesktop]::TryFocusPictureWindow($Window, $Owner) } 12 'the owned launcher picture window to take focus' | Out-Null
}

function Save-KitSurface([IntPtr]$Window, [string]$Name, [string[]]$Controls, [string[]]$Names = @(), [uint32]$PictureOwner = 0) {
    foreach ($control in $Controls) {
        Wait-KitCondition { Find-KitControl $Window $control -AllowOffscreen } 12 $control | Out-Null
    }
    foreach ($control in $Names) {
        Wait-KitCondition { Find-KitControl $Window $control -Name -AllowOffscreen } 12 $control | Out-Null
    }
    $path = Join-Path $Results "$Name.png"
    if ($PictureOwner) {
        Focus-KitPictureWindow $Window $PictureOwner
        try { Save-KitWindow $Window $path }
        catch {
            if ([KitDesktop]::GetForegroundWindow() -eq $Window) { throw }
            Focus-KitPictureWindow $Window $PictureOwner
            Save-KitWindow $Window $path
        }
    } else {
        Focus-KitWindow $Window
        Save-KitWindow $Window $path
    }
    "$Name IDs [$($Controls -join ', ')] names [$($Names -join ', ')]" |
        Add-Content -LiteralPath (Join-Path $Results 'automation-controls.txt') -Encoding UTF8
}

function Invoke-KitWindows10Startup {
    Initialize-KitDesktop
    Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
    $exe = Get-KitStartTarget
    [IO.File]::WriteAllText((Join-Path $KitProfile 'empty-releases.json'), '[]')
    foreach ($appearance in 'dark','light') {
        foreach ($route in 'setup','first-home','returning-home') {
            $name = "$appearance-$route"
            $LauncherHome = New-Folder (Join-Path $Private $name)
            $profile = New-Folder (Join-Path $LauncherHome 'launcher')
            [IO.File]::WriteAllText((Join-Path $profile 'launcher-settings.json'), "{`"appearance`":`"$appearance`"}")
            if ($route -ne 'setup') {
                [IO.File]::WriteAllText((Join-Path $profile '.launcher-setup-complete'), "prepared`n")
            }
            if ($GamePath) {
                [IO.File]::WriteAllText((Join-Path $profile 'data-path.txt'), $GamePath)
            }
            if ($route -eq 'returning-home') {
                [IO.File]::WriteAllText((Join-Path $profile 'launcher-library.json'), '{"recent":["127.0.0.1:9"]}')
            }
            $settings = Get-KitLauncherSettings
            $settings.COD2_UNBOUND_TEST_WINDOWS_CREDENTIALS = '0'
            $settings.COD2_UNBOUND_TEST_FORCE_WINDOWS10 = '1'
            $run = Start-KitProcess $exe '' $LauncherHome $settings
            $trace = Join-Path $LauncherHome 'startup-timing.log'
            try {
                $app = Get-KitLauncherWindow $run.Process
                $id = $run.Process.Id
                Wait-KitCondition {
                    if ($run.Process.HasExited) { throw "The launcher exited with $($run.Process.ExitCode)." }
                    (Test-Path -LiteralPath $trace) -and
                        (Select-String -LiteralPath $trace -SimpleMatch " $id first content frame" -Quiet)
                } 45 "$name content" | Out-Null
                if (-not (Select-String -LiteralPath $trace -SimpleMatch " $id Windows 10 solid backdrop selected" -Quiet)) {
                    throw 'The launcher did not select the solid Windows 10 backdrop.'
                }
                $control = switch ($route) {
                    'setup' { 'SetupNext' }
                    'first-home' { 'OpenGameMenuButton' }
                    'returning-home' { 'LaunchButton' }
                }
                [KitDesktop]::SizeSurfaceWindow($app, 1440, 900)
                Save-KitSurface $app $name @($control) -PictureOwner $run.Process.Id
            } finally {
                if (-not $run.Process.HasExited) { [void]$run.Process.CloseMainWindow() }
                $done = Complete-KitProcess $run (New-Folder (Join-Path $Results $name)) 12
                Save-Text $trace (Join-Path $Results "$name-startup.log")
                if ($done.TimedOut -or $done.ExitCode -ne 0) { throw "$name did not close cleanly." }
            }
        }
    }
}

function Invoke-KitJumpPointer([ValidateRange(1,5)][int]$Rounds = 1, [switch]$StatusDuringPress) {
    if ($Rounds -gt 1) {
        $receiptRoot = $Results
        $privateRoot = $Private
        $failures = foreach ($round in 1..$Rounds) {
            $Results = New-Folder (Join-Path $receiptRoot "round-$round")
            $Private = New-Folder (Join-Path $privateRoot "round-$round")
            try { Invoke-KitJumpPointer -StatusDuringPress }
            catch { "Round ${round}: $($_.Exception.Message)" }
        }
        if ($failures) { throw ($failures -join ' ') }
        return
    }
    Stop-KitIfNoGame
    Initialize-KitDesktop
    Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
    $exe = Get-KitStartTarget
    $setupHome = $LauncherHome
    $cases = @(
        @{ Query='library'; Name='Library'; Witness='DemosSection' }
        @{ Query='configs'; Name='Configs'; Witness='ConfigsCheckConfig' }
        @{ Query='keyboard shortcuts'; Name='Keyboard Shortcuts'; Witness='PrimaryButton'; Close='Done' }
        @{ Query='connect to address'; Name='Connect to Address'; Witness='AddressBox'; Close='Cancel' }
    )
    $appearanceFailures = foreach ($theme in 'dark','light') {
        $LauncherHome = New-KitLauncherTestHome $setupHome (New-Folder (Join-Path $Private $theme))
        Set-KitLauncherProfile 'original' 'dx9'
        $preferences = Join-Path $LauncherHome 'launcher\launcher-settings.json'
        $settings = Get-Content -LiteralPath $preferences -Raw | ConvertFrom-Json
        $settings | Add-Member -NotePropertyName appearance -NotePropertyValue $theme
        $settings | Add-Member -NotePropertyName refreshServersOnOpen -NotePropertyValue $false -Force
        $settings | ConvertTo-Json | Set-Content -LiteralPath $preferences -Encoding ASCII
        $pictures = New-Folder (Join-Path $Results $theme)
        $launcherSettings = Get-KitLauncherSettings
        $address = '127.0.0.1:9'
        $master = $run = $null
        $timing = Join-Path $LauncherHome 'startup-timing.log'
        $library = Join-Path $LauncherHome 'launcher\launcher-library.json'
        try {
            if ($StatusDuringPress) {
                $fixture = New-RunContext 'jump-refresh-fixture'
                $port = Join-Path $fixture.Folder 'master-port.txt'
                $ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
                $arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}" -Stage "{1}" -JumpRace' -f
                    (Join-Path $Kit 'Local-Refresh-Fixture.ps1'),$fixture.Folder
                $master = Start-KitProcess $ps $arguments $fixture.Folder -NoWindow
                $launcherSettings.COD2_UNBOUND_TEST_MASTER_PORT = Wait-KitCondition {
                    if ($master.Process.HasExited) { throw 'The Jump to status fixture exited.' }
                    if (Test-Path -LiteralPath $port) { [IO.File]::ReadAllText($port) }
                } 15 'the Jump to status fixture'
                $address = [IO.File]::ReadAllText((Join-Path $fixture.Folder 'server-address.txt'))
            }
            [IO.File]::WriteAllText($library, (@{recent=@($address)} | ConvertTo-Json -Compress))
            $run = Start-KitProcess $exe '' $LauncherHome $launcherSettings
            $app = Get-KitLauncherWindow $run.Process
            Wait-KitHome $app
            [KitDesktop]::SizeSurfaceWindow($app, 1440, 900)
            foreach ($case in ($cases + @{ Query=$address; Name=$address; Server=$true })) {
                Focus-KitPictureWindow $app $run.Process.Id
                $whilePressed = $null
                if ($case.Server -and $StatusDuringPress) {
                    Invoke-KitControl $app 'ServersItem'
                    $refresh = Wait-KitCondition { Find-KitControl $app 'Refresh server list' -Name } 12 'the Servers refresh button'
                    $refresh.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
                    Wait-KitCondition { Find-KitControl $app 'RefreshMessage' } 6 'the refresh to start' | Out-Null
                    $pressTrace = Join-Path $pictures 'server-press.txt'
                    $sendStatus = Join-Path $fixture.Folder 'send-status.txt'
                    $whilePressed = [Action]{
                        "Pointer down: $([DateTime]::UtcNow.ToString('o'))" | Set-Content -LiteralPath $pressTrace
                        [IO.File]::WriteAllText($sendStatus, '')
                        Wait-KitCondition {
                            Select-String -LiteralPath $library -SimpleMatch 'Fixture 000' -Quiet
                        } 12 'the local server status to reach the library with the pointer held' | Out-Null
                        "Status arrived before release: $([DateTime]::UtcNow.ToString('o'))" | Add-Content -LiteralPath $pressTrace
                        $deferred = @(Select-String -LiteralPath $timing -SimpleMatch 'jump results deferred for pointer').Count
                        Wait-KitCondition {
                            @(Select-String -LiteralPath $timing -SimpleMatch 'jump results deferred for pointer').Count -gt $deferred
                        } 5 'the status refresh to defer replacing the pressed Jump to result' | Out-Null
                    }
                }
                [KitDesktop]::Chord(17, 75)
                Set-KitEdit $app 'JumpBox' $case.Query
                $button = Wait-KitCondition {
                    $list = Find-KitControl $app 'JumpList'
                    if ($list) {
                        $kind = [Windows.Automation.PropertyCondition]::new(
                            [Windows.Automation.AutomationElement]::ControlTypeProperty,
                            [Windows.Automation.ControlType]::Button)
                        $list.FindAll([Windows.Automation.TreeScope]::Descendants,$kind) | Where-Object {
                            $_.Current.IsEnabled -and -not $_.Current.IsOffscreen -and
                                $_.Current.Name -like "$($case.Name)*"
                        } | Select-Object -First 1
                    }
                } 10 "the $($case.Name) Jump to button"
                $file = $case.Query -replace '[^a-z0-9]+','-'
                Save-KitWindow $app (Join-Path $pictures "$file-before.png")
                $area = $button.Current.BoundingRectangle
                if ($area.IsEmpty -or -not [KitDesktop]::ClickSurface($app,
                        [int]($area.Left + $area.Width/2), [int]($area.Top + $area.Height/2), $true, $whilePressed)) {
                    throw "The $($case.Name) Jump to button could not receive a pointer click."
                }
                Wait-KitCondition {
                    if (Find-KitControl $app 'JumpBox') { return }
                    if (-not $case.Server) { return Find-KitControl $app $case.Witness }
                    $row = Get-KitSelectedServerRow $app
                    $detail = Find-KitControl $app 'DetailAddress' -AllowOffscreen
                    $name = if ($StatusDuringPress) { 'Fixture 000' } else { $address }
                    if ($row.Name -like "*$name*" -and $detail.Current.Name -like "*$address*") {
                        "Selected: $($row.Name). Details: $($detail.Current.Name)." |
                            Set-Content -LiteralPath (Join-Path $pictures 'server-selection.txt') -Encoding UTF8
                        return $true
                    }
                } 12 "the result of clicking $($case.Name)" {
                    if ([KitDesktop]::GetForegroundWindow() -eq $app -and -not [KitDesktop]::SecurityPrompt()) {
                        Save-KitWindow $app (Join-Path $pictures "$file-after-timeout.png")
                    }
                } | Out-Null
                Save-KitWindow $app (Join-Path $pictures "$file-after.png")
                "$theme $($case.Name): pointer click opened the result." |
                    Add-Content -LiteralPath (Join-Path $Results 'pointer-results.txt') -Encoding UTF8
                if ($case.Close) {
                    Invoke-KitControl $app $case.Close -Name
                    Wait-KitCondition { -not (Find-KitControl $app $case.Witness) } 8 'the Jump to action sheet to close' | Out-Null
                }
            }
        } catch {
            "$theme appearance. $($_.Exception.Message)"
        } finally {
            try {
                if ($run) { Stop-KitLauncher $run $pictures }
                Save-Text $timing (Join-Path $pictures 'startup-timing.log')
            } finally {
                if ($master) {
                    [IO.File]::WriteAllText((Join-Path $fixture.Folder 'master-stop.txt'), '')
                    Complete-KitProcess $master $fixture.Evidence 5 | Out-Null
                    Save-Text (Join-Path $fixture.Folder 'master-receipt.txt') (Join-Path $pictures 'status-fixture.txt')
                }
            }
        }
    }
    if ($appearanceFailures) { throw ($appearanceFailures -join ' ') }
}

function Invoke-KitSurface([string]$Page) {
    if ($Page -ne 'home' -and $Page -ne 'hosting') {
        Invoke-KitSurfaceRun $Page
        return
    }
    if ($Page -eq 'home') {
        Invoke-KitSurfaceRun 'home' -Variant 'first'
        Invoke-KitSurfaceRun 'home' -Variant 'returning'
        return
    }
    $failures = foreach ($theme in 'dark', 'light') {
        if ($theme -eq 'light') { Wait-KitCooling }
        try { Invoke-KitSurfaceRun 'hosting' -Variant $theme | Out-Null }
        catch { "$theme appearance. $($_.Exception.Message)" }
    }
    if ($failures) { throw ($failures -join ' ') }
}

function Invoke-KitSurfaceRun([string]$Page, [string]$Variant) {
    Stop-KitIfNoGame
    Initialize-KitDesktop
    Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
    $exe = Get-KitStartTarget
    if ($Variant) {
        $Results = New-Folder (Join-Path $Results $Variant)
        $Private = New-Folder (Join-Path $Private $Variant)
    }
    $LauncherHome = New-KitLauncherTestHome $LauncherHome $Private
    Set-KitLauncherProfile 'original' 'dx9'
    if ($Page -eq 'home' -and $Variant -eq 'returning') {
        [IO.File]::WriteAllText((Join-Path $LauncherHome 'launcher\launcher-library.json'),
            '{"recent":["127.0.0.1:9"]}')
    }
    if ($Page -eq 'hosting') {
        $settingsFile = Join-Path $LauncherHome 'launcher\launcher-settings.json'
        $settings = Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json
        $settings | Add-Member -NotePropertyName appearance -NotePropertyValue $Variant
        $settings | ConvertTo-Json | Set-Content -LiteralPath $settingsFile -Encoding ASCII
    }
    if ($Page -eq 'configs') {
        $players = Join-Path $LauncherHome 'main\players'
        Rename-Item -LiteralPath (Join-Path $players 'kit') -NewName 'Main'
        [IO.File]::WriteAllText((Join-Path $players 'active.txt'), 'Main')
        [IO.File]::WriteAllText((Join-Path $players 'Main\studio.json'),
            '{"displayName":"Main","lastImportedValues":{},"locks":[],"notes":{},"origin":{"kind":"created"},"schemaVersion":1}')
    }
    if ($Page -eq 'library') {
        $demo = Join-Path $Kit 'frame-references\original\smoke-mp_toujane.dm_1'
        $picture = Join-Path $Kit 'frame-references\original\dx9\dx9-mp_toujane-00-windows.png'
        Copy-Item -LiteralPath $demo -Destination (New-Folder (Join-Path $LauncherHome 'main\demos'))
        Copy-Item -LiteralPath $picture -Destination (New-Folder (Join-Path $LauncherHome 'main\screenshots'))
        $stats = New-Folder (Join-Path $LauncherHome 'main\stats')
        $summary = @{
            schemaVersion=1; endedAt=[DateTime]::UtcNow.ToString('o'); endReason='intermission'
            map='mp_toujane'; mode='dm'; durationSeconds=60; serverAddress='127.0.0.1:29792'
            ruleHints=@{}; frameCount=0; capSampleFrames=0; capFPS=0; pingSamples=0
            packetsReceived=0; packetsLost=0; snapshotsReceived=0
        }
        $summary | ConvertTo-Json -Compress |
            Set-Content -LiteralPath (Join-Path $stats 'matches.jsonl') -Encoding ASCII
    }
    $run = Start-KitProcess $exe '' $LauncherHome (Get-KitLauncherSettings)
    $hostedProcess = $null
    try {
        $app = Get-KitLauncherWindow $run.Process
        Wait-KitHome $app
        [KitDesktop]::SizeSurfaceWindow($app, 1440, 900)
        if ($Page -ne 'home') { Focus-KitWindow $app }
        switch ($Page) {
            'home' {
                if ($Variant -eq 'first') {
                    Save-KitSurface $app 'first-home' @('OpenGameMenuButton', 'FindServerButton') -PictureOwner $run.Process.Id
                    $news = Wait-KitCondition {
                        foreach ($id in 'SeeAllNews', 'NewsChip') {
                            if (Find-KitControl $app $id) { return $id }
                        }
                    } 12 'the visible Home news action'
                    Invoke-KitControl $app $news
                    Save-KitSurface $app 'news' @('CloseButton') -PictureOwner $run.Process.Id
                    Invoke-KitControl $app 'CloseButton'
                } else {
                    Save-KitSurface $app 'returning-home' @('LaunchButton') -PictureOwner $run.Process.Id
                    Open-KitJump $app 'open game menu' 'Open Game Menu' | Out-Null
                    Save-KitSurface $app 'game-menu-jump' @('JumpBox', 'JumpList')
                    [KitDesktop]::Tap(27)
                    Wait-KitCondition { -not (Find-KitControl $app 'JumpBox') } 12 'Jump to to close' | Out-Null
                    Focus-KitPictureWindow $app $run.Process.Id
                    Open-KitConnect $app
                    Save-KitSurface $app 'connect' @('AddressBox', 'CloseButton')
                    Invoke-KitControl $app 'CloseButton'
                }
            }
            'jump' {
                Open-KitJump $app 'keyboard shortcuts' 'Keyboard Shortcuts' | Out-Null
                Save-KitSurface $app 'jump-search' @('JumpBox', 'JumpList')
                (Get-KitJumpAction $app 'Keyboard Shortcuts').Invoke()
                $sheet = Wait-KitCondition { Find-KitControl $app 'KeyboardShortcutsDialog' } 12 'the shortcuts sheet'
                Save-KitSurface $app 'shortcuts' @('PrimaryButton')
                Invoke-KitControl $sheet 'PrimaryButton'
            }
            'hosting' {
                Invoke-KitControl $app 'ServersItem'
                Invoke-KitControl $app 'HostingTab'
                Invoke-KitControl $app 'HostingEmptyNewServer'
                Set-KitEdit $app 'Server name' 'Kit Export' -Name
                Invoke-KitControl $app 'Start server' -Name
                $room = Wait-KitCondition { Find-KitHostedServer 'Kit Export' } 20 'the disposable Hosting room'
                Wait-KitCondition { (Request-KitServer $room.Port 'getstatus Kit') -match 'Kit Export' } 45 'the Hosting room to start' | Out-Null
                $dedicated = Get-Binary 'original' -Server -Root $InstallRoot
                $child = Get-CimInstance Win32_Process -Filter "ParentProcessId=$($run.Process.Id)" |
                    Where-Object { $_.ExecutablePath -ieq $dedicated -and $_.CommandLine -like "*$($room.Id)*" } |
                    Select-Object -First 1
                if ($child) {
                    $candidate = [Diagnostics.Process]::GetProcessById($child.ProcessId)
                    $null = $candidate.Handle
                    if ($candidate.Path -ieq $dedicated -and
                        [Math]::Abs(($candidate.StartTime - $child.CreationDate).TotalMilliseconds) -lt 1) {
                        $hostedProcess = $candidate
                    } else { $candidate.Dispose() }
                }
                if (-not $hostedProcess) { throw 'The Hosting picture check could not verify its own server process.' }
                Invoke-KitHostingExport $app (New-Folder (Join-Path $Private 'export'))
            }
            'configs' {
                [KitDesktop]::Chord(17, 52)
                Save-KitSurface $app 'config-landing' @('Loadouts', 'AddConfig')
                $loadout = Wait-KitCondition {
                    Find-KitControl $app 'ConfigsLoadout-Main'
                } 12 'the Main loadout row'
                $loadout.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
                Wait-KitCondition { Find-KitControl $app 'StudioSettings' } 12 'the Main loadout' | Out-Null
                Invoke-KitControl $app 'Binds' -Name
                Save-KitSurface $app 'config-binds' @() @('Find an action or key')
                Invoke-KitControl $app 'History' -Name
                Save-KitSurface $app 'config-history' @() @('Save a snapshot now')
                Invoke-KitControl (Find-KitControl $app 'DiscoverPlaces') 'Check a config' -Name
                Save-KitSurface $app 'config-checker' @() @('Check a loadout')
            }
            'library' {
                Invoke-KitControl $app 'LibraryItem'
                Save-KitSurface $app 'library-demos' @('Search','ShowFolder','Files')
                Invoke-KitControl $app 'MatchesSection'
                Save-KitSurface $app 'library-matches' @('Matches') @(('Toujane ' + [char]0x00b7 + ' Deathmatch'))
                Invoke-KitControl $app 'ScreenshotsSection'
                Save-KitSurface $app 'library-screenshots' @('Search','ShowFolder','Screenshots')
            }
            'settings' {
                $settingsWindow = Open-KitSettings $app
                $panes = Find-KitControl $settingsWindow 'Panes'
                try {
                    Invoke-KitControl $panes 'Display' -Name
                    Save-KitSurface $settingsWindow 'settings-display' @('Panes') @('Mode','Graphics')
                    Invoke-KitControl $panes 'About' -Name
                    $report = 'Report a Problem' + [char]0x2026
                    Save-KitSurface $settingsWindow 'settings-about' @('Panes') @($report,'Acknowledgements')
                    Invoke-KitControl $settingsWindow 'SettingsReportProblem'
                    $reportWindow = Wait-KitCondition {
                        foreach ($candidate in $settingsWindow, $app) {
                            if ((Find-KitControl $candidate 'CloseButton' -AllowOffscreen) -and
                                (Find-KitControl $candidate 'Copy details' -Name -AllowOffscreen)) {
                                return $candidate
                            }
                        }
                    } 12 'the Report a Problem sheet'
                    Save-KitSurface $reportWindow 'problem-report' @('CloseButton') @('Copy details') -PictureOwner $run.Process.Id
                    Invoke-KitControl $reportWindow 'CloseButton'
                } finally { [KitDesktop]::CloseOwnedWindow($settingsWindow) }
            }
        }
    } finally {
        if ($Page -eq 'hosting') {
            if ($hostedProcess) {
                try {
                    # This step checks the export and folder controls. The
                    # separate Hosting match step proves graceful shutdown.
                    if (-not $hostedProcess.HasExited) {
                        $hostedProcess.Kill()
                        if (-not $hostedProcess.WaitForExit(5000)) { throw 'The owned picture fixture server did not exit.' }
                    }
                } finally { $hostedProcess.Dispose() }
            }
            $logs = Join-Path $LauncherHome 'hosting\servers'
            foreach ($file in Get-ChildItem -LiteralPath $logs -Recurse -File -Filter 'engine-*.log' -ErrorAction SilentlyContinue) {
                Save-KitEngineAudit (Read-KitText $file.FullName) $Results
                Save-Text $file.FullName (Join-Path $Results $file.Name)
            }
        }
        Stop-KitLauncher $run $Results
    }
}

function Find-KitHostingPasswordToggle($Root) {
    Get-KitControls $Root 'Passwords' -Name |
        Where-Object { $_.GetCurrentPropertyValue([Windows.Automation.AutomationElement]::IsTogglePatternAvailableProperty) } |
        Select-Object -First 1
}

function Find-KitHostingExportDialog([IntPtr]$App) {
    # Passwords is owned by this sheet and exposed as a toggle. There can
    # be unrelated Cancel buttons under the launcher window.
    $toggle = Find-KitHostingPasswordToggle $App
    if (-not $toggle) { return }
    $walker = [Windows.Automation.TreeWalker]::RawViewWalker
    for ($node = $walker.GetParent($toggle); $node; $node = $walker.GetParent($node)) {
        if ($node.Current.ProcessId -ne [KitDesktop]::WindowPid($App) -or
            $node.Current.NativeWindowHandle -eq $App.ToInt64()) { break }
        if ($node.Current.Name -like 'Export * for another PC') { return $node }
    }
    throw 'The Hosting password toggle has no export dialog ancestor.'
}

function Find-KitExportPicker([IntPtr]$App) {
    $front = [KitDesktop]::GetForegroundWindow()
    if ($front -eq [IntPtr]::Zero -or $front -eq $App -or
        -not [KitDesktop]::IsOwnedWindow($front, $App)) { return }
    $root = [Windows.Automation.AutomationElement]::FromHandle($front)
    if ($root.Current.Name -eq 'Choose where to write the server files' -and
        (Find-KitControl $root '1') -and (Find-KitControl $root '2')) { return $root }
}

function Wait-KitHostingExport([IntPtr]$App, [string]$Route, [int]$Seconds = 12) {
    $surface = Wait-KitCondition {
        $picker = Find-KitExportPicker $App
        if ($picker) { return @{ Kind='picker'; Root=$picker } }
        $dialog = Find-KitHostingExportDialog $App
        if ($dialog) { return @{ Kind='dialog'; Root=$dialog } }
    } $Seconds "the Hosting export surface opened by $Route"
    Save-KitHostingExportPeers $surface.Root $Route $surface.Kind
    $surface
}

function Save-KitHostingExportPeers($Root, [string]$Route, [string]$Kind) {
    $peers = foreach ($key in 'Passwords','PrimaryButton','CloseButton','1148','1','2') {
        $peer = if ($key -eq 'Passwords') { Find-KitHostingPasswordToggle $Root } else { Find-KitControl $Root $key }
        if ($peer) { "$key=$($peer.Current.ControlType.ProgrammaticName)/$($peer.Current.AutomationId)" }
    }
    "route=$Route kind=$Kind type=$($Root.Current.ControlType.ProgrammaticName) id=$($Root.Current.AutomationId) peers=$($peers -join ', ')" |
        Add-Content -LiteralPath (Join-Path $Results 'hosting-export-peers.txt') -Encoding UTF8
}

function Select-KitExportFolder($Picker, [string]$Destination) {
    $window = [IntPtr]$Picker.Current.NativeWindowHandle
    if ($window -eq [IntPtr]::Zero -or [KitDesktop]::GetForegroundWindow() -ne $window) {
        throw 'The launcher folder picker is no longer in front.'
    }
    $marker = 'Kit target ' + [guid]::NewGuid().ToString('N')
    [void][IO.Directory]::CreateDirectory((Join-Path $Destination $marker))
    # Alt+D is the native folder picker's address shortcut on both Windows versions.
    [KitDesktop]::Chord(18, 68)
    $keys = [regex]::Replace($Destination, '[+^%~(){}\[\]]', [Text.RegularExpressions.MatchEvaluator]{
        param($match)
        '{' + $match.Value + '}'
    })
    [Windows.Forms.SendKeys]::SendWait($keys)
    [KitDesktop]::Tap(13)
    Wait-KitCondition {
        if ([KitDesktop]::GetForegroundWindow() -ne $window) { throw 'The folder picker lost foreground.' }
        Find-KitControl $Picker $marker -Name
    } 10 'the folder picker to show the marker in the private destination' | Out-Null
    'The folder picker displayed the private destination marker.' |
        Add-Content -LiteralPath (Join-Path $Results 'hosting-export-peers.txt') -Encoding UTF8
    $commit = Wait-KitCondition {
        $button = Find-KitControl $Picker '1'
        if ($button -and -not $button.Current.BoundingRectangle.IsEmpty) { return $button }
        foreach ($name in 'Select Folder', 'Open') {
            $button = Find-KitControl $Picker $name -Name
            if ($button -and -not $button.Current.BoundingRectangle.IsEmpty) { return $button }
        }
    } 10 'the folder picker to expose its selection button'
    if ([KitDesktop]::GetForegroundWindow() -ne $window) { throw 'The folder picker lost foreground.' }
    "picker commit=$($commit.Current.ControlType.ProgrammaticName)/$($commit.Current.AutomationId)" |
        Add-Content -LiteralPath (Join-Path $Results 'hosting-export-peers.txt') -Encoding UTF8
    $action = $null
    if ($commit.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$action)) {
        $action.Invoke()
    } else {
        $bounds = $commit.Current.BoundingRectangle
        if ($bounds.IsEmpty -or $bounds.Width -le 0 -or $bounds.Height -le 0) {
            throw 'The folder picker selection button has no visible bounds.'
        }
        $cursor = [Windows.Forms.Cursor]::Position
        try {
            [Windows.Forms.Cursor]::Position = [Drawing.Point]::new([int]($bounds.X + $bounds.Width / 2),
                [int]($bounds.Y + $bounds.Height / 2))
            if ([KitDesktop]::GetForegroundWindow() -ne $window) { throw 'The folder picker lost foreground.' }
            [KitDesktop]::ClickButton($false)
            [KitDesktop]::ClickButton($true)
        } finally { [Windows.Forms.Cursor]::Position = $cursor }
    }
}

function Invoke-KitHostingExport([IntPtr]$App, [string]$Destination) {
    $row = Wait-KitCondition { Find-KitControl $App 'Kit Export' -Name } 12 'the saved Hosting row'
    $row.SetFocus()
    [KitDesktop]::Chord(16, 121)
    Wait-KitCondition { Find-KitControl $App 'HostingRowExport' } 12 'the Hosting row menu' | Out-Null
    # Keep focus in the keyboard-opened menu for the arrow and Enter input.
    Save-KitWindow $App (Join-Path $Results 'hosting-row-menu.png')
    Wait-KitCondition {
        if ([KitDesktop]::GetForegroundWindow() -ne $App) { throw 'The Hosting menu lost foreground.' }
        if (Find-KitControl $App 'HostingRowExport') {
            if ([Windows.Automation.AutomationElement]::FocusedElement.Current.AutomationId -eq 'HostingRowExport') { return $true }
            [KitDesktop]::Tap(40)
        }
    } 12 'keyboard focus on the Hosting row export action' | Out-Null
    [KitDesktop]::Tap(13)

    $surface = Wait-KitHostingExport $App 'row'
    if ($surface.Kind -eq 'dialog') {
        Save-KitWindow $App (Join-Path $Results 'hosting-export-cancel.png')
        Invoke-KitControl $surface.Root 'CloseButton'
        Wait-KitCondition { -not (Find-KitHostingExportDialog $App) } 12 'the export dialog to close' | Out-Null
    } else {
        if (-not (Find-KitExportPicker $App)) { throw 'The folder picker lost foreground.' }
        [KitDesktop]::Tap(27)
        Wait-KitCondition { -not (Find-KitExportPicker $App) } 12 'the export picker to close' | Out-Null
    }
    if (@(Get-ChildItem -LiteralPath $Destination -Force).Count) { throw 'Cancel wrote export files.' }

    $surface = $null
    foreach ($attempt in 1..3) {
        Invoke-KitControl $App 'ExportHost'
        try {
            $surface = Wait-KitHostingExport $App 'page' 6
            break
        } catch {
            # WinUI may expose the page before its previous dialog's action has finished.
            if ($attempt -eq 3 -or $_.Exception.Message -ne 'Timed out waiting for the Hosting export surface opened by page.') { throw }
        }
    }
    if ($surface.Kind -eq 'dialog') {
        Save-KitWindow $App (Join-Path $Results 'hosting-export-accept.png')
        Invoke-KitControl $surface.Root 'PrimaryButton'
        $surface = Wait-KitCondition { Find-KitExportPicker $App } 12 'the native export folder picker'
        Save-KitHostingExportPeers $surface 'page' 'picker'
    } else {
        $surface = $surface.Root
    }
    Select-KitExportFolder $surface $Destination
    $config = Wait-KitCondition {
        Get-ChildItem -LiteralPath $Destination -Recurse -File -Filter 'server.cfg' | Select-Object -First 1
    } 15 'server.cfg in the private export folder'
    $files = @('server.cfg','start-linux.sh','start-windows.bat','README.txt')
    foreach ($name in $files) {
        if (-not (Test-Path -LiteralPath (Join-Path $config.DirectoryName $name) -PathType Leaf)) {
            throw "The Hosting export omitted $name."
        }
    }
    $settings = Read-KitText $config.FullName
    if ($settings -notmatch 'Kit Export' -or $settings -notmatch 'CHANGE_ME') {
        throw 'The Hosting export did not contain the selected server and password placeholders.'
    }
    "Row export canceled. Page export wrote $($files -join ', ') for Kit Export with password placeholders." |
        Set-Content -LiteralPath (Join-Path $Results 'hosting-export.txt') -Encoding UTF8
}

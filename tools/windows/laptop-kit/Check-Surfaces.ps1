function Save-KitSurface([IntPtr]$Window, [string]$Name, [string[]]$Controls, [string[]]$Names = @()) {
    foreach ($control in $Controls) {
        Wait-KitCondition { Find-KitControl $Window $control -AllowOffscreen } 12 $control | Out-Null
    }
    foreach ($control in $Names) {
        Wait-KitCondition { Find-KitControl $Window $control -Name -AllowOffscreen } 12 $control | Out-Null
    }
    Focus-KitWindow $Window
    Save-KitWindow $Window (Join-Path $Results "$Name.png")
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
                if (-not (Select-String -LiteralPath $trace -SimpleMatch " $id Windows 10 acrylic fallback applied" -Quiet)) {
                    throw 'The Windows 10 fallback did not apply to both themes.'
                }
                $control = switch ($route) {
                    'setup' { 'SetupNext' }
                    'first-home' { 'OpenGameMenuButton' }
                    'returning-home' { 'LaunchButton' }
                }
                [KitDesktop]::SizeSurfaceWindow($app, 1440, 900)
                Save-KitSurface $app $name @($control)
            } finally {
                if (-not $run.Process.HasExited) { [void]$run.Process.CloseMainWindow() }
                $done = Complete-KitProcess $run (New-Folder (Join-Path $Results $name)) 12
                Save-Text $trace (Join-Path $Results "$name-startup.log")
                if ($done.TimedOut -or $done.ExitCode -ne 0) { throw "$name did not close cleanly." }
            }
        }
    }
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
        Focus-KitWindow $app
        switch ($Page) {
            'home' {
                if ($Variant -eq 'first') {
                    Save-KitSurface $app 'first-home' @('OpenGameMenuButton', 'FindServerButton')
                    $news = Wait-KitCondition {
                        foreach ($id in 'SeeAllNews', 'NewsChip') {
                            if (Find-KitControl $app $id) { return $id }
                        }
                    } 12 'the visible Home news action'
                    Invoke-KitControl $app $news
                    Save-KitSurface $app 'news' @('CloseButton')
                    Invoke-KitControl $app 'CloseButton'
                } else {
                    Save-KitSurface $app 'returning-home' @('LaunchButton')
                    Open-KitJump $app 'open game menu' 'Open Game Menu'
                    Save-KitSurface $app 'game-menu-jump' @('JumpBox', 'JumpList')
                    [KitDesktop]::Tap(27)
                    Open-KitConnect $app
                    Save-KitSurface $app 'connect' @('AddressBox', 'CloseButton')
                    Invoke-KitControl $app 'CloseButton'
                }
            }
            'jump' {
                Open-KitJump $app 'keyboard shortcuts' 'Keyboard Shortcuts'
                Save-KitSurface $app 'jump-search' @('JumpBox', 'JumpList')
                [KitDesktop]::Tap(13)
                $sheet = Wait-KitCondition { Get-KitControls $app 'Keyboard shortcuts' -Name |
                    Where-Object { $_.Current.ControlType -eq [Windows.Automation.ControlType]::Window } } 12 'the shortcuts sheet'
                Save-KitSurface $app 'shortcuts' @('PrimaryButton')
                Invoke-KitControl $sheet 'PrimaryButton'
            }
            'hosting' {
                Invoke-KitControl $app 'ServersItem'
                Invoke-KitControl $app 'Hosting' -Name
                Invoke-KitControl $app 'New server' -Name
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
                Invoke-KitControl $app 'ConfigsItem'
                Save-KitSurface $app 'config-landing' @('Loadouts', 'AddConfig')
                Invoke-KitControl (Find-KitControl $app 'Loadouts') 'Main' -Name
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
                Invoke-KitControl $app 'MatchesSection'
                Save-KitSurface $app 'library-matches' @('Matches') @(('Toujane ' + [char]0x00b7 + ' Deathmatch'))
                Invoke-KitControl $app 'DemosSection'
                Save-KitSurface $app 'library-demos' @('Search','ShowFolder','Files')
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
                    Invoke-KitControl $settingsWindow $report -Name
                    Save-KitSurface $settingsWindow 'problem-report' @('CloseButton') @('Copy details')
                    Invoke-KitControl $settingsWindow 'CloseButton'
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

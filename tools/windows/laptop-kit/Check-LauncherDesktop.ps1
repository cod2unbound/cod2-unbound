function Initialize-KitLauncherDesktop {
    Initialize-KitDesktop
    Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
    [void][KitDesktop]::SetThreadDpiAwarenessContext([IntPtr](-4))
}

function Start-KitDesktopLauncher([string]$Exe, [string]$Appearance = 'system', [hashtable]$Settings) {
    $path = Join-Path $LauncherHome 'launcher\launcher-settings.json'
    $preferences = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    $preferences | Add-Member -NotePropertyName appearance -NotePropertyValue $Appearance -Force
    $preferences | ConvertTo-Json | Set-Content -LiteralPath $path -Encoding ASCII
    if (-not $Settings) { $Settings = Get-KitLauncherSettings }
    Start-KitProcess $Exe '' $LauncherHome $Settings
}

function Invoke-KitSheetDimming {
    Stop-KitIfNoGame
    Initialize-KitLauncherDesktop
    $exe = Get-KitStartTarget
    $setup = $LauncherHome
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize'
    $system = (Get-ItemProperty -LiteralPath $key -Name AppsUseLightTheme -ErrorAction SilentlyContinue).AppsUseLightTheme
    $failures = foreach ($appearance in 'light','dark','system') {
        $directory = New-Folder (Join-Path $Results $appearance)
        $LauncherHome = New-KitLauncherTestHome $setup (New-Folder (Join-Path $Private $appearance))
        Set-KitLauncherProfile 'original' 'dx9'
        $run = Start-KitDesktopLauncher $exe $appearance
        try {
            $window = Get-KitLauncherWindow $run.Process
            Wait-KitHome $window
            [KitDesktop]::SizeSurfaceWindow($window, 1440, 900)
            Focus-KitPictureWindow $window $run.Process.Id
            [KitDesktop]::Chord(17,50)
            Wait-KitCondition { Find-KitControl $window 'SearchBox' } 12 'Servers' | Out-Null
            $beforePath = Join-Path $directory 'before.png'
            $afterPath = Join-Path $directory 'sheet.png'
            Save-KitWindow $window $beforePath
            Open-KitConnect $window
            Start-Sleep -Milliseconds 650
            Save-KitWindow $window $afterPath
            $before = [Drawing.Bitmap]::new($beforePath)
            $after = [Drawing.Bitmap]::new($afterPath)
            try {
                if ($before.Size -ne $after.Size) { throw 'The window resized during sampling.' }
                $y = [int]($before.Height * 0.53)
                $samples = foreach ($x in 31,($before.Width - 38)) {
                    $a = $before.GetPixel($x,$y)
                    $b = $after.GetPixel($x,$y)
                    $old = $a.R + $a.G + $a.B
                    $new = $b.R + $b.G + $b.B
                    [pscustomobject]@{ Appearance=$appearance; WindowsAppsUseLightTheme=$system; X=$x; Y=$y
                        Before=('{0},{1},{2}' -f $a.R,$a.G,$a.B); After=('{0},{1},{2}' -f $b.R,$b.G,$b.B)
                        Darkened=($new -lt $old -and $b.R -le $a.R -and $b.G -le $a.G -and $b.B -le $a.B) }
                }
                $samples | Export-Csv -LiteralPath (Join-Path $directory 'pixels.csv') -NoTypeInformation -Encoding UTF8
                if ($samples.Darkened -contains $false) { throw "The $appearance sheet did not darken both samples." }
            } finally { $before.Dispose(); $after.Dispose() }
            Invoke-KitControl $window 'Cancel' -Name
        } catch {
            "$appearance appearance. $($_.Exception.Message)"
        } finally { Stop-KitLauncher $run $directory }
    }
    if ($failures) { throw ($failures -join ' ') }
}

function Get-KitTabTrace([IntPtr]$Window, [string]$Page, [int]$Count) {
    foreach ($index in 0..$Count) {
        if ([KitDesktop]::GetForegroundWindow() -ne $Window) { throw "The $Page window lost focus during the Tab walk." }
        $focus = [Windows.Automation.AutomationElement]::FocusedElement.Current
        [pscustomobject]@{ Page=$Page; Tab=$index; Name=$focus.Name; Id=$focus.AutomationId
            Type=$focus.ControlType.ProgrammaticName; Focused=$focus.HasKeyboardFocus }
        if ($index -lt $Count) {
            [KitDesktop]::Tap(9)
            Start-Sleep -Milliseconds 140
        }
    }
}

function Invoke-KitTabNames {
    Stop-KitIfNoGame
    Initialize-KitLauncherDesktop
    $exe = Get-KitStartTarget
    $LauncherHome = New-KitLauncherTestHome $LauncherHome $Private
    Set-KitLauncherProfile 'original' 'dx9'
    $run = Start-KitDesktopLauncher $exe
    $receipt = Join-Path $Results 'tab-names.csv'
    $unnamed = $false
    try {
        $window = Get-KitLauncherWindow $run.Process
        Wait-KitHome $window
        [KitDesktop]::SizeSurfaceWindow($window, 1440, 900)
        $pages = @(
            @{Name='Home'; Item='HomeItem'; Witness='HeroTitle'},
            @{Name='Servers'; Item='ServersItem'; Witness='SearchBox'},
            @{Name='Hosting'; Item='HostingTab'; Witness='HostingNewServer'},
            @{Name='Library'; Item='LibraryItem'; Witness='DemosSection'},
            @{Name='Configs'; Item='ConfigsItem'; Witness='ConfigsCheckConfig'}
        )
        foreach ($page in $pages) {
            Focus-KitPictureWindow $window $run.Process.Id
            $item = Wait-KitCondition { Find-KitControl $window $page.Item } 8 "the $($page.Name) navigation item"
            $area = $item.Current.BoundingRectangle
            if ($area.IsEmpty -or -not [KitDesktop]::ClickSurface($window,
                    [int]($area.Left + $area.Width / 2), [int]($area.Top + $area.Height / 2), $true)) {
                throw "The $($page.Name) navigation item could not receive a pointer click."
            }
            Wait-KitCondition { Find-KitControl $window $page.Witness -AllowOffscreen } 12 $page.Name | Out-Null
            $start = Wait-KitCondition { Find-KitControl $window 'SettingsItem' } 8 'the Settings sidebar item'
            $start.SetFocus()
            $rows = @(Get-KitTabTrace $window $page.Name 12)
            $rows | Export-Csv -LiteralPath $receipt -Append -NoTypeInformation -Encoding UTF8
            if ($rows.Where({ !$_.Name -or !$_.Focused }).Count -or $rows[0].Id -ne 'SettingsItem') { $unnamed = $true }
        }
        $settingsWindow = Open-KitSettings $window
        try {
            $selector = Find-KitControl $settingsWindow 'Panes'
            foreach ($name in 'General','Display','Game Data','Privacy','Updates','Advanced','About') {
                Invoke-KitControl $selector $name -Name
                $item = Find-KitControl $selector $name -Name
                Focus-KitPictureWindow $settingsWindow $run.Process.Id
                $item.SetFocus()
                $rows = @(Get-KitTabTrace $settingsWindow "Settings / $name" 10)
                $rows | Export-Csv -LiteralPath $receipt -Append -NoTypeInformation -Encoding UTF8
                if ($rows.Where({ !$_.Name -or !$_.Focused }).Count -or $rows[0].Name -ne $name) { $unnamed = $true }
            }
        } finally { [KitDesktop]::CloseOwnedWindow($settingsWindow) }
    } finally { Stop-KitLauncher $run $Results }
    if ($unnamed) { throw 'A Tab target was unnamed or the starting control did not receive focus. See tab-names.csv.' }
}

function Find-KitActivatedLauncher([string]$Exe, [DateTime]$Since, [int]$Session) {
    Get-Process -Name 'CoD2Unbound.Windows' -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -ieq $Exe -and $_.StartTime -ge $Since -and $_.SessionId -eq $Session
    } | Sort-Object StartTime | Select-Object -First 1
}

function Invoke-KitDesktopActivation([ValidateSet('running','closed')][string]$Opening) {
    Stop-KitIfNoGame
    Initialize-KitLauncherDesktop
    $exe = Get-KitStartTarget
    $profile = $LauncherHome
    $session = [Diagnostics.Process]::GetCurrentProcess().SessionId
    $config = Join-Path $Private 'kit activation.cod2-unbound-test-config'
    Copy-Item -LiteralPath (Join-Path $Kit 'Sample config.cfg') -Destination $config
    $recording = Join-Path $Kit 'frame-references\original\smoke-mp_toujane.dm_1'
    Stop-KitIfMissing $recording 'The test recording is missing.'
    $demo = Join-Path $Private 'kit activation recording.cod2-unbound-test-demo'
    Copy-Item -LiteralPath $recording -Destination $demo
    $playableName = 'kit_activation_recording'
    $sourceHash = (Get-FileHash -LiteralPath $demo).Hash
    $failures = foreach ($case in @(
        @{ Type='config'; Source=$config; Class='CoD2UnboundTest.Config'; Sheet='Review this config'; Witness='Cancel' },
        @{ Type='demo'; Source=$demo; Class=$FixtureDemo },
        @{ Type='server'; Source='cod2-test://127.0.0.1:9'; Class='cod2-test'; Sheet='Connect to an address'; Witness='Server address' }
    )) {
        $directory = New-Folder (Join-Path $Results $case.Type)
        $LauncherHome = New-KitLauncherTestHome $profile (New-Folder (Join-Path $Private $case.Type))
        Set-KitLauncherProfile 'original' 'dx9'
        $settings = Get-KitLauncherSettings
        $run = $process = $sheet = $game = $gameSince = $null
        $window = $expectedWindow = [IntPtr]::Zero
        $expectedMonitor = $null
        $started = Get-Date
        try {
            $run = Start-KitDesktopLauncher $exe -Settings $settings
            $preparedWindow = Get-KitLauncherWindow $run.Process
            Wait-KitHome $preparedWindow
            Focus-KitPictureWindow $preparedWindow $run.Process.Id
            # Visiting Studio once lets it create its default loadout before the no-import comparison.
            [KitDesktop]::Chord(17,52)
            Wait-KitCondition { Find-KitControl $preparedWindow 'ConfigsCheckConfig' -AllowOffscreen } 12 'the initialized Config Studio' | Out-Null
            [KitDesktop]::Chord(17,49)
            Wait-KitHome $preparedWindow
            if ($Opening -eq 'running') {
                $process = $run.Process
                $expectedWindow = $preparedWindow
                $expectedMonitor = [Windows.Forms.Screen]::FromHandle($expectedWindow).DeviceName
                [void][KitDesktop]::ShowWindow($expectedWindow, 6)
                Wait-KitCondition { [KitDesktop]::IsIconic($expectedWindow) } 5 'the launcher to minimize' | Out-Null
            } else {
                Stop-KitLauncher $run (New-Folder (Join-Path $directory 'prepared'))
                $run = $null
                $started = Get-Date
            }
            $before = @(Get-TreeHashes (Join-Path $LauncherHome 'main'))
            $before | Set-Content -LiteralPath (Join-Path $directory 'private-files-before.txt')
            $gameSince = Get-Date
            if ($case.Type -ne 'server') {
                $suffix = [IO.Path]::GetExtension($case.Source)
                $registration = Get-Item -LiteralPath "HKCU:\Software\Classes\$suffix\OpenWithProgids"
                if ($case.Class -notin $registration.GetValueNames()) {
                    throw "The installed Test file type $suffix does not list its registered handler."
                }
            }
            Open-KitAssociation $case.Source $case.Class $exe $settings
            $window = Wait-KitCondition {
                $front = [KitDesktop]::GetForegroundWindow()
                if (!$front -or ($expectedWindow -ne [IntPtr]::Zero -and $front -ne $expectedWindow)) { return }
                $owner = Get-Process -Id ([KitDesktop]::WindowPid($front)) -ErrorAction SilentlyContinue
                if ($owner.Path -ieq $exe -and $owner.SessionId -eq $session -and $owner.StartTime -ge $started) { $front }
            } 25 'the Test launcher to come to the front after the association opened'
            if (!$process) { $process = Get-Process -Id ([KitDesktop]::WindowPid($window)) }
            $monitor = [Windows.Forms.Screen]::FromHandle($window).DeviceName
            "Foreground window=$window process=$($process.Id) monitor=$monitor expected=$expectedMonitor" |
                Set-Content -LiteralPath (Join-Path $directory 'foreground.txt')
            if ($expectedMonitor -and $monitor -ne $expectedMonitor) { throw 'The existing window moved to another screen.' }
            if ($case.Type -eq 'demo') {
                $game = Get-KitOwnedGame 'original' $gameSince $session
                $command = (Get-CimInstance Win32_Process -Filter "ProcessId=$($game.Id)").CommandLine
                if ($command -notmatch ('(?:^|\s)"?\+demo"?\s+"?' + [regex]::Escape($playableName) + '"?(?=\s|$)')) {
                    throw 'The game did not start with the expected playable demo name.'
                }
                $staged = Join-Path $LauncherHome "main\demos\$playableName.dm_1"
                $stagedHash = (Get-FileHash -LiteralPath $staged).Hash
                if ($stagedHash -ne $sourceHash) { throw 'The private demo copy does not match the source.' }
                "Started process $($game.Id) with +demo $playableName. Source and private copy SHA-256: $stagedHash" |
                    Set-Content -LiteralPath (Join-Path $directory 'playback.txt')
                Stop-KitOwnedGame $game ([IntPtr]::Zero)
                Wait-KitCondition {
                    if ([KitDesktop]::GetForegroundWindow() -ne $window) { return }
                    $demos = Find-KitControl $window 'DemosSection'
                    if ($demos) {
                        $demos.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected
                    }
                } 15 'Library Demos in the foreground after playback' | Out-Null
                Save-KitWindow $window (Join-Path $directory 'library.png')
                continue
            }
            $sheet = Wait-KitCondition { Find-KitControl $window $case.Sheet -Name } 14 $case.Sheet
            Wait-KitCondition { Find-KitControl $window $case.Witness -Name } 10 $case.Witness | Out-Null
            if ($case.Type -eq 'server') {
                $edit = Find-KitControl $window 'AddressBox'
                if ($edit.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -ne '127.0.0.1:9') {
                    throw 'Connect did not contain the supplied address.'
                }
            }
            Save-KitWindow $window (Join-Path $directory 'review.png')
            $after = @(Get-TreeHashes (Join-Path $LauncherHome 'main'))
            $after | Set-Content -LiteralPath (Join-Path $directory 'private-files-at-review.txt')
            if (($before -join [Environment]::NewLine) -cne ($after -join [Environment]::NewLine)) {
                throw 'The test player or recording files changed before confirmation.'
            }
            if (Get-Process -Name 'cod2_win32','cod2_win32_ded' -ErrorAction SilentlyContinue | Where-Object {
                $_.SessionId -eq $session -and $_.StartTime -ge $started -and
                    $_.Path -and $_.Path.StartsWith("$InstallRoot\",[StringComparison]::OrdinalIgnoreCase)
            }) { throw 'An engine started before confirmation.' }
            'The review was shown. No test data changed and no test engine started.' |
                Set-Content -LiteralPath (Join-Path $directory 'confirmation.txt')
        } catch {
            "$($case.Type). $($_.Exception.Message)"
        } finally {
            try {
                if ($case.Type -eq 'demo' -and $gameSince) {
                    if (!$game) {
                        try { $game = Get-KitOwnedGame 'original' $gameSince $session -Seconds 2 } catch { }
                    }
                    Stop-KitOwnedGame $game ([IntPtr]::Zero)
                }
                if ($sheet -and (Find-KitControl $window $case.Sheet -Name)) {
                    $dismiss = if (Find-KitControl $window 'Cancel' -Name) { 'Cancel' } else { 'Close' }
                    Invoke-KitControl $window $dismiss -Name
                }
            } finally {
                if ($run) { Stop-KitLauncher $run $directory }
                else {
                    if (!$process) { $process = Find-KitActivatedLauncher $exe $started $session }
                    if ($process -and !$process.HasExited) {
                        [void]$process.CloseMainWindow()
                        if (!$process.WaitForExit(12000)) {
                            $process.Kill()
                            [void]$process.WaitForExit(8000)
                            throw 'The association-opened Test launcher did not close and was stopped.'
                        }
                    }
                }
            }
        }
    }
    if ($failures) { throw ($failures -join ' ') }
}

function Invoke-KitTaskbarCaption {
    Stop-KitIfNoGame
    Initialize-KitLauncherDesktop
    $exe = Get-KitStartTarget
    $LauncherHome = New-KitLauncherTestHome $LauncherHome $Private
    Set-KitLauncherProfile 'original' 'dx9'
    $run = Start-KitDesktopLauncher $exe
    try {
        $window = Get-KitLauncherWindow $run.Process
        Wait-KitHome $window
        Focus-KitPictureWindow $window $run.Process.Id
        $caption = [Windows.Automation.AutomationElement]::FromHandle($window).Current.Name
        if (!$caption) { throw 'Windows returned an empty window caption.' }
        $caption | Set-Content -LiteralPath (Join-Path $Results 'caption.txt')
        Save-KitWindow $window (Join-Path $Results 'caption-window.png')
        $desktop = [Windows.Automation.AutomationElement]::RootElement
        $condition = [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::ClassNameProperty,'Shell_TrayWnd')
        $taskbar = $desktop.FindFirst([Windows.Automation.TreeScope]::Children,$condition)
        $buttons = [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Button)
        $matches = if ($taskbar) { @($taskbar.FindAll([Windows.Automation.TreeScope]::Descendants,$buttons) | Where-Object {
            !$_.Current.IsOffscreen -and $_.Current.Name -like "$caption*"
        }) }
        if (@($matches).Count -ne 1) {
            'A unique visible taskbar button could not be captured. Inspect the Test icon in person.' |
                Set-Content -LiteralPath (Join-Path $Results 'taskbar.txt')
            throw [NotSupportedException]::new('The Test taskbar icon needs an attended check.')
        }
        $bar = $taskbar.Current.BoundingRectangle
        $bounds = [Windows.Rect]::Intersect(@($matches)[0].Current.BoundingRectangle, $bar)
        if ($bounds.IsEmpty) { throw [NotSupportedException]::new('The Test taskbar icon is outside the visible taskbar.') }
        "$(@($matches)[0].Current.Name), bounds=$bounds" | Set-Content -LiteralPath (Join-Path $Results 'taskbar.txt')
        $bitmap = [Drawing.Bitmap]::new([int]$bounds.Width,[int]$bounds.Height)
        $drawing = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $drawing.CopyFromScreen([int]$bounds.X,[int]$bounds.Y,0,0,$bitmap.Size)
            $bitmap.Save((Join-Path $Results 'taskbar-button.png'),[Drawing.Imaging.ImageFormat]::Png)
        } finally { $drawing.Dispose(); $bitmap.Dispose() }
    } finally { Stop-KitLauncher $run $Results }
}

function Find-KitUpdateToast([string]$Version) {
    $title = "CoD2 Unbound $Version is ready"
    $name = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::NameProperty, $title)
    $desktop = [Windows.Automation.AutomationElement]::RootElement
    $node = $desktop.FindFirst([Windows.Automation.TreeScope]::Descendants,$name)
    $walker = [Windows.Automation.TreeWalker]::ControlViewWalker
    for ($depth = 0; $node -and $depth -lt 4; $depth++) {
        $pattern = $null
        if (!$node.Current.IsOffscreen -and
            $node.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern,[ref]$pattern)) {
            return $pattern
        }
        $node = $walker.GetParent($node)
    }
}

function Invoke-KitUpdateNotice([ValidateSet('foreground','minimized','closed')][string]$State) {
    Stop-KitIfNoGame
    Initialize-KitLauncherDesktop
    $exe = Get-KitStartTarget
    $LauncherHome = New-KitLauncherTestHome $LauncherHome $Private
    Set-KitLauncherProfile 'original' 'dx9'
    $preferencesPath = Join-Path $LauncherHome 'launcher\launcher-settings.json'
    $preferences = Get-Content -LiteralPath $preferencesPath -Raw | ConvertFrom-Json
    $preferences | Add-Member -NotePropertyName checkUpdatesAutomatically -NotePropertyValue $true -Force
    $preferences | ConvertTo-Json | Set-Content -LiteralPath $preferencesPath -Encoding ASCII

    $setup = (Get-KitUpdateTrust).Setup
    $entry = New-RunContext 'notice-feed'
    $ready = Join-Path $entry.Folder 'port'
    # Windows release assets accept only an unsigned 16-bit build number.
    $version = '0.5.' + (Get-Random -Minimum 10000 -Maximum ([uint16]::MaxValue + 1))
    $presented = Join-Path $entry.Folder "presentation-ready-$version"
    $server = Join-Path $Kit 'Local-Update-Feed.ps1'
    $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}" -Setup "{1}" -Ready "{2}" -NoticeVersion {3} -NoticeGate "{4}"' -f $server,$setup.FullName,$ready,$version,$presented
    $feed = Start-KitProcess $powershell $arguments $entry.Folder -NoWindow
    $run = $null
    $receipt = Join-Path $Results 'notice.txt'
    "State=$State. Loopback offer=$version." | Set-Content -LiteralPath $receipt
    try {
        $port = Wait-KitCondition {
            if ($feed.Process.HasExited) { throw 'The local update feed exited.' }
            if (Test-Path -LiteralPath $ready) {
                $number = Get-Content -LiteralPath $ready -Raw
                if ($number -match '^\d+$') { $number }
            }
        } 12 'the local update feed'
        $settings = Get-KitLauncherSettings
        $settings.COD2_UNBOUND_TEST_UPDATE_ORIGIN = "http://127.0.0.1:$port/"
        $run = Start-KitDesktopLauncher $exe -Settings $settings
        $window = Get-KitLauncherWindow $run.Process
        Wait-KitHome $window
        if ($State -eq 'foreground') {
            Focus-KitPictureWindow $window $run.Process.Id
            [IO.File]::WriteAllText($presented, 'foreground')
            try {
                Wait-KitCondition {
                    if ([KitDesktop]::GetForegroundWindow() -ne $window) { throw 'Foreground changed during the update check.' }
                    Find-KitControl $window 'Install Update' -Name
                } 40 'the in-app Install Update action' | Out-Null
            } catch {
                if ([KitDesktop]::GetForegroundWindow() -eq $window) {
                    Save-KitWindow $window (Join-Path $Results 'in-app-timeout.png')
                }
                throw
            }
            Save-KitWindow $window (Join-Path $Results 'in-app-offer.png')
            'Home showed the offer. The launcher reserves Windows notifications for background offers.' |
                Add-Content -LiteralPath $receipt
            return
        }

        [void][KitDesktop]::ShowWindow($window,6)
        Wait-KitCondition { [KitDesktop]::IsIconic($window) } 5 'the launcher to minimize' | Out-Null
        [IO.File]::WriteAllText($presented, 'minimized')
        $library = Join-Path $LauncherHome 'launcher\launcher-library.json'
        try {
            Wait-KitCondition {
                if (Test-Path -LiteralPath $library) {
                    try { $version -in (Get-Content -LiteralPath $library -Raw | ConvertFrom-Json).notifiedUpdates }
                    catch { $false }
                }
            } 40 'Windows accepting the local update notification' | Out-Null
        } catch {
            'Windows did not acknowledge the notification during the sample. Leave Windows settings as they are.' |
                Add-Content -LiteralPath $receipt
            throw [NotSupportedException]::new('Check the Test update notification in person. No Windows notification acknowledgement was recorded.')
        }
        'The launcher recorded that Windows accepted the notification.' | Add-Content -LiteralPath $receipt
        if ($State -eq 'closed') {
            Stop-KitLauncher $run $Results
            $run = $null
            'Click the Test notice after closing the launcher in a disposable offline Windows account. Confirm Settings opens on Updates. The notification service opens a cold process without the private test environment.' |
                Add-Content -LiteralPath $receipt
            throw [NotSupportedException]::new('Cold notification activation needs an attended check in a disposable account.')
        }

        $toast = Find-KitUpdateToast $version
        if (!$toast) {
            $key = if ((Get-KitWindowsVersion).Family -eq 'Windows 10') { 65 } else { 78 }
            [KitDesktop]::Chord(91,$key)
            Start-Sleep -Milliseconds 550
            $toast = Find-KitUpdateToast $version
            if (!$toast) { [KitDesktop]::Tap(27) }
        }
        if (!$toast) {
            'Windows accepted the notification, but its clickable item was unavailable to UI Automation. Click the notice with the launcher minimized and confirm Settings opens on Updates.' |
                Add-Content -LiteralPath $receipt
            throw [NotSupportedException]::new('The Test notification click needs an attended check.')
        }
        $toast.Invoke()
        $updates = Wait-KitCondition {
            foreach ($candidate in [KitDesktop]::VisibleProcessWindows($run.Process.Id)) {
                if ($candidate -eq $window -or $candidate -ne [KitDesktop]::GetForegroundWindow()) { continue }
                $pane = Find-KitControl $candidate 'SettingsUpdatesPane'
                $selection = $null
                if ($pane -and $pane.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern,[ref]$selection) -and
                    $selection.Current.IsSelected) { $candidate }
            }
        } 20 'notification activation to bring Updates forward'
        Save-KitWindow $updates (Join-Path $Results 'updates.png')
        'Invoked the Windows notification and saw the Updates section selected.' | Add-Content -LiteralPath $receipt
        [KitDesktop]::CloseOwnedWindow($updates)
    } finally {
        if ($run) { Stop-KitLauncher $run $Results }
        if (!$feed.Process.HasExited) { $feed.Process.Kill() }
        Complete-KitProcess $feed $entry.Evidence 8 | Out-Null
    }
}

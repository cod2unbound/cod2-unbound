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

function Get-KitTabTrace([IntPtr]$Window, [string]$Page, [int]$Count, $Start, [int]$Owner, [int]$Delay = 140) {
    foreach ($attempt in 1..2) {
        Focus-KitPictureWindow $Window $Owner
        $Start.SetFocus()
        $rows = [Collections.Generic.List[object]]::new()
        foreach ($index in 0..$Count) {
            $front = [KitDesktop]::GetForegroundWindow()
            if ($front -ne $Window) {
                $ownerAtLoss = Get-KitForegroundOwner $front $Owner
                $ownedHandles = if ($ownerAtLoss -eq 'our launcher') { " Expected HWND=$Window, actual HWND=$front." }
                "$([DateTimeOffset]::UtcNow.ToString('O')) $Page attempt $attempt, Tab $index. Foreground: $ownerAtLoss.$ownedHandles" |
                    Add-Content -LiteralPath (Join-Path $Results 'tab-foreground-loss.txt') -Encoding UTF8
                break
            }
            $focus = [Windows.Automation.AutomationElement]::FocusedElement.Current
            $rows.Add([pscustomobject]@{ Page=$Page; Tab=$index; Name=$focus.Name; Id=$focus.AutomationId
                Type=$focus.ControlType.ProgrammaticName; Focused=$focus.HasKeyboardFocus })
            if ($index -lt $Count) {
                [KitDesktop]::Tap(9)
                Start-Sleep -Milliseconds $Delay
            }
        }
        if ($rows.Count -eq ($Count + 1)) { return $rows }
    }
    throw "The $Page window lost foreground in both Tab walks. See tab-foreground-loss.txt."
}

function Invoke-KitTabNames {
    Stop-KitIfNoGame
    Initialize-KitLauncherDesktop
    $exe = Get-KitStartTarget
    $LauncherHome = New-KitLauncherTestHome $LauncherHome $Private
    Set-KitLauncherProfile 'original' 'dx9'
    $run = Start-KitDesktopLauncher $exe
    $receipt = Join-Path $Results 'tab-names.csv'
    $repeats = Join-Path $Results 'tab-settings-general.csv'
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
            $rows = @(Get-KitTabTrace $window $page.Name 12 $start $run.Process.Id)
            $rows | Export-Csv -LiteralPath $receipt -Append -NoTypeInformation -Encoding UTF8
            if ($rows.Where({ !$_.Name -or !$_.Focused }).Count -or $rows[0].Id -ne 'SettingsItem') { $unnamed = $true }
        }
        $settingsWindow = Open-KitSettings $window
        try {
            $selector = Find-KitControl $settingsWindow 'Panes'
            foreach ($name in 'General','Display','Game Data','Privacy','Updates','Advanced','About') {
                Invoke-KitControl $selector $name -Name
                $item = Find-KitControl $selector $name -Name
                $rows = @(Get-KitTabTrace $settingsWindow "Settings / $name" 10 $item $run.Process.Id)
                $rows | Export-Csv -LiteralPath $receipt -Append -NoTypeInformation -Encoding UTF8
                if ($rows.Where({ !$_.Name -or !$_.Focused }).Count -or $rows[0].Name -ne $name) { $unnamed = $true }
            }
        } finally { [KitDesktop]::CloseOwnedWindow($settingsWindow) }
        foreach ($delay in 70,140,300) {
            Wait-KitCondition {
                $settingsWindow -notin [KitDesktop]::VisibleProcessWindows($run.Process.Id)
            } 8 'the previous Settings window to close' | Out-Null
            Focus-KitPictureWindow $window $run.Process.Id
            # Open Settings as a shell navigation completes, then vary the Tab cadence.
            $key = if ($delay -eq 140) { 49 } else { 52 }
            [KitDesktop]::Chord(17,$key)
            $settingsWindow = Open-KitSettings $window
            try {
                $selector = Wait-KitCondition { Find-KitControl $settingsWindow 'Panes' } 8 'the Settings sections'
                Invoke-KitControl $selector 'General' -Name
                $general = Wait-KitCondition { Find-KitControl $selector 'General' -Name } 8 'General settings'
                $rows = @(Get-KitTabTrace $settingsWindow "Settings / General ($delay ms)" 5 $general $run.Process.Id $delay)
                $rows | Export-Csv -LiteralPath $repeats -Append -NoTypeInformation -Encoding UTF8
                if ($rows.Where({ !$_.Name -or !$_.Focused }).Count -or $rows[0].Name -ne 'General') { $unnamed = $true }
            } finally { [KitDesktop]::CloseOwnedWindow($settingsWindow) }
        }
    } finally {
        Stop-KitLauncher $run $Results
        Save-Text (Join-Path $LauncherHome 'startup-timing.log') (Join-Path $Results 'startup-timing.log')
    }
    if ($unnamed) { throw 'A Tab target was unnamed or the starting control did not receive focus. See tab-names.csv.' }
}

function Find-KitActivatedLauncher([string]$Exe, [DateTime]$Since, [int]$Session) {
    Get-Process -Name 'CoD2Unbound.Windows' -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -ieq $Exe -and $_.StartTime -ge $Since -and $_.SessionId -eq $Session
    } | Sort-Object StartTime | Select-Object -First 1
}

function Find-KitBindEdit([IntPtr]$Window, [string]$Field) {
    Get-KitControls $Window $Field -Name -AllowOffscreen | Where-Object {
        $_.Current.ControlType -eq [Windows.Automation.ControlType]::Edit
    } | Select-Object -First 1
}

function Set-KitBindText([IntPtr]$Window, [string]$Field, [string]$Text, [switch]$KeepFocus) {
    $edit = Wait-KitCondition { Find-KitBindEdit $Window $Field } 8 $Field
    $edit.SetFocus()
    $edit.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($Text)
    if ($KeepFocus) { return }
    [KitDesktop]::Tap(9)
    Start-Sleep -Milliseconds 350
    if (Find-KitControl $Window 'Save bind' -Name -AllowOffscreen) {
        Invoke-KitControl $Window 'Save bind' -Name -AllowOffscreen
        'Answered the editor confirmation with Save bind.' |
            Add-Content -LiteralPath (Join-Path $Results 'bind-confirmation.txt')
    }
}

function Invoke-KitBindChain {
    Stop-KitIfNoGame
    Initialize-KitLauncherDesktop
    $exe = Get-KitStartTarget
    $LauncherHome = New-KitLauncherTestHome $LauncherHome $Private
    Set-KitLauncherProfile 'original' 'dx9'
    $title = 'Kit multi-step bind'
    foreach ($phase in 'create','reopen','reopen-edited') {
        $evidence = New-Folder (Join-Path $Results $phase)
        $run = Start-KitDesktopLauncher $exe 'dark'
        try {
            $window = Get-KitLauncherWindow $run.Process
            Wait-KitHome $window
            [KitDesktop]::SizeSurfaceWindow($window,1440,900)
            Focus-KitPictureWindow $window $run.Process.Id
            Invoke-KitControl $window 'ConfigsItem'
            Invoke-KitControl $window 'ConfigsLoadout-kit'
            Invoke-KitControl $window 'ConfigsBinds'
            $modes = Wait-KitCondition { Find-KitControl $window 'Show binds as' -Name } 8 'bind view choices'
            Invoke-KitControl $modes 'List' -Name
            if ($phase -eq 'create') {
                Invoke-KitControl $window 'New Bind' -Name
                Wait-KitCondition {
                    [Windows.Automation.AutomationElement]::FocusedElement.Current.Name -like '*Press a key or mouse button*'
                } 8 'the key capture prompt' | Out-Null
                [KitDesktop]::Tap(117)
                Set-KitBindText $window 'Name' $title
                Set-KitBindText $window 'Step 1 text' 'weapnext'
                Invoke-KitControl $window 'Add step' -Name -AllowOffscreen
                Set-KitBindText $window 'Step 2 text' 'weapprev'
            } else {
                $list = Wait-KitCondition {
                    Get-KitControls $window 'Advanced binds' -Name | Where-Object {
                        $_.Current.ControlType -eq [Windows.Automation.ControlType]::List
                    } | Select-Object -First 1
                } 8 'the saved advanced binds'
                $row = Wait-KitCondition {
                    $list.FindAll([Windows.Automation.TreeScope]::Children,[Windows.Automation.Condition]::TrueCondition) |
                        Where-Object { $_.Current.Name -like "*$title*" } | Select-Object -First 1
                } 8 'the saved multi-step bind'
                $row.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
            }
            $values = @(foreach ($field in 'Name','Step 1 text','Step 2 text') {
                $edit = Wait-KitCondition { Find-KitBindEdit $window $field } 8 $field
                $edit.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value
            })
            $values | Set-Content -LiteralPath (Join-Path $evidence 'bind.txt')
            $second = if ($phase -eq 'reopen-edited') { 'centerview' } else { 'weapprev' }
            if (($values -join '|') -ne "$title|weapnext|$second") { throw "The $phase editor showed different bind actions." }
            Save-KitWindow $window (Join-Path $evidence 'bind.png')
            if ($phase -eq 'reopen') {
                Set-KitBindText $window 'Step 2 text' 'centerview' -KeepFocus
                $config = Join-Path $LauncherHome 'main\players\kit\config_mp.cfg'
                Wait-KitCondition {
                    $edit = Find-KitBindEdit $window 'Step 2 text'
                    if (!$edit) { return }
                    $value = $edit.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value
                    $focused = $edit.Current.HasKeyboardFocus
                    $saved = Select-String -LiteralPath $config -Pattern '^bind\s+F6\s+"weapnext\s*;\s*centerview"\s*$' -Quiet
                    "Value=$value. FieldFocused=$focused. Saved=$saved." |
                        Set-Content -LiteralPath (Join-Path $evidence 'bind-edit.txt')
                    $value -eq 'centerview' -and $focused -and $saved
                } 8 'the edited bind to save while its field still has focus' -OnTimeout {
                    Save-KitForegroundTimeout 'Bind edit focus and save' $run.Process.Id
                } | Out-Null
            }
        } finally { Stop-KitLauncher $run $evidence -RequireCleanExit }
    }
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
    if ($Opening -eq 'running') {
        '<!doctype html><title>Test launcher activation</title><a href="cod2-test://127.0.0.1:9">Open the Test Connect sheet</a>' |
            Set-Content -LiteralPath (Join-Path $Private 'Test launcher activation.html') -Encoding ASCII
    }
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
            } 25 'the Test launcher to come to the front after the association opened' -OnTimeout {
                $active = if ($process) { $process } else { Find-KitActivatedLauncher $exe $started $session }
                Save-KitForegroundTimeout 'Registered activation focus' $active.Id
            }
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
                    try {
                        if (!$process) { $process = Find-KitActivatedLauncher $exe $started $session }
                        if ($process -and !$process.HasExited) {
                            [void]$process.CloseMainWindow()
                            if (!$process.WaitForExit(12000)) {
                                $process.Kill()
                                [void]$process.WaitForExit(8000)
                                throw 'The association-opened Test launcher did not close and was stopped.'
                            }
                        }
                    } finally {
                        Save-Text (Join-Path $LauncherHome 'startup-timing.log') (Join-Path $directory 'startup-timing.log')
                    }
                }
            }
        }
    }
    if ($failures) { throw ($failures -join ' ') }
}

function Save-KitScreenRegion([Windows.Rect]$Bounds, [string]$Path) {
    $bitmap = [Drawing.Bitmap]::new([int]$Bounds.Width,[int]$Bounds.Height)
    $drawing = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $drawing.CopyFromScreen([int]$Bounds.X,[int]$Bounds.Y,0,0,$bitmap.Size)
        $bitmap.Save($Path,[Drawing.Imaging.ImageFormat]::Png)
    } finally { $drawing.Dispose(); $bitmap.Dispose() }
}

function Get-KitTaskbar {
    $handle = [KitDesktop]::PrimaryTaskbar()
    if ($handle -ne [IntPtr]::Zero) { [Windows.Automation.AutomationElement]::FromHandle($handle) }
}

function Invoke-KitTaskbarCaption {
    Stop-KitIfNoGame
    Initialize-KitLauncherDesktop
    $exe = Get-KitStartTarget
    $LauncherHome = New-KitLauncherTestHome $LauncherHome $Private
    Set-KitLauncherProfile 'original' 'dx9'
    $taskbar = Get-KitTaskbar
    if ($taskbar -and !$taskbar.Current.BoundingRectangle.IsEmpty) {
        Save-KitScreenRegion $taskbar.Current.BoundingRectangle (Join-Path $Results 'taskbar-before.png')
    }
    $run = Start-KitDesktopLauncher $exe
    try {
        $window = Get-KitLauncherWindow $run.Process
        Wait-KitHome $window
        Focus-KitPictureWindow $window $run.Process.Id
        $caption = [Windows.Automation.AutomationElement]::FromHandle($window).Current.Name
        if (!$caption) { throw 'Windows returned an empty window caption.' }
        $caption | Set-Content -LiteralPath (Join-Path $Results 'caption.txt')
        Save-KitWindow $window (Join-Path $Results 'caption-window.png')
        $taskbar = Get-KitTaskbar
        if (!$taskbar -or $taskbar.Current.BoundingRectangle.IsEmpty) {
            throw [NotSupportedException]::new('Show the Windows taskbar to check the Test icon in person.')
        }
        $bar = $taskbar.Current.BoundingRectangle
        Save-KitScreenRegion $bar (Join-Path $Results 'taskbar-launcher-open.png')
        $buttons = [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Button)
        $matches = if ($taskbar) { @($taskbar.FindAll([Windows.Automation.TreeScope]::Descendants,$buttons) | Where-Object {
            !$_.Current.IsOffscreen -and $_.Current.Name -like "*$caption*"
        }) }
        if (@($matches).Count -ne 1) {
            'The taskbar is pictured before and after the Test launcher opened. Compare the pictures to identify the new icon. Confirm it belongs to this window by pointing at it.' |
                Set-Content -LiteralPath (Join-Path $Results 'taskbar.txt')
            throw [NotSupportedException]::new('The Test taskbar icon needs an attended check.')
        }
        $bounds = [Windows.Rect]::Intersect(@($matches)[0].Current.BoundingRectangle, $bar)
        if ($bounds.IsEmpty) { throw [NotSupportedException]::new('The Test taskbar icon is outside the visible taskbar.') }
        "$(@($matches)[0].Current.Name), bounds=$bounds" | Set-Content -LiteralPath (Join-Path $Results 'taskbar.txt')
        Save-KitScreenRegion $bounds (Join-Path $Results 'taskbar-button.png')
    } finally { Stop-KitLauncher $run $Results }
}

function Find-KitUpdateToast($TitleCondition, [ref]$SawBannerWindow) {
    foreach ($handle in [KitDesktop]::NotificationBannerCandidates()) {
        try {
            $root = [Windows.Automation.AutomationElement]::FromHandle($handle)
            $window = $root.Current
            if ($window.AutomationId -notlike '*ToastView*' -or $window.IsOffscreen -or
                $window.BoundingRectangle.IsEmpty) { continue }
            $SawBannerWindow.Value = $true
            $title = $root.FindFirst([Windows.Automation.TreeScope]::Descendants, $TitleCondition)
            if ($title -and !$title.Current.IsOffscreen -and !$title.Current.BoundingRectangle.IsEmpty) {
                return @{ Window = $handle; Title = $title }
            }
        } catch [Windows.Automation.ElementNotAvailableException] { }
        catch [Runtime.InteropServices.COMException] { }
    }
}

function Invoke-KitUpdateNotice([ValidateSet('foreground','minimized','minimized-foreground','closed')][string]$State) {
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
    $timing = Join-Path $LauncherHome 'startup-timing.log'
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
        Focus-KitPictureWindow $window $run.Process.Id
        $offerLine = 'update notice offer version=' + [regex]::Escape($version) + ' '
        if ($State -eq 'foreground') {
            [IO.File]::WriteAllText($presented, 'foreground')
            try {
                Wait-KitCondition {
                    if ([KitDesktop]::GetForegroundWindow() -ne $window) { throw 'Foreground changed during the update check.' }
                    (Find-KitControl $window 'Install Update' -Name) -and
                        (Select-String -LiteralPath $timing -Pattern ($offerLine + 'foregroundMatch=True minimized=False visible=True foreground=True eligible=False$') -Quiet)
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

        if ($State -eq 'minimized-foreground') {
            [KitDesktop]::MinimizeAndActivate($window)
            Wait-KitCondition {
                [KitDesktop]::IsIconic($window) -and [KitDesktop]::GetForegroundWindow() -eq $window
            } 5 'the minimized launcher to hold foreground' | Out-Null
        } else {
            if (![KitDesktop]::RequestMinimize($window)) { throw 'Windows did not queue the system minimize command.' }
            Wait-KitCondition { [KitDesktop]::IsIconic($window) } 5 'the launcher to minimize' | Out-Null
        }
        $foregroundAtRelease = [KitDesktop]::GetForegroundWindow() -eq $window
        "Iconic=$([KitDesktop]::IsIconic($window)). Foreground match at feed release=$foregroundAtRelease." |
            Add-Content -LiteralPath $receipt
        [IO.File]::WriteAllText($presented, $State)
        $library = Join-Path $LauncherHome 'launcher\launcher-library.json'
        $banner = $null
        $sawBannerWindow = $false
        $titleCondition = [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::NameProperty, "CoD2 Unbound $version is ready")
        $accepted = $false
        $closedAfterSubmission = $false
        $historyLine = "update notice history version=$version present=True"
        $showResult = 'update notice show version=' + [regex]::Escape($version) + ' id=[1-9][0-9]*$'
        $deadline = (Get-Date).AddSeconds(40)
        do {
            if (!$accepted -and (Test-Path -LiteralPath $timing)) {
                $accepted = Select-String -LiteralPath $timing -Pattern $showResult -Quiet
                if ($accepted) {
                    $deadline = (Get-Date).AddSeconds(4)
                }
            }
            if ($State -eq 'closed' -and $run -and $accepted -and
                ((Select-String -LiteralPath $timing -SimpleMatch $historyLine -Quiet) -or (Get-Date) -ge $deadline)) {
                Stop-KitLauncher $run $Results
                $run = $null
                $closedAfterSubmission = $true
                $deadline = (Get-Date).AddSeconds(4)
            }
            if (!$banner) { $banner = Find-KitUpdateToast $titleCondition ([ref]$sawBannerWindow) }
            if ($accepted -and $banner -and ($State -ne 'closed' -or !$run)) { break }
            [Windows.Forms.Application]::DoEvents()
            Start-Sleep -Milliseconds 100
        } while ((Get-Date) -lt $deadline -or ($State -eq 'closed' -and $run -and $accepted))

        "BannerWindowVisible=$sawBannerWindow. TestBannerIdentified=$([bool]$banner)." | Add-Content -LiteralPath $receipt
        if (!$accepted) {
            'Submission=not acknowledged. See startup-timing.log.' | Add-Content -LiteralPath $receipt
            throw 'The Test notice was not acknowledged by Windows.'
        }
        $match = if ($State -eq 'minimized-foreground') { 'True' } else { '(True|False)' }
        if (!(Select-String -LiteralPath $timing -Pattern ($offerLine + "foregroundMatch=$match minimized=True visible=(True|False) foreground=False eligible=True$") -Quiet)) {
            throw 'The launcher did not decide on the offer in the required minimized state. See startup-timing.log.'
        }
        if ($State -eq 'closed') {
            if (!$closedAfterSubmission) { throw 'The launcher did not close after the notice was submitted.' }
            'Closed after Windows acknowledged the notice.' | Add-Content -LiteralPath $receipt
        }
        'Submission=accepted.' | Add-Content -LiteralPath $receipt
        if (Test-Path -LiteralPath $library) {
            $remembered = $version -in (Get-Content -LiteralPath $library -Raw | ConvertFrom-Json).notifiedUpdates
            "Remembered=$remembered." | Add-Content -LiteralPath $receipt
        }
        $history = Select-String -LiteralPath $timing -SimpleMatch $historyLine -Quiet
        "HistoryPresent=$([bool]$history)." | Add-Content -LiteralPath $receipt
        if (!$banner) { return }
        if ($State -eq 'closed') {
            'Click=attended in a disposable offline account with the kit environment.' | Add-Content -LiteralPath $receipt
            return
        }
        $area = $banner.Title.Current.BoundingRectangle
        if ($area.IsEmpty) { throw 'The Test banner disappeared before pointer input.' }
        $x = [int]($area.Left + $area.Width / 2)
        $y = [int]($area.Top + $area.Height / 2)
        $toastWindow = [KitDesktop]::RootWindowAt($x,$y)
        $target = [Windows.Automation.AutomationElement]::FromPoint([Windows.Point]::new($x,$y))
        if (!$banner.Title.Equals($target) -or $toastWindow -ne $banner.Window -or
            ![KitDesktop]::ClickSurface($toastWindow,$x,$y,$false)) {
            throw 'The Test banner did not receive pointer input.'
        }
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
        'Clicked the Windows notification with pointer input and saw the Updates section selected.' | Add-Content -LiteralPath $receipt
        [KitDesktop]::CloseOwnedWindow($updates)
    } finally {
        try {
            if ($run) { Stop-KitLauncher $run $Results }
            Save-Text $timing (Join-Path $Results 'startup-timing.log')
        } finally {
            if (!$feed.Process.HasExited) { $feed.Process.Kill() }
            Complete-KitProcess $feed $entry.Evidence 8 | Out-Null
        }
    }
}

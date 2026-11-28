$InstallRoot = Join-Path (Join-Path $LocalData 'Programs') $FixtureName
$InstallKey = "HKCU:\$FixtureUninstall"
$InstallMarker = Join-Path $KitProfile 'owns-install.txt'
$KeyMarker = Join-Path $KitProfile 'owns-key.txt'
$LauncherHome = Join-Path $KitProfile 'launcher-profile'
$programs = [Environment]::GetFolderPath('Programs')
if (-not $programs -and $Mode -eq 'Status') { $programs = $Kit }
$StartGroup = Join-Path $programs $FixtureName
$EngineNames = @('cod2_win32.exe','cod2_win32_ded.exe','CoD2Unbound.Windows.exe')

function Stop-KitIfMissing([string]$Path, [string]$Reason) {
    if (-not (Test-Path -LiteralPath $Path)) { throw [NotSupportedException]::new($Reason) }
}

function Stop-KitIfNoGame {
    if (-not $GamePath) { throw [NotSupportedException]::new('A game folder is required. Give its path with -GamePath.') }
}

function Save-KitFirewall([string]$Label) {
    # Windows 10 refuses the rule list to a standard account. That must not fail
    # the game or server step that asked for it: record the refusal and go on.
    try {
        $records = foreach ($filter in Get-NetFirewallApplicationFilter -PolicyStore ActiveStore -ErrorAction Stop) {
            if (-not $filter.Program -or $filter.Program -eq 'Any') { continue }
            if ((Split-Path $filter.Program -Leaf) -notin $EngineNames) { continue }
            if (-not $filter.Program.StartsWith("$InstallRoot\", [StringComparison]::OrdinalIgnoreCase) -and
                -not $filter.Program.StartsWith("$Kit\", [StringComparison]::OrdinalIgnoreCase)) { continue }
            $rule = Get-NetFirewallRule -AssociatedNetFirewallApplicationFilter $filter -ErrorAction Stop
            [pscustomobject]@{ Program=$filter.Program; Direction=$rule.Direction.ToString()
                Enabled=$rule.Enabled.ToString(); Action=$rule.Action.ToString(); Profile=$rule.Profile.ToString() }
        }
    } catch {
        Protect-KitText "Windows did not let this account list firewall rules. $($_.Exception.Message)" |
            Set-Content -LiteralPath (Join-Path $Results "firewall-$Label-unreadable.txt") -Encoding UTF8
        return $false
    }
    Protect-KitText (ConvertTo-Json -InputObject @($records) -Depth 4) |
        Set-Content -LiteralPath (Join-Path $Results "firewall-$Label.json") -Encoding UTF8
    return $true
}

function Save-KitFirewallStep([string]$Label) {
    if (-not (Save-KitFirewall $Label)) {
        throw [NotSupportedException]::new('Windows does not let a standard account list firewall rules on this PC')
    }
}

function Get-KitLauncherSettings {
    @{
        COD2_UNBOUND_TEST_HOME = $LauncherHome
        COD2_UNBOUND_TEST_MASTER_PORT = '9'
        COD2_UNBOUND_TEST_UPDATE_ORIGIN = 'http://127.0.0.1:9'
        COD2_UNBOUND_TEST_RELEASES = Join-Path $KitProfile 'empty-releases.json'
        COD2_UNBOUND_TEST_CONFIG_DIRECTORY = Join-Path $Kit 'launcher-fixtures\directory.json'
        COD2_UNBOUND_TEST_UPDATE_VISUAL = '1'
        COD2_UNBOUND_TEST_WINDOWS_CREDENTIALS = if (Test-Path -LiteralPath $KeyMarker) { '1' } else { '0' }
        COD2_USAGE_URL = 'http://127.0.0.1:9'
        COD2_CRASH_URL = 'http://127.0.0.1:9'
    }
}

function Get-KitStartTarget {
    $exe = Join-Path $InstallRoot 'launcher\CoD2Unbound.Windows.exe'
    $shortcutPath = Join-Path $StartGroup "$FixtureName.lnk"
    Stop-KitIfMissing $InstallMarker 'The kit has no installation to start.'
    Stop-KitIfMissing $shortcutPath 'The Start shortcut is missing.'
    $shell = New-Object -ComObject WScript.Shell
    try {
        $shortcut = $shell.CreateShortcut($shortcutPath)
        try {
            if ($shortcut.TargetPath -ine $exe -or $shortcut.Arguments) {
                throw 'The Start shortcut does not point to the test launcher.'
            }
        } finally { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shortcut) }
    } finally { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell) }
    $exe
}

function Invoke-KitSetupVersion([bool]$Previous) {
    $source = Join-Path $Kit 'install'
    if ($Previous) { $source = Join-Path $source 'previous' }
    $setup = @(Get-ChildItem -LiteralPath $source -File -Filter '*-setup.exe')
    if ($setup.Count -ne 1) { throw 'Expected one Setup for this version.' }
    Assert-FixtureSetup $setup[0].FullName
    $exitCode = Invoke-Setup $setup[0].FullName 'setup' '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /CURRENTUSER /TASKS=links'
    if ($exitCode -ne 0) { throw "Setup exited with code $exitCode. See the Setup log." }
    if ((Read-InstalledVersion 'HKCU' $InstallRoot) -ne $setup[0].Name.Split('-')[2]) {
        throw 'Setup did not leave the requested version installed.'
    }
}

function Add-KitStep([string]$Id, [string]$Title, [scriptblock]$Action, [string]$Prompt, [switch]$Consent,
    [string]$Edition, [string]$Renderer, [string]$Variant) {
    [pscustomobject]@{ Id=$Id; Title=$Title; Action=$Action; Prompt=$Prompt; Consent=$Consent.IsPresent; Edition=$Edition; Renderer=$Renderer; Variant=$Variant }
}

$Steps = @(
    Add-KitStep 'preflight' 'Kit files and offline checks' {
        Save-KitPublicKeyStamp 'before'
        Save-KitPublicInstallState 'before'
        Save-KitPreflightComputer | Set-Content -LiteralPath (Join-Path $Results 'monitors.txt') -Encoding UTF8
        $gateways = @(Get-NetRoute | Where-Object { $_.DestinationPrefix -in @('0.0.0.0/0', '::/0') }).Count
        "Default routes: $gateways. Online run allowed by the driver: $($env:COD2_KIT_ONLINE -eq '1')." |
            Set-Content -LiteralPath (Join-Path $Results 'network.txt') -Encoding UTF8
        Test-KitFiles
        Assert-KitSlotBinaries
        [IO.File]::WriteAllText($UpdateTrustFile, (Get-KitUpdateTrust).Status)
        $captured = @()
        if ($GamePath) {
            [IO.File]::WriteAllText($GamePathFile, (Get-DataRoot $GamePath))
        } else {
            try { $captured = Invoke-DataCheck 'discovery-automatic' '' }
            catch { Protect-KitText $_.Exception.Message | Set-Content (Join-Path $Results 'discovery-error.txt') }
            $discovered = if ($captured.Path) { $captured.Path } else { Find-KitDefaultRetail }
            if ($discovered) { [IO.File]::WriteAllText($GamePathFile, (Get-DataRoot $discovered)) }
        }
        if ([IO.File]::Exists($GamePathFile)) {
            Save-InstallInventory ([IO.File]::ReadAllText($GamePathFile)) '' 'before'
        }
    }
    Add-KitStep 'machine' 'Windows, graphics, DirectX and sound devices' {
        Save-Machine
        Initialize-KitDesktop
        $dpiContext = [KitDesktop]::SetThreadDpiAwarenessContext([IntPtr](-4))
        $modes = try {
            foreach ($screen in [Windows.Forms.Screen]::AllScreens) {
                $dpi = [KitDesktop]::DisplayDpi($screen.Bounds.Left, $screen.Bounds.Top)
                "$($screen.DeviceName) $($screen.Bounds) primary=$($screen.Primary) dpi=$dpi"
                [KitDesktop]::Display($screen.DeviceName)
            }
        } finally {
            if ($dpiContext -ne [IntPtr]::Zero) { [void][KitDesktop]::SetThreadDpiAwarenessContext($dpiContext) }
        }
        $modes | Set-Content -LiteralPath (Join-Path $Results 'display-modes.txt')
        $probe = Join-Path $Kit 'probes\media-probe.exe'
        if (Test-Path -LiteralPath $probe) {
            $adapter = New-RunContext 'graphics-adapters'
            $running = Start-KitProcess $probe 'adapter' $adapter.Folder -NoWindow
            Complete-KitProcess $running $adapter.Evidence 10 | Out-Null
        }
        Get-CimInstance Win32_SoundDevice | Select-Object Name,Status,Manufacturer |
            ConvertTo-Json | Set-Content (Join-Path $Results 'sound-devices.json')
        Get-ChildItem -LiteralPath "$env:SystemRoot\System32" -File |
            Where-Object Name -match '^(d3d9|d3dcompiler_\d+|d3dx9_\d+|ddraw|dsound)\.dll$' |
            ForEach-Object { "$($_.Name) $($_.VersionInfo.FileVersion)" } |
            Set-Content (Join-Path $Results 'directx-files.txt')
    }
    Add-KitStep 'shaders' 'Stock shaders on the Windows compiler' { Invoke-KitShaderTable }
    Add-KitStep 'firewall-before' 'Firewall rules before first start' { Save-KitFirewallStep 'before' }
    Add-KitStep 'key' 'Windows reads the sample key' {
        if (Test-Path -LiteralPath $KeyMarker) {
            Invoke-TestKey 'clear'
        } else {
            Invoke-TestKey 'absent'
            [IO.File]::WriteAllText($KeyMarker, 'sample')
        }
        Invoke-TestKey 'absent'
        try {
            if ($GamePath) { Invoke-KitEmptySlotClients }
        } finally {
            Invoke-TestKey 'seed'
            Invoke-TestKey 'verify'
        }
    }
    foreach ($edition in 'original','cod2x') {
        $name = Get-EditionName $edition
        Add-KitStep "server-$edition" "$name server and saved Toujane match" {
            param($step)
            Stop-KitIfNoGame
            $extracted = Expand-ServerPackage
            try {
                Invoke-ServerCheck $step.Edition $GamePath $extracted 'server'
                Wait-KitCooling
                Invoke-Replay $step.Edition $GamePath 'standing' 'mp_toujane' 'dm' 12345 600 2 (Join-Path $Kit 'replays\standing\usercmds.txt') ''
            } finally { [void](Save-KitFirewall 'after-server') }
        } -Edition $edition
    }
    Add-KitStep 'install' 'Install the previous version for this user' {
        Assert-NoKitInstall
        [IO.File]::WriteAllText($InstallMarker, 'current user')
        Invoke-KitSetupVersion $true
    } 'Type YES to install the previous test version for this Windows account, or press Enter to skip' -Consent
    Add-KitStep 'update' 'Install the current version and check its files' {
        Stop-KitIfMissing $InstallMarker 'The previous version was skipped.'
        Invoke-KitSetupVersion $false
        Assert-InstalledFiles $InstallRoot (Join-Path $Kit 'portable')
    } 'Type YES to update this Windows account to the current test version, or press Enter to skip' -Consent
    Add-KitStep 'firewall-first-start' 'Start the first installed server and let Windows finish asking' { Invoke-KitFirewallWarmup }
    Add-KitStep 'security-note' 'Windows Security, SmartScreen or UAC' {} 'For this offline test, choose Cancel on a firewall prompt. Read any SmartScreen or UAC screen. Leave security settings alone. Write what you saw and chose, or type skip'
    Add-KitStep 'installed-signatures' 'Windows signature state for installed files' {
        Stop-KitIfMissing $InstallMarker 'There is no kit install to check.'
        $programs = @(Get-ChildItem -LiteralPath $InstallRoot -File -Recurse |
            Where-Object Extension -in '.exe','.dll','.cat')
        if (-not $programs.Count) { throw 'Setup left no files to check.' }
        Save-Signatures $programs.FullName (Join-Path $Results 'installed-signatures.json')
    }
    Add-KitStep 'launcher' 'First and later launcher starts from the installed shortcut' {
        Stop-KitIfNoGame
        Initialize-KitDesktop
        Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
        $exe = Get-KitStartTarget
        Save-KitGraphicsPreferences $InstallRoot $Results
        $settings = New-Folder (Join-Path $LauncherHome 'launcher')
        [IO.File]::WriteAllText((Join-Path $settings 'data-path.txt'), $GamePath)
        [IO.File]::WriteAllText((Join-Path $KitProfile 'empty-releases.json'), '[]')
        foreach ($number in 1..2) {
            $settings = Get-KitLauncherSettings
            Stop-KitIfMissing $KeyMarker 'The sample key check was skipped.'
            if ($number -eq 1) { Invoke-TestKey 'clear' }
            $run = Start-KitProcess $exe '' $LauncherHome $settings
            try {
                $trace = Join-Path $LauncherHome 'startup-timing.log'
                $id = $run.Process.Id
                Wait-KitCondition {
                    if ($run.Process.HasExited) { throw "The launcher exited with $($run.Process.ExitCode)." }
                    (Test-Path -LiteralPath $trace) -and
                    (Select-String -LiteralPath $trace -SimpleMatch " $id first content frame" -Quiet)
                } 60 'first launcher content' | Out-Null
                if ($number -eq 1) {
                    Complete-KitLauncherSetup (Get-KitLauncherWindow $run.Process) $Results
                    Invoke-TestKey 'verify'
                }
            } finally {
                if (-not $run.Process.HasExited) { [void]$run.Process.CloseMainWindow() }
                $done = Complete-KitProcess $run (New-Folder (Join-Path $Results "start-$number")) 12
                Save-Text $trace (Join-Path $Results 'startup-timing.log')
                if ($done.TimedOut -or $done.ExitCode -ne 0) { throw 'The launcher did not close cleanly.' }
            }
        }
    }
    Add-KitStep 'launcher-windows10' 'Open Setup and Home in both appearances with Windows 10 behavior' {
        Invoke-KitWindows10Startup
    }
    foreach ($surface in 'home','jump','hosting','configs','library','settings') {
        Add-KitStep "launcher-$surface" "Launcher $surface controls and pictures" {
            param($step)
            Invoke-KitSurface $step.Variant
        } -Variant $surface
    }
    Add-KitStep 'launcher-jump-pointer' 'Click Jump to results in dark and light' { Invoke-KitJumpPointer }
    Add-KitStep 'launcher-jump-pointer-repeat' 'Repeat the Jump to pointer sweep five times' { Invoke-KitJumpPointer -Rounds 5 }
    Add-KitStep 'launcher-sheet-dimming' 'Sample sheet dimming in Light, Dark and Match System' { Invoke-KitSheetDimming }
    Add-KitStep 'launcher-tab-names' 'Record Tab stops from Settings into each page and through Settings sections' { Invoke-KitTabNames }
    foreach ($opening in 'running','closed') {
        Add-KitStep "launcher-activation-$opening" "Open a config, recording and server link with the launcher $opening" {
            param($step)
            Invoke-KitDesktopActivation $step.Variant
        } -Variant $opening
    }
    Add-KitStep 'launcher-taskbar-caption' 'Capture the launcher taskbar button and window caption' { Invoke-KitTaskbarCaption }
    foreach ($state in 'foreground','minimized','closed') {
        Add-KitStep "launcher-update-notice-$state" "Check the local update notice with the launcher $state" {
            param($step)
            Invoke-KitUpdateNotice $step.Variant
        } -Variant $state
    }
    Add-KitStep 'resolution-original' 'First Original launch at the chosen resolution without a configuration question' {
        Invoke-KitFirstResolution
    }
    Add-KitStep 'servers-refresh-navigation' 'Navigate away and preserve the selected server during a 252-address refresh' {
        Invoke-KitRefreshNavigation
    }
    foreach ($edition in 'original','cod2x') {
        $name = Get-EditionName $edition
        foreach ($renderer in 'dx9','dx7') {
            Add-KitStep "direct-$edition-$renderer" "$name direct, $(Get-GraphicsMode $renderer), four maps and restart" {
                param($step)
                Stop-KitIfNoGame
                $script:RecordKitAudio = $step.Edition -eq 'original' -and $step.Renderer -eq 'dx9'
                try { Invoke-AutomaticClient $step.Edition $GamePath $step.Renderer }
                finally { $script:RecordKitAudio = $false; [void](Save-KitFirewall 'after-client') }
            } -Edition $edition -Renderer $renderer
        }
        foreach ($renderer in 'dx9','dx7') {
            Add-KitStep "installed-$edition-$renderer" "$name from the launcher, $(Get-GraphicsMode $renderer), four maps and restart" {
                param($step)
                Stop-KitIfNoGame
                Invoke-InstalledKitClient $step.Edition $step.Renderer
            } -Edition $edition -Renderer $renderer
        }
        foreach ($renderer in 'dx9','dx7') {
            Add-KitStep "demo-$edition-$renderer" "$name saved demo pictures in $(Get-GraphicsMode $renderer)" {
                param($step)
                Invoke-KitDemoFrames $step.Edition $step.Renderer
            } -Edition $edition -Renderer $renderer
        }
        foreach ($variant in 'desktop','lower') {
            Add-KitStep "full-$edition-$variant" "$name full-screen at $variant mode and Alt+Tab" {
                param($step)
                Stop-KitIfNoGame
                Initialize-KitDesktop
                $desktop = [KitDesktop]::CurrentMode()
                $mode = $desktop
                if ($step.Variant -eq 'lower') {
                    $w,$h = $desktop -split 'x'
                    $mode = [KitDesktop]::Display() | ForEach-Object {
                        if ($_ -match '^\d+ (\d+)x(\d+) 32bpp ' -and [int]$Matches[1] -lt [int]$w -and [int]$Matches[2] -le [int]$h) {
                            [pscustomobject]@{ Value="$($Matches[1])x$($Matches[2])"; Pixels=([int]$Matches[1]*[int]$Matches[2]) }
                        }
                    } | Sort-Object Pixels -Descending | Select-Object -First 1 -ExpandProperty Value
                    if (-not $mode) { throw [NotSupportedException]::new('The display does not list a lower mode.') }
                }
                Invoke-AutomaticClient $step.Edition $GamePath 'dx9' $mode
                if ([KitDesktop]::CurrentMode() -ne $desktop) { throw 'Windows did not return to the desktop resolution.' }
            } -Edition $edition -Variant $variant
        }
        Add-KitStep "borderless-$edition" "$name Direct3D 9 borderless at desktop size" {
            param($step)
            Stop-KitIfNoGame
            Initialize-KitDesktop
            Invoke-AutomaticClient $step.Edition $GamePath 'dx9' ([KitDesktop]::CurrentMode()) -Borderless
        } -Edition $edition
    }
    # The normal Direct3D 9 runs provide the first Carentan sample on each route.
    foreach ($route in 'direct','installed') {
        foreach ($attempt in 2,3) {
            Add-KitStep "carentan-$route-$attempt" "CoD2x Direct3D 9 Carentan load, $route attempt $attempt" {
                param($step)
                Stop-KitIfNoGame
                if ($step.Variant -eq 'direct') { Invoke-AutomaticClient 'cod2x' $GamePath 'dx9' -CarentanOnly }
                else { Invoke-InstalledKitClient 'cod2x' 'dx9' -CarentanOnly }
            } -Variant $route
        }
    }
    Add-KitStep 'audio' 'Sound loopback and volume ratio' {
        $audio = Join-Path $RunResults 'direct-original-dx9\game'
        $ratio = Join-Path $audio 'audio-ratio.txt'
        $reason = Join-Path $audio 'audio-error.txt'
        if (Test-Path -LiteralPath $ratio) {
            Copy-Item -LiteralPath $ratio -Destination $Results
        } elseif (Test-Path -LiteralPath $reason) {
            Copy-Item -LiteralPath $reason -Destination $Results
            $message = (Get-Content -LiteralPath $reason -Raw).Trim()
            if ($message -match 'no default sound output|No audio capture tool') {
                throw [NotSupportedException]::new($message)
            }
            throw $message
        } else { throw [NotSupportedException]::new('The direct game check did not reach sound capture.') }
    }
    foreach ($edition in 'original','cod2x') {
        $name = Get-EditionName $edition
        foreach ($route in 'connect','link','list','hosting','handoff') {
            Add-KitStep "match-$edition-$route" "$name loopback through $route" {
                param($step)
                Invoke-KitLoopback $step.Edition $step.Variant
            } -Edition $edition -Variant $route
        }
        foreach ($renderer in 'dx9','dx7') {
            $primary = "match-$edition-fullscreen"
            if ($renderer -eq 'dx7') { $primary += '-dx7' }
            foreach ($attempt in 1..3) {
                $id = if ($attempt -eq 1) { $primary } else { "$primary-$attempt" }
                Add-KitStep $id "$name exclusive $(Get-GraphicsMode $renderer) match and Alt+Tab, attempt $attempt of 3" {
                    param($step)
                    Stop-KitIfNoGame
                    Invoke-KitFullscreenMatch $step.Edition $step.Renderer
                } -Edition $edition -Renderer $renderer
            }
        }
    }
    foreach ($edition in 'original','cod2x') {
        Add-KitStep "open-demo-$edition" "Open the $(Get-EditionName $edition) demo through Windows" {
            param($step)
            Invoke-KitOpenDemo $step.Edition
        } -Edition $edition
    }
    Add-KitStep 'identity-cod2x' 'Compare the CoD2x identifier with the 1.4.6.8 algorithm on this PC' {
        Stop-KitIfNoGame
        Invoke-KitHwidCheck (Get-Binary 'cod2x') $GamePath
    }
    foreach ($attempt in 1..5) {
        Add-KitStep "update-$attempt" "In-app update $attempt of 5 from the installed shortcut" {
            Invoke-KitLauncherUpdate
        } "Type YES to reinstall the previous version for update $attempt of 5 and update it through the launcher, or press Enter to skip" -Consent
    }
    Add-KitStep 'interrupt' 'Interrupt one update with a restart' {
        Invoke-KitLauncherUpdate -Interrupt
    } 'Save your work and close other apps. Type YES to start an update and restart the PC during Setup, or press Enter to skip' -Consent
    Add-KitStep 'restore' 'Restore the old version and read the sample key after restart' {
        Invoke-KitRestore
    }
    Add-KitStep 'firewall-after' 'Firewall rules after game and server starts' { Save-KitFirewallStep 'after' }
    Add-KitStep 'security-answer' 'Security prompts actually shown' {} 'Write each Windows Security, SmartScreen or UAC message and your answer. If the firewall rule list was unavailable, record which program asked and whether you chose Allow or Cancel'
    Add-KitStep 'faults' 'Freeze recovery and crash with a local report receiver' {
        Invoke-KitFaults
    }
    Add-KitStep 'collect' 'Windows application and Defender events for the test installation' { Save-KitEvents }
    Add-KitStep 'collect-dumps' 'Collect Windows crash dumps' {
        throw [NotSupportedException]::new('The Windows dump folder belongs to the whole account. The kit uses its own local crash-report check.')
    }
    Add-KitStep 'comparison-pictures' 'Open game and reference pictures side by side' { Open-KitComparison }
    Add-KitStep 'looks' 'Picture, mouse and sound on this PC' {} 'Compare the game pictures. Try mouse turning and sound in both editions, then Alt+Tab out and back. Write one line about picture, mouse, sound and full-screen, or type skip'
    Add-KitStep 'narrator' 'Windows appearance and Narrator' {} 'Check Home and Settings > Privacy with Narrator. Write what read out of order, what was hard to reach, or what looked out of place. Type skip to skip'
    Add-KitStep 'cleanup' 'Uninstall and clear the test folders and sample key' {
        $failures = @()
        if (Test-Path -LiteralPath $InstallMarker) {
            try {
                Invoke-KitDemoUninstall
                Remove-Item -LiteralPath $InstallMarker
            } catch { $failures += $_.Exception.Message }
        }
        if (Test-Path -LiteralPath $KeyMarker) {
            try {
                Invoke-TestKey 'clear'
                Invoke-TestKey 'absent'
                Remove-Item -LiteralPath $KeyMarker
            }
            catch { $failures += $_.Exception.Message }
        }
        try { Invoke-TestKey 'absent' } catch { $failures += $_.Exception.Message }
        try {
            Remove-KitScratch $KitProfile
            if ((Test-Path -LiteralPath $KitScratchRoot) -and -not (Get-ChildItem -LiteralPath $KitScratchRoot -Force)) {
                [IO.Directory]::Delete($KitScratchRoot)
            }
        } catch { $failures += $_.Exception.Message }
        foreach ($check in { Assert-NoKitInstall },
            { Save-KitPublicKeyStamp 'after' }, { Save-KitPublicInstallState 'after' }) {
            try { & $check } catch { $failures += $_.Exception.Message }
        }
        if ($failures.Count) { throw ($failures -join ' ') }
    }
)

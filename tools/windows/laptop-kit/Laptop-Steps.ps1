$InstallRoot = Join-Path $LocalData 'Programs\CoD2 Unbound'
$InstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CoD2Unbound_is1'
$InstallMarker = Join-Path $KitProfile 'owns-install.txt'
$KeyMarker = Join-Path $KitProfile 'owns-key.txt'
$LauncherHome = Join-Path $KitProfile 'launcher-profile'
$programs = [Environment]::GetFolderPath('Programs')
if (-not $programs -and $Mode -eq 'Status') { $programs = $Kit }
$StartGroup = Join-Path $programs 'CoD2 Unbound'
$EngineNames = @('cod2_win32.exe','cod2_win32_ded.exe','CoD2Unbound.Windows.exe')

function Stop-KitIfMissing([string]$Path, [string]$Reason) {
    if (-not (Test-Path -LiteralPath $Path)) { throw [NotSupportedException]::new($Reason) }
}

function Stop-KitIfNoGame {
    if (-not $GamePath) { throw [NotSupportedException]::new('A game folder is required. Give its path with -GamePath.') }
}

function Save-KitFirewall([string]$Label) {
    $records = foreach ($filter in Get-NetFirewallApplicationFilter -PolicyStore ActiveStore) {
        if (-not $filter.Program -or $filter.Program -eq 'Any') { continue }
        if ((Split-Path $filter.Program -Leaf) -notin $EngineNames) { continue }
        $rule = Get-NetFirewallRule -AssociatedNetFirewallApplicationFilter $filter
        [pscustomobject]@{ Program=$filter.Program; Direction=$rule.Direction.ToString()
            Enabled=$rule.Enabled.ToString(); Action=$rule.Action.ToString(); Profile=$rule.Profile.ToString() }
    }
    Protect-KitText (ConvertTo-Json -InputObject @($records) -Depth 4) |
        Set-Content -LiteralPath (Join-Path $Results "firewall-$Label.json") -Encoding UTF8
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
    $shortcutPath = Join-Path $StartGroup 'CoD2 Unbound.lnk'
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
        $os = Get-CimInstance -ClassName Win32_OperatingSystem
        $version = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
        "$($os.Caption), $($version.DisplayVersion), build $($os.BuildNumber).$($version.UBR), Windows PowerShell $($PSVersionTable.PSVersion)" |
            Set-Content -LiteralPath (Join-Path $Results 'windows-version.txt') -Encoding UTF8
        Test-KitFiles
        $captured = @()
        if ($GamePath) {
            [IO.File]::WriteAllText($GamePathFile, (Get-DataRoot $GamePath))
        } else {
            try { $captured = Invoke-DataCheck 'discovery-automatic' '' }
            catch { Protect-KitText $_.Exception.Message | Set-Content (Join-Path $Results 'discovery-error.txt') }
            if ($captured.Path) { [IO.File]::WriteAllText($GamePathFile, (Get-DataRoot $captured.Path)) }
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
    Add-KitStep 'firewall-before' 'Firewall rules before first start' { Save-KitFirewall 'before' }
    Add-KitStep 'key' 'Windows reads the sample key' {
        Assert-NoSavedKitKey
        [IO.File]::WriteAllText($KeyMarker, 'sample')
        Invoke-TestKey 'seed'
        Invoke-TestKey 'check'
        Invoke-TestKey 'clear'
    }
    foreach ($edition in 'original','cod2x') {
        $name = Get-EditionName $edition
        Add-KitStep "server-$edition" "$name server and saved Toujane match" {
            param($step)
            Stop-KitIfNoGame
            $extracted = Expand-ServerPackage
            try {
                Invoke-ServerCheck $step.Edition $GamePath $extracted 'server'
                Invoke-Replay $step.Edition $GamePath 'standing' 'mp_toujane' 'dm' 12345 600 2 (Join-Path $Kit 'replays\standing\usercmds.txt') ''
            } finally { Save-KitFirewall 'after-server' }
        } -Edition $edition
    }
    Add-KitStep 'install' 'Install the previous version for this user' {
        $allRoot = Join-Path $env:ProgramFiles 'CoD2 Unbound'
        $allGroup = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'CoD2 Unbound'
        foreach ($other in @($InstallRoot, $InstallKey, $StartGroup, $allRoot, $allGroup,
            'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CoD2Unbound_is1',
            'HKCU:\Software\Classes\cod2unbound',
            'HKCU:\Software\Classes\cod2',
            'HKCU:\Software\Classes\cod2x')) {
            if (Test-Path -LiteralPath $other) {
                throw [NotSupportedException]::new('An existing install or link handler is present. Use a separate clean account.')
            }
        }
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
        $settings = New-Folder (Join-Path $LauncherHome 'launcher')
        [IO.File]::WriteAllText((Join-Path $settings 'data-path.txt'), $GamePath)
        [IO.File]::WriteAllText((Join-Path $KitProfile 'empty-releases.json'), '[]')
        foreach ($number in 1..2) {
            $run = Start-KitProcess $exe '' $LauncherHome (Get-KitLauncherSettings)
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
                    if (Test-Path -LiteralPath $KeyMarker) { Invoke-TestKey 'check' }
                }
            } finally {
                if (-not $run.Process.HasExited) { [void]$run.Process.CloseMainWindow() }
                $done = Complete-KitProcess $run (New-Folder (Join-Path $Results "start-$number")) 12
                Save-Text $trace (Join-Path $Results 'startup-timing.log')
                if ($done.TimedOut -or $done.ExitCode -ne 0) { throw 'The launcher did not close cleanly.' }
            }
        }
    }
    foreach ($edition in 'original','cod2x') {
        $name = Get-EditionName $edition
        foreach ($renderer in 'dx9','dx7') {
            Add-KitStep "direct-$edition-$renderer" "$name direct, $(Get-GraphicsMode $renderer), four maps and restart" {
                param($step)
                Stop-KitIfNoGame
                $script:RecordKitAudio = $step.Edition -eq 'original' -and $step.Renderer -eq 'dx9'
                try { Invoke-AutomaticClient $step.Edition $GamePath $step.Renderer }
                finally { $script:RecordKitAudio = $false; Save-KitFirewall 'after-client' }
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
        foreach ($route in 'connect','link','list','hosting') {
            Add-KitStep "match-$edition-$route" "$name loopback through $route" {
                param($step)
                Invoke-KitLoopback $step.Edition $step.Variant
            } -Edition $edition -Variant $route
        }
    }
    foreach ($edition in 'original','cod2x') {
        Add-KitStep "open-demo-$edition" "Open the $(Get-EditionName $edition) demo through Windows" {
            param($step)
            Invoke-KitOpenDemo $step.Edition
        } -Edition $edition
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
    Add-KitStep 'comparison-pictures' 'Open game and reference pictures side by side' { Open-KitComparison }
    Add-KitStep 'looks' 'Picture, mouse and sound on this PC' {} 'Compare the game pictures. Try mouse turning and sound in both editions, then Alt+Tab out and back. Write one line about picture, mouse, sound and full-screen, or type skip'
    Add-KitStep 'narrator' 'Windows appearance and Narrator' {} 'Check Home and Settings > Privacy with Narrator. Write what read out of order, what was hard to reach, or what looked out of place. Type skip to skip'
    Add-KitStep 'firewall-after' 'Firewall rules after game and server starts' { Save-KitFirewall 'after' }
    Add-KitStep 'security-answer' 'Security prompts actually shown' {} 'Write the Windows Security, SmartScreen or UAC wording and the button you pressed. If there was no prompt, write none'
    Add-KitStep 'faults' 'Freeze recovery and crash with a local report receiver' { Invoke-KitFaults }
    Add-KitStep 'collect' 'Windows application and Defender event entries' { Save-KitEvents }
    Add-KitStep 'cleanup' 'Uninstall and clear the test folders and sample key' {
        $failures = @()
        if (Test-Path -LiteralPath $InstallMarker) {
            try {
                $entry = Get-ItemProperty -LiteralPath $InstallKey -ErrorAction SilentlyContinue
                $uninstaller = Join-Path $InstallRoot 'unins000.exe'
                if ($entry) {
                    if ($entry.UninstallString.Trim('"') -ine $uninstaller) { throw 'The registered uninstaller changed.' }
                    $code = Invoke-Setup $uninstaller 'uninstall' '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART'
                    if ($code -ne 0) { throw "Uninstaller exit $code." }
                }
                if ((Test-Path -LiteralPath $InstallRoot) -or (Test-Path -LiteralPath $StartGroup) -or
                        (Test-Path -LiteralPath $InstallKey)) { throw 'Windows kept install files, shortcuts or registration.' }
                Remove-Item -LiteralPath $InstallMarker
            } catch { $failures += $_.Exception.Message }
        }
        if (Test-Path -LiteralPath $KeyMarker) {
            try {
                if (Test-SavedKitKey) { Invoke-TestKey 'clear' }
                Assert-NoSavedKitKey
                Remove-Item -LiteralPath $KeyMarker
            }
            catch { $failures += $_.Exception.Message }
        }
        if ($failures.Count) { throw ($failures -join ' ') }
        Remove-KitScratch $KitProfile
    } 'Type YES to uninstall the test copy and clear its sample data, or press Enter to keep it' -Consent
)

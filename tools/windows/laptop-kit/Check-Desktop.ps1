# Interactive engine checks run in the signed-in user's session.
function Initialize-KitDesktop {
    if ([Diagnostics.Process]::GetCurrentProcess().SessionId -eq 0) {
        throw 'Open the kit in the signed-in Windows desktop session.'
    }
    Add-Type -AssemblyName System.Drawing,System.Windows.Forms
    if (-not ('KitDesktop' -as [type])) {
        Add-Type -Path (Join-Path $Kit 'KitDesktop.cs') -ReferencedAssemblies System.Drawing,System.Windows.Forms
    }
}

function Wait-KitCondition([scriptblock]$Condition, [int]$Seconds, [string]$Description,
    [scriptblock]$OnTimeout, [switch]$SkipSecurity) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        $watch.Stop()
        if (-not $SkipSecurity) { Wait-KitSecurity }
        $watch.Start()
        $answer = & $Condition
        if ($answer) { return $answer }
        Start-Sleep -Milliseconds 250
    } while ($watch.Elapsed.TotalSeconds -lt $Seconds)
    if ($OnTimeout) { & $OnTimeout }
    throw "Timed out waiting for $Description."
}

function Wait-KitSecurity([scriptblock]$OnManagedFirewall) {
    if (-not ('KitDesktop' -as [type])) { return }
    $prompt = [KitDesktop]::SecurityPrompt()
    if (-not $prompt) { return }
    if ($script:KitAttentionTimedOut) { throw 'Windows is still waiting for a person at the laptop. See attention.txt.' }
    $instruction = switch ($prompt) {
        'windows-security' { 'Windows Security is open. If it is asking about firewall access, choose Cancel for this offline test.' }
        'uac' { 'Read the administrator approval prompt on the laptop. Cancel it for the per-user test.' }
        'smartscreen' { 'Read the SmartScreen message on the laptop. Leave the Windows security settings alone.' }
    }
    $attention = Join-Path $Results 'attention.txt'
    $instruction | Set-Content -LiteralPath $attention -Encoding UTF8
    Write-Host $instruction
    $managed = $prompt -eq 'windows-security' -and $OnManagedFirewall -and $env:COD2_KIT_ARM_REHEARSAL -eq '1'
    if ($managed) {
        'Windows Security appeared. The scratch policy will dismiss it when the rehearsal process exits.' |
            Set-Content -LiteralPath $attention -Encoding UTF8
        & $OnManagedFirewall
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(150)
    do {
        Start-Sleep -Milliseconds 500
        if (-not [KitDesktop]::SecurityPrompt()) {
            Remove-Item -LiteralPath $attention
            if ($managed) {
                'Scratch Windows Security cleared after the rehearsal process exited.' |
                    Set-Content -LiteralPath (Join-Path $Results 'managed-firewall.txt') -Encoding UTF8
                return $true
            }
            return
        }
    } while ([DateTime]::UtcNow -lt $deadline)
    $script:KitAttentionTimedOut = $true
    throw 'Windows is still waiting for a person at the laptop. See attention.txt.'
}

function Invoke-KitFirewallWarmup {
    Stop-KitIfNoGame
    Stop-KitIfMissing $InstallMarker 'The kit has no installation to run.'
    Initialize-KitDesktop
    Wait-KitSecurity
    $entry = New-RunContext 'firewall-first-server'
    $port = 29789
    $exe = Get-Binary 'original' -Server -Root $InstallRoot
    $password = [guid]::NewGuid().ToString('N')
    $bindAddress = if ($env:COD2_KIT_ONLINE -eq '1') { '127.0.0.1' } else { '0.0.0.0' }
    $arguments = (Get-EngineArgs $entry.Folder $GamePath -Address $bindAddress -Port $port) +
        " +set dedicated 1 +set rcon_password $password +map mp_toujane"
    $run = Start-KitProcess $exe $arguments $entry.Folder -NoWindow
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(45)
        $managed = {
            if (-not $run.Process.HasExited) { $run.Process.Kill(); [void]$run.Process.WaitForExit(8000) }
        }
        $ready = $false
        do {
            $managedPrompt = Wait-KitSecurity $managed
            if ($managedPrompt) { return }
            if ($run.Process.HasExited) { throw 'The first dedicated server exited before opening its socket.' }
            $ready = (Request-KitServer $port 'getstatus Kit') -match '\\mapname\\mp_toujane\\'
        } until ($ready -or [DateTime]::UtcNow -ge $deadline)
        if (-not $ready) { throw 'The first installed dedicated server did not answer.' }
        # Windows may put up its prompt shortly after the socket opens.
        for ($i = 0; $i -lt 12; $i++) {
            if (Wait-KitSecurity $managed) { return }
            Start-Sleep -Milliseconds 500
        }
    } finally {
        try {
            if (-not $run.Process.HasExited) { Stop-SetupServer $password $port }
        } finally { Complete-KitProcess $run $entry.Evidence 8 | Out-Null }
    }
}

function Focus-KitWindow([IntPtr]$Window) {
    Wait-KitCondition { [KitDesktop]::TryFocus($Window) } 8 'the test window to take focus' | Out-Null
}

function Send-KitCommand([IntPtr]$Window, [string]$Command) {
    Focus-KitWindow $Window
    [KitDesktop]::Tap(192)
    Start-Sleep -Milliseconds 300
    foreach ($letter in $Command.ToCharArray()) {
        if (-not [KitDesktop]::PostMessage($Window, 0x102, [IntPtr][int]$letter, [IntPtr]1)) {
            throw 'The game did not accept console input.'
        }
    }
    [KitDesktop]::Tap(13)
    Start-Sleep -Milliseconds 650
}

function Save-KitWindow([IntPtr]$Window, [string]$Path) {
    [KitDesktop]::SaveWindow($Window, $Path)
    "$([IO.Path]::GetFileName($Path)) $([KitDesktop]::WindowFacts($Window))" |
        Add-Content -LiteralPath (Join-Path (Split-Path $Path -Parent) 'window-pictures.txt') -Encoding UTF8
}

function Get-KitGameWindow($Process, [string]$ExclusiveEvidence) {
    Wait-KitCondition {
        if ($ExclusiveEvidence) { Assert-KitExclusiveForeground ([IntPtr]::Zero) $ExclusiveEvidence -ModalOnly }
        $Process.Refresh()
        if ($Process.HasExited) { throw "The game closed with exit code $($Process.ExitCode)." }
        $window = $Process.MainWindowHandle
        if ($window -ne [IntPtr]::Zero -and [KitDesktop]::WindowClass($window) -eq 'SDL_app') { $window }
    } 65 'the game window' -SkipSecurity:([bool]$ExclusiveEvidence)
}

function Save-KitFrame([IntPtr]$Window, [string]$GameHome, [string]$Evidence, [string]$Name, [switch]$NativeConsole) {
    $shot = Join-Path $GameHome "main\screenshots\$Name.jpg"
    if (Test-Path -LiteralPath $shot) { Remove-Item -LiteralPath $shot }
    if ($NativeConsole) {
        Assert-KitExclusiveForeground $Window $Evidence
        $dumpName = "$Name-console.txt"
        [KitDesktop]::SendGameConsole($Window, "condump $dumpName; screenshotJpeg $Name")
    } else {
        Send-KitCommand $Window "/toggleconsole; screenshotJpeg $Name"
    }
    Wait-KitCondition {
        if ($NativeConsole) { Assert-KitExclusiveForeground $Window $Evidence }
        Test-Path -LiteralPath $shot -PathType Leaf
    } 12 "$Name game picture" -SkipSecurity:$NativeConsole | Out-Null
    Copy-Item -LiteralPath $shot -Destination $Evidence
    if ($NativeConsole) {
        Save-Text (Join-Path $GameHome "main\$dumpName") (Join-Path $Evidence $dumpName)
    }
    Save-KitWindow $Window (Join-Path $Evidence "$Name-window.png")
}

function Enter-KitPlay([IntPtr]$Window) {
    Focus-KitWindow $Window
    [KitDesktop]::ClickGameContinue($Window)
    Start-Sleep -Milliseconds 900
    foreach ($key in 13,50,49,49,49) {
        Focus-KitWindow $Window
        [KitDesktop]::Tap($key)
        Start-Sleep -Milliseconds 650
    }
}

function Set-KitHarborView([IntPtr]$Window, [string]$Log, [string]$Phase) {
    Start-Sleep -Milliseconds 1500
    $toggleNoclip = $true
    foreach ($attempt in 1..3) {
        $start = @(Get-Content -LiteralPath $Log).Count
        Enter-KitPlay $Window
        Focus-KitWindow $Window
        $camera = 'setviewpos -11736 -7472 253 90; cg_draw2D 0; cg_drawGun 0'
        # The native console has one edit box. Submit the toggle and move together.
        if ($toggleNoclip) { $camera = 'noclip; ' + $camera }
        [KitDesktop]::SendGameConsole($Window, $camera)
        Start-Sleep -Milliseconds 700
        [KitDesktop]::SendGameConsole($Window, 'viewpos')
        Start-Sleep -Milliseconds 350
        $view = Get-Content -LiteralPath $Log | Select-Object -Skip $start |
            Where-Object { $_ -match '^\(-?\d+ -?\d+ -?\d+\)\s*:\s*-?\d+\s*$' } | Select-Object -Last 1
        "$Phase attempt $attempt view=$view" | Add-Content -LiteralPath (Join-Path $Results 'water-view.txt')
        if ($view -match '^\(-11736 -7472 253\)\s*:\s*90\s*$') { return }
        $toggleNoclip = $view -match '^\(-11736 -7472 (-?\d+)\)' -and [int]$Matches[1] -lt 253
    }
    throw "The game did not reach the Harbor water viewpoint $Phase. No picture was taken for this phase."
}

function Invoke-KitHarborWater([string]$Edition, [string]$Renderer) {
    Stop-KitIfNoGame
    Wait-KitCooling
    Initialize-KitDesktop
    $gameHome = Join-Path $Private 'game'
    New-TestProfile $gameHome
    $arguments = (Get-EngineArgs $gameHome $GamePath) +
        " +set r_rendererPreference $Renderer +set r_aaSamples 1 +set r_fullscreen 0 +set r_mode 640x480" +
        ' +set net_noudp 1 +set com_introPlayed 1 +set com_maxfps 30 +set logfile 2 +set snd_volume 0 +set g_gametype dm +devmap mp_harbor'
    $log = Join-Path $gameHome 'main\console_mp.log'
    $run = Start-KitProcess (Get-Binary $Edition -Root $InstallRoot) $arguments $Private @{COD2_CRASH_NOPOPUP='1'}
    try {
        $window = Get-KitGameWindow $run.Process
        Wait-KitCondition {
            (Test-Path -LiteralPath $log) -and
                (Select-String -LiteralPath $log -SimpleMatch "Using Direct3D $($Renderer.Substring(2)) code path" -Quiet) -and
                (Select-String -LiteralPath $log -SimpleMatch 'CL_InitCGame:' -Quiet)
        } 45 'Harbor and the selected renderer' | Out-Null
        Set-KitHarborView $window $log 'before restart'
        foreach ($name in 'water-1','water-2','water-3','water-after-restart') {
            if ($name -eq 'water-after-restart') {
                $loads = @(Select-String -LiteralPath $log -SimpleMatch 'CL_InitCGame:').Count
                Focus-KitWindow $window
                [KitDesktop]::SendGameConsole($window,'noclip; map_restart')
                Wait-KitCondition {
                    if ($run.Process.HasExited) { throw 'The game stopped during the Harbor restart.' }
                    @(Select-String -LiteralPath $log -SimpleMatch 'CL_InitCGame:').Count -gt $loads
                } 45 'Harbor to restart' | Out-Null
                Set-KitHarborView $window $log 'after restart'
            } elseif ($name -ne 'water-1') { Start-Sleep -Seconds 3 }
            $shot = Join-Path $gameHome "main\screenshots\$name.tga"
            Focus-KitWindow $window
            [KitDesktop]::SendGameConsole($window,"screenshot $name")
            Wait-KitCondition {
                (Test-Path -LiteralPath $shot) -and (Get-Item -LiteralPath $shot).Length -ge 921618
            } 10 "the $name picture" | Out-Null
            Copy-Item -LiteralPath $shot -Destination $Results
        }
        $samples = @((Get-FileHash -LiteralPath (Get-ChildItem -Path $Results -Filter 'water-[1-3].tga').FullName).Hash | Select-Object -Unique)
        if ($samples.Count -lt 2) { throw 'The three Harbor water pictures were identical.' }
    } finally {
        if (!$run.Process.HasExited) { [void]$run.Process.CloseMainWindow() }
        Complete-KitProcess $run $Results 8 | Out-Null
        Save-GameEvidence $gameHome $Results
    }
}

function Enable-KitGameConsole([IntPtr]$Window, [string]$GameHome, [string]$Evidence) {
    $console = Join-Path $GameHome 'main\console_mp.log'
    if (Test-Path -LiteralPath $console) {
        if (Select-String -LiteralPath $console -SimpleMatch 'Working directory:' -Quiet) { return $console }
    }
    # The player entry point need not start with disk logging on. Save the
    # existing engine console before enabling logging for the rest of this run.
    $boot = Join-Path $GameHome 'main\kit-boot.txt'
    if (Test-Path -LiteralPath $boot) { Remove-Item -LiteralPath $boot }
    Focus-KitWindow $Window
    [KitDesktop]::Tap(27)
    Start-Sleep -Seconds 2
    Send-KitCommand $Window '/toggleconsole; condump kit-boot.txt; set logfile 2; logfile'
    Wait-KitCondition {
        (Test-Path -LiteralPath $boot) -and (Test-Path -LiteralPath $console)
    } 18 'the engine console dump and disk log' | Out-Null
    $output = Join-Path $Evidence 'boot-console.txt'
    Save-Text $boot $output
    $output
}

function Save-KitMapStall($Game, [string]$Log, [string]$Evidence, [string]$Label) {
    if ($Game.HasExited) { return }
    $privateDiagnostic = New-Folder (Join-Path $Evidence "private-map-stall-$Label")
    try {
        if (Test-Path -LiteralPath $Log -PathType Leaf) {
            $tail = (Get-Content -LiteralPath $Log -Tail 80) -join "`r`n"
            Protect-KitText $tail | Set-Content -LiteralPath (Join-Path $privateDiagnostic 'console-tail.txt') -Encoding UTF8
        }
    } catch { Write-Warning 'The kit game console tail could not be read.' }
    $image = Join-Path $Kit 'probes\stack-probe.exe'
    $argsText = "$($Game.Id) $($Game.StartTime.ToFileTimeUtc())"
    $probe = Start-KitProcess $image $argsText (Split-Path -Parent $Log) -NoWindow
    try {
        if (-not $probe.Process.WaitForExit(8000)) {
            $probe.Process.Kill()
            [void]$probe.Process.WaitForExit(2000)
            throw 'The stack probe timed out.'
        }
        $stacks = $probe.Output.GetAwaiter().GetResult()
        if ($probe.Process.ExitCode -ne 0) { throw 'The stack probe found no readable game stacks.' }
        Protect-KitText $stacks | Set-Content -LiteralPath (Join-Path $privateDiagnostic 'thread-stacks.txt') -Encoding UTF8
    } finally { $probe.Process.Dispose() }
}

function Wait-KitMapLoad([scriptblock]$Loaded, [int]$Seconds, [string]$Label, $Game,
    [string]$Log, [string]$Evidence, [switch]$Exclusive) {
    Wait-KitCondition {
        if ($Game.HasExited) { throw "The game closed with exit code $($Game.ExitCode) while loading $Label." }
        & $Loaded
    } $Seconds "$Label to load" -OnTimeout {
        try { Save-KitMapStall $Game $Log $Evidence $Label }
        catch { Write-Warning (Protect-KitText $_.Exception.Message) }
    } -SkipSecurity:$Exclusive | Out-Null
}

function Invoke-MapPictures([string]$GameHome, [string]$Evidence, [IntPtr]$Window, [string]$Renderer,
    $Trace, $Game, [switch]$FromLauncher, [switch]$CarentanOnly) {
    $log = Join-Path $GameHome 'main\console_mp.log'
    $boot = $log
    if ($FromLauncher) {
        $boot = Enable-KitGameConsole $Window $GameHome $Evidence
    } else {
        Wait-KitCondition {
            (Test-Path -LiteralPath $log) -and (Select-String -LiteralPath $log -SimpleMatch 'Working directory:' -Quiet)
        } 40 'the startup log' | Out-Null
    }
    if (-not (Select-String -LiteralPath $boot -SimpleMatch "Using Direct3D $($Renderer.Substring(2)) code path" -Quiet)) {
        throw "The engine did not report $(Get-GraphicsMode $Renderer)."
    }
    Focus-KitWindow $Window
    [KitDesktop]::Tap(27)
    $notes = Join-Path $Evidence 'input.txt'
    "Menu clip $([KitDesktop]::Clip())" | Set-Content -LiteralPath $notes
    Save-KitFrame $Window $GameHome $Evidence 'kit-menu'
    $maps = if ($CarentanOnly) { 'mp_carentan' } else { 'mp_toujane','restart','mp_harbor','mp_carentan','mp_burgundy' }
    foreach ($map in $maps) {
        $marker = 'CL_InitCGame:'
        $count = @(Select-String -LiteralPath $log -SimpleMatch $marker).Count
        $command = if ($map -eq 'restart') { 'map_restart' } else { "devmap $map" }
        $load = [Diagnostics.Stopwatch]::StartNew()
        Send-KitCommand $Window "/toggleconsole; $command"
        Wait-KitMapLoad {
            @(Select-String -LiteralPath $log -SimpleMatch $marker).Count -gt $count
        } 55 $map $Game $log $Evidence
        $load.Stop()
        if ($map -eq 'mp_carentan') {
            "mp_carentan $($load.ElapsedMilliseconds) ms from sending the command to observing CL_InitCGame." |
                Set-Content -LiteralPath (Join-Path $Evidence 'map-load-times.txt') -Encoding UTF8
        }
        if ($map -ne 'restart') {
            Start-Sleep -Seconds 2
            Enter-KitPlay $Window
        }
        Save-KitFrame $Window $GameHome $Evidence "kit-$map"
        if ($map -eq 'mp_toujane') {
            Start-KitFrameSample $Trace $Window $log
            "Play clip $([KitDesktop]::Clip())" | Add-Content -LiteralPath $notes
            Send-KitCommand $Window '/toggleconsole; viewpos'
            Focus-KitWindow $Window
            [KitDesktop]::KeyEvent(87, $false)
            try { Start-Sleep -Milliseconds 650 } finally { [KitDesktop]::KeyEvent(87, $true) }
            [KitDesktop]::Move(35, 12)
            [KitDesktop]::ClickButton($false)
            try { Start-Sleep -Milliseconds 450 } finally { [KitDesktop]::ClickButton($true) }
            Send-KitCommand $Window '/toggleconsole; viewpos'
            $views = @(Select-String -LiteralPath $log -Pattern '^\(-?\d+ -?\d+ -?\d+\) : -?\d+\s*$' |
                Select-Object -Last 2 -ExpandProperty Line)
            $views | Add-Content -LiteralPath $notes
            if ($views.Count -lt 2 -or $views[0] -eq $views[1]) {
                'The recorded position and heading did not change after input.' | Add-Content -LiteralPath $notes
            }
            Save-KitFrame $Window $GameHome $Evidence 'kit-after-input'
            $Trace.End = [Diagnostics.Stopwatch]::GetTimestamp()
            if ($script:RecordKitAudio) {
                try { Invoke-KitAudio $Window $Evidence }
                catch { Protect-KitText $_.Exception.Message | Set-Content (Join-Path $Evidence 'audio-error.txt') }
            }
        }
    }
}

function Assert-KitExclusiveForeground([IntPtr]$Window, [string]$Evidence, [switch]$ModalOnly, [switch]$AllowShell) {
    $front = [KitDesktop]::GetForegroundWindow()
    if ($front -eq [IntPtr]::Zero -or $front -eq $Window) { return }
    $owner = [KitDesktop]::WindowPid($front)
    if ($owner -in @([KitDesktop]::WindowPid($Window), [Diagnostics.Process]::GetCurrentProcess().Id)) { return }
    $process = Get-Process -Id $owner -ErrorAction SilentlyContinue
    if (-not $process) { return }
    try {
        $name = $process.ProcessName
        if ($AllowShell -and $name -in 'explorer','ShellExperienceHost','StartMenuExperienceHost','SearchHost','dwm') { return }
        if ($ModalOnly -and [KitDesktop]::WindowClass($front) -ne '#32770' -and -not [KitDesktop]::SecurityPrompt()) { return }
        $message = "$name has the foreground. The full-screen check stopped. Dismiss or move that window yourself before retrying."
        $message | Set-Content -LiteralPath (Join-Path $Evidence 'foreground-block.txt') -Encoding UTF8
        throw [NotSupportedException]::new($message)
    } finally { $process.Dispose() }
}

function Invoke-KitTaskSwitch([IntPtr]$Window, [string]$HomePath, [string]$Evidence, [string]$Label,
    [int]$SwitchTargetPid = 0) {
    $switches = Join-Path $Evidence 'fullscreen-switch.txt'
    $mode = [KitDesktop]::CurrentMode()
    Assert-KitExclusiveForeground $Window $Evidence
    [KitDesktop]::Chord(18, 9)
    $switchDescription = if ($SwitchTargetPid) { 'Alt+Tab to reach the kit switch window' } else { 'Alt+Tab to switch away' }
    Wait-KitCondition {
        Assert-KitExclusiveForeground $Window $Evidence -ModalOnly
        $current = [KitDesktop]::GetForegroundWindow()
        $current -ne $Window -and $current -ne [IntPtr]::Zero -and
            [KitDesktop]::WindowClass($current) -ne 'ForegroundStaging' -and
            (-not $SwitchTargetPid -or [KitDesktop]::WindowPid($current) -eq $SwitchTargetPid)
    } 8 $switchDescription -SkipSecurity | Out-Null
    "Alt+Tab switched away. Game minimized $([KitDesktop]::IsIconic($Window))." |
         Set-Content -LiteralPath $switches -Encoding UTF8
    [KitDesktop]::Chord(18, 9)
    try {
        Wait-KitCondition {
            Assert-KitExclusiveForeground $Window $Evidence -ModalOnly
            [KitDesktop]::GetForegroundWindow() -eq $window
        } 8 'Alt+Tab to return' -OnTimeout { Assert-KitExclusiveForeground $Window $Evidence -AllowShell } -SkipSecurity | Out-Null
        'Alt+Tab returned foreground to the game.' | Add-Content -LiteralPath $switches
    } catch {
        if ($_.Exception -is [NotSupportedException] -or $SwitchTargetPid) { throw }
        'Trying a direct restore from the Windows shell.' | Add-Content -LiteralPath $switches
        Focus-KitWindow $window
    }
    Wait-KitCondition {
        Assert-KitExclusiveForeground $Window $Evidence
        [KitDesktop]::GetForegroundWindow() -eq $Window -and -not [KitDesktop]::IsIconic($Window) -and
            [KitDesktop]::CurrentMode() -eq $mode
    } 8 'the game to restore its exclusive display mode' -SkipSecurity | Out-Null
    # During mode recovery Windows can discard console keystrokes. Ask the
    # running client for the picture through its own native console.
    Save-KitFrame $Window $HomePath $Evidence "$Label-return" -NativeConsole
    "The restored game rendered at $([KitDesktop]::CurrentMode())." | Add-Content -LiteralPath $switches
}

function Start-KitTaskSwitchWindow([string]$Folder) {
    # An otherwise empty desktop gives Alt+Tab no window to switch to.
    $script = @'
Add-Type -AssemblyName System.Windows.Forms
$form = [Windows.Forms.Form]::new()
$form.Text = 'CoD2 Unbound full-screen test'
try { [Windows.Forms.Application]::Run($form) }
finally { $form.Dispose() }
'@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script))
    $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    Start-KitProcess $powershell "-NoProfile -NonInteractive -STA -EncodedCommand $encoded" $Folder -NoWindow
}

function Invoke-AutomaticClient([string]$Edition, [string]$GamePath, [string]$Renderer, [string]$DisplayMode,
    [switch]$Borderless, [switch]$CarentanOnly) {
    Initialize-KitDesktop
    $entry = New-RunContext 'game'
    if ($DisplayMode -and -not $Borderless) {
        Assert-KitExclusiveForeground ([IntPtr]::Zero) $entry.Evidence -ModalOnly
    }
    $homePath = Join-Path $entry.Folder 'profile'
    New-TestProfile $homePath
    $argsText = (Get-EngineArgs $homePath $GamePath) +
        " +set r_rendererPreference $Renderer +set r_aaSamples 1 +set net_noudp 1 +set com_introPlayed 1 +set g_gametype dm"
    if ($DisplayMode) { $argsText += " +set r_fullscreen 1 +set r_borderless $([int]$Borderless.IsPresent) +set r_mode $DisplayMode" }
    else { $argsText += ' +set r_fullscreen 0 +set r_mode 800x600' }
    $trace = $null
    $settings = @{}
    if (-not $DisplayMode) {
        $trace = New-KitFrameTrace $entry.Evidence
        $settings.COD2_FRAME_TRACE_FILE = $trace.Path
    }
    $run = $switchWindow = $null
    $window = [IntPtr]::Zero
    try {
        $exclusive = if ($DisplayMode -and -not $Borderless) { $entry.Evidence }
        if ($exclusive) {
            $switchWindow = Start-KitTaskSwitchWindow $entry.Folder
            Wait-KitCondition {
                $switchWindow.Process.Refresh()
                if ($switchWindow.Process.HasExited) { throw 'The task-switch window exited during startup.' }
                $switchWindow.Process.MainWindowHandle -ne [IntPtr]::Zero
            } 10 'the task-switch window' | Out-Null
        }
        $run = Start-KitProcess (Get-Binary $Edition) $argsText $entry.Folder $settings
        $window = Get-KitGameWindow $run.Process $exclusive
        if ($DisplayMode) {
            $log = Join-Path $homePath 'main\console_mp.log'
            Wait-KitCondition {
                if ($exclusive) { Assert-KitExclusiveForeground $window $entry.Evidence -ModalOnly }
                (Test-Path -LiteralPath $log) -and (Select-String -LiteralPath $log -SimpleMatch 'Working directory:' -Quiet)
            } 40 'the completed game startup' -SkipSecurity:([bool]$exclusive) | Out-Null
            if (-not $Borderless) { Assert-KitExclusiveForeground $window $entry.Evidence }
            Send-KitCommand $window '/toggleconsole; r_fullscreen; r_mode'
            $mode = [KitDesktop]::CurrentMode()
            if ($mode -ne $DisplayMode) { throw "The display reported $mode after requesting $DisplayMode." }
            if ($Borderless) {
                $facts = [KitDesktop]::WindowFacts($window)
                $facts | Set-Content -LiteralPath (Join-Path $entry.Evidence 'borderless.txt') -Encoding UTF8
                if (-not [KitDesktop]::FillsMonitorWithoutFrame($window)) { throw "Borderless window geometry did not match the monitor. $facts" }
                Save-KitFrame $window $homePath $entry.Evidence 'kit-borderless'
            } else {
                "Menu cursor clip $([KitDesktop]::Clip())" |
                    Set-Content -LiteralPath (Join-Path $entry.Evidence 'fullscreen-input.txt') -Encoding UTF8
                Save-KitFrame $window $homePath $entry.Evidence 'kit-fullscreen'
                Invoke-KitTaskSwitch $window $homePath $entry.Evidence 'kit-fullscreen'
            }
        } else {
            Invoke-MapPictures $homePath $entry.Evidence $window $Renderer $trace $run.Process -CarentanOnly:$CarentanOnly
        }
    } finally {
        try {
            if ($run) {
                if (-not $run.Process.HasExited) { [KitDesktop]::CloseOwnedWindow($window) }
                $done = Complete-KitProcess $run $entry.Evidence 12
                Save-GameEvidence $homePath $entry.Evidence
                Save-KitFrameTrace $trace $entry.Evidence (Join-Path $homePath 'main\console_mp.log')
            }
        } finally {
            if ($switchWindow) {
                if (-not $switchWindow.Process.HasExited) { [void]$switchWindow.Process.CloseMainWindow() }
                Complete-KitProcess $switchWindow (New-Folder (Join-Path $entry.Evidence 'switch-window')) 5 | Out-Null
            }
        }
    }
    if ($done.TimedOut -or $done.ExitCode -ne 0) { throw 'The game did not close cleanly.' }
}

function Invoke-KitAudio([IntPtr]$Window, [string]$Evidence) {
    Assert-Offline
    $exe = Join-Path $Kit 'probes\media-probe.exe'
    Stop-KitIfMissing $exe 'No audio capture tool in the kit.'
    $levels = [ordered]@{ high='0.75'; low='0.25' }
    $measure = @{}
    foreach ($label in $levels.Keys) {
        Send-KitCommand $Window "/toggleconsole; set snd_volume $($levels[$label]); snd_volume"
        $entry = New-RunContext "audio-$label"
        $run = Start-KitProcess $exe 'audio clip.wav 6' $entry.Folder -NoWindow
        try {
            Start-Sleep -Seconds 1
            Focus-KitWindow $Window
            [KitDesktop]::ClickButton($false)
            try { Start-Sleep -Milliseconds 2200 } finally { [KitDesktop]::ClickButton($true) }
        } finally { $done = Complete-KitProcess $run $entry.Evidence 10 }
        $clip = Join-Path $entry.Folder 'clip.wav'
        if (Test-Path -LiteralPath $clip) { Copy-Item -LiteralPath $clip -Destination (Join-Path $entry.Evidence 'clip.wav') }
        if (Select-String -LiteralPath (Join-Path $entry.Evidence 'stderr.txt') -Pattern 'GetDefaultAudioEndpoint: 0x80070490' -Quiet) {
            throw [NotSupportedException]::new('Windows has no default sound output.')
        }
        if ($done.TimedOut -or $done.ExitCode -ne 0) { throw 'Windows loopback capture failed. See the audio logs.' }
        $rms = Select-String -LiteralPath $done.Stdout -Pattern '^RMS=(\d+\.\d+)$'
        if (-not $rms) { throw 'The audio format did not yield an amplitude measurement.' }
        $measure[$label] = [double]::Parse($rms.Matches[0].Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture)
    }
    if ($measure.low -le 0 -or $measure.high -le 0) { throw 'The render endpoint returned a silent clip.' }
    "0.75 RMS=$($measure.high); 0.25 RMS=$($measure.low); high/low=$($measure.high / $measure.low). Repeated firing should yield roughly 3:1." |
        Set-Content -LiteralPath (Join-Path $Evidence 'audio-ratio.txt') -Encoding UTF8
}

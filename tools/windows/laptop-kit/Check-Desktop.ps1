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

function Wait-KitCondition([scriptblock]$Condition, [int]$Seconds, [string]$Description) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        $watch.Stop()
        Wait-KitSecurity
        $watch.Start()
        $answer = & $Condition
        if ($answer) { return $answer }
        Start-Sleep -Milliseconds 250
    } while ($watch.Elapsed.TotalSeconds -lt $Seconds)
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
    $arguments = (Get-EngineArgs $entry.Folder $GamePath -Address '0.0.0.0' -Port $port) +
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

function Get-KitGameWindow($Process) {
    Wait-KitCondition {
        $Process.Refresh()
        if ($Process.HasExited) { throw "The game closed with exit code $($Process.ExitCode)." }
        $window = $Process.MainWindowHandle
        if ($window -ne [IntPtr]::Zero -and [KitDesktop]::WindowClass($window) -eq 'SDL_app') { $window }
    } 65 'the game window'
}

function Save-KitFrame([IntPtr]$Window, [string]$GameHome, [string]$Evidence, [string]$Name) {
    $shot = Join-Path $GameHome "main\screenshots\$Name.jpg"
    if (Test-Path -LiteralPath $shot) { Remove-Item -LiteralPath $shot }
    Send-KitCommand $Window "/toggleconsole; screenshotJpeg $Name"
    Wait-KitCondition { Test-Path -LiteralPath $shot -PathType Leaf } 12 "$Name game picture" | Out-Null
    Copy-Item -LiteralPath $shot -Destination $Evidence
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

function Invoke-MapPictures([string]$GameHome, [string]$Evidence, [IntPtr]$Window, [string]$Renderer, [switch]$FromLauncher) {
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
    foreach ($map in 'mp_toujane','restart','mp_harbor','mp_carentan','mp_burgundy') {
        $marker = 'CL_InitCGame:'
        $count = @(Select-String -LiteralPath $log -SimpleMatch $marker).Count
        $command = if ($map -eq 'restart') { 'map_restart' } else { "devmap $map" }
        Send-KitCommand $Window "/toggleconsole; $command"
        Wait-KitCondition {
            @(Select-String -LiteralPath $log -SimpleMatch $marker).Count -gt $count
        } 55 "$map to load" | Out-Null
        if ($map -ne 'restart') {
            Start-Sleep -Seconds 2
            Enter-KitPlay $Window
        }
        Save-KitFrame $Window $GameHome $Evidence "kit-$map"
        if ($map -eq 'mp_toujane') {
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
            if ($script:RecordKitAudio) {
                try { Invoke-KitAudio $Window $Evidence }
                catch { Protect-KitText $_.Exception.Message | Set-Content (Join-Path $Evidence 'audio-error.txt') }
            }
        }
    }
}

function Invoke-AutomaticClient([string]$Edition, [string]$GamePath, [string]$Renderer, [string]$DisplayMode) {
    Initialize-KitDesktop
    $entry = New-RunContext 'game'
    $homePath = Join-Path $entry.Folder 'profile'
    New-TestProfile $homePath
    $argsText = (Get-EngineArgs $homePath $GamePath) +
        " +set r_rendererPreference $Renderer +set r_aaSamples 1 +set net_noudp 1 +set com_introPlayed 1 +set g_gametype dm"
    if ($DisplayMode) { $argsText += " +set r_fullscreen 1 +set r_mode $DisplayMode" }
    else { $argsText += ' +set r_fullscreen 0 +set r_mode 800x600' }
    $run = Start-KitProcess (Get-Binary $Edition) $argsText $entry.Folder
    $window = [IntPtr]::Zero
    try {
        $window = Get-KitGameWindow $run.Process
        if ($DisplayMode) {
            $log = Join-Path $homePath 'main\console_mp.log'
            Wait-KitCondition {
                (Test-Path -LiteralPath $log) -and (Select-String -LiteralPath $log -SimpleMatch 'Working directory:' -Quiet)
            } 40 'the completed game startup' | Out-Null
            Send-KitCommand $window '/toggleconsole; r_fullscreen; r_mode'
            $mode = [KitDesktop]::CurrentMode()
            if ($mode -ne $DisplayMode) { throw "The display reported $mode after requesting $DisplayMode." }
            Save-KitFrame $window $homePath $entry.Evidence 'kit-fullscreen'
            $switches = Join-Path $entry.Evidence 'fullscreen-switch.txt'
            [KitDesktop]::Chord(18, 9)
            Wait-KitCondition {
                $current = [KitDesktop]::GetForegroundWindow()
                $current -ne $window -and $current -ne [IntPtr]::Zero -and
                    [KitDesktop]::WindowClass($current) -ne 'ForegroundStaging'
            } 8 'Alt+Tab to switch away' | Out-Null
            $foreground = [KitDesktop]::GetForegroundWindow()
            "Away: shell foreground class $([KitDesktop]::WindowClass($foreground)); game minimized $([KitDesktop]::IsIconic($window))" |
                Set-Content -LiteralPath $switches -Encoding UTF8
            [KitDesktop]::Chord(18, 9)
            try {
                Wait-KitCondition { [KitDesktop]::GetForegroundWindow() -eq $window } 8 'Alt+Tab to return' | Out-Null
                'Alt+Tab returned foreground to the game.' | Add-Content -LiteralPath $switches
            } catch {
                $foreground = [KitDesktop]::GetForegroundWindow()
                "Alt+Tab selected $([KitDesktop]::WindowClass($foreground)); game minimized $([KitDesktop]::IsIconic($window)). Trying a direct window restore." |
                    Add-Content -LiteralPath $switches
                Focus-KitWindow $window
            }
            Save-KitFrame $window $homePath $entry.Evidence 'kit-fullscreen-return'
            "The restored game rendered at $([KitDesktop]::CurrentMode())." | Add-Content -LiteralPath $switches
        } else {
            Invoke-MapPictures $homePath $entry.Evidence $window $Renderer
        }
    } finally {
        if (-not $run.Process.HasExited -and $window -ne [IntPtr]::Zero) {
            try { Send-KitCommand $window '/quit' } catch { [void]$run.Process.CloseMainWindow() }
        }
        $done = Complete-KitProcess $run $entry.Evidence 12
        Save-GameEvidence $homePath $entry.Evidence
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

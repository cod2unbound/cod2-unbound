function Save-KitUpdateLogs([string]$Label, [DateTime]$Since) {
    $updateLogs = Join-Path $LauncherHome 'launcher\logs\updates'
    foreach ($log in Get-ChildItem -LiteralPath $updateLogs -File -Filter '*.log' -ErrorAction SilentlyContinue) {
        if ($log.LastWriteTime -lt $Since) { continue }
        Save-Text $log.FullName (Join-Path $Results "$Label-$($log.Name)")
        $text = Read-KitText $log.FullName
        $refusals = [regex]::Matches($text, 'Win32 error (\d+), elapsed (\d+) ms')
        $waits = [regex]::Matches($text, 'MoveFileExW (?:succeeded .*? after|retry window expired after) (\d+) ms')
        if ($refusals.Count -or $waits.Count) {
            $errors = ($refusals | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique) -join ', '
            $times = ($waits | ForEach-Object { $_.Groups[1].Value }) -join ', '
            "$($log.Name) refusals=$($refusals.Count); Windows codes=$errors; wait ms=$times" |
                Add-Content -LiteralPath (Join-Path $Results 'rename-waits.txt')
        }
    }
}

function Get-KitUpdateTrust {
    $current = @(Get-ChildItem -LiteralPath (Join-Path $Kit 'install') -File -Filter '*-setup.exe')
    if ($current.Count -ne 1) { throw 'Expected one current Setup.' }
    $signature = Get-AuthenticodeSignature -LiteralPath $current[0].FullName
    [pscustomobject]@{ Setup=$current[0]; Status=$signature.Status.ToString() }
}

function Invoke-KitLauncherUpdate([switch]$Interrupt) {
    Stop-KitIfNoGame
    $exe = Get-KitStartTarget
    $trust = Get-KitUpdateTrust
    if ($trust.Status -ne 'Valid') { throw 'Windows must trust the signed update before it can run.' }
    $current = $trust.Setup
    $complete = Join-Path $LauncherHome 'launcher\.launcher-setup-complete'
    Stop-KitIfMissing $complete 'The launcher setup did not finish.'
    Invoke-KitSetupVersion $true

    Initialize-KitDesktop
    Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
    $entry = New-RunContext 'update-feed'
    $ready = Join-Path $entry.Folder 'port'
    $ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $server = Join-Path $Kit 'Local-Update-Feed.ps1'
    $argsText = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}" -Setup "{1}" -Ready "{2}"' -f $server,$current.FullName,$ready
    $feed = Start-KitProcess $ps $argsText $entry.Folder -NoWindow
    $launcher = $null
    $launched = Get-Date
    try {
        $port = Wait-KitCondition {
            if ($feed.Process.HasExited) { throw 'The offline update feed exited.' }
            if (Test-Path -LiteralPath $ready) {
                $value = Get-Content -LiteralPath $ready -Raw
                if ($value -match '^\d+$') { $value }
            }
        } 12 'the local update feed'
        $settings = Get-KitLauncherSettings
        $settings.COD2_UNBOUND_TEST_UPDATE_ORIGIN = "http://127.0.0.1:$port/"
        $launcher = Start-KitProcess $exe '' $LauncherHome $settings -WorkingDirectory (Join-Path $env:SystemRoot 'System32')
        $window = Get-KitLauncherWindow $launcher.Process
        Wait-KitHome $window
        Invoke-KitControl $window 'Settings' -Name
        Invoke-KitControl $window 'Updates' -Name
        $action = Wait-KitCondition {
            foreach ($title in 'Install and relaunch','Check now','Try again') {
                if (Find-KitControl $window $title -Name) { return $title }
            }
        } 45 'the update check'
        if ($action -ne 'Install and relaunch') { Invoke-KitControl $window $action -Name }
        Wait-KitCondition { Find-KitControl $window 'Install and relaunch' -Name } 30 'an update offer' | Out-Null
        Invoke-KitControl $window 'Install and relaunch' -Name
        $expected = $current.Name.Split('-')[2]
        if ($Interrupt) {
            $pending = Join-Path $LauncherHome 'launcher\updates\pending.txt'
            $logRoot = Join-Path $LauncherHome 'launcher\logs\updates'
            $digest = (Get-FileHash -LiteralPath $current.FullName).Hash
            $boot = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToUniversalTime().ToString('o')
            $deadline = [DateTime]::UtcNow.AddSeconds(120)
            $log = $null
            do {
                if ((Test-Path -LiteralPath "$InstallRoot.previous-install") -and (Test-Path -LiteralPath $pending)) {
                    $data = @(Get-Content -LiteralPath $pending -ErrorAction SilentlyContinue)
                    if ($data.Count -eq 6 -and $data[0] -match '^[a-f0-9]{32}$' -and $data[1] -eq $expected -and $data[3] -ieq $digest) {
                        $attempt = (Get-ItemProperty -LiteralPath 'HKCU:\Software\CoD2Unbound\InstallTransaction' -ErrorAction SilentlyContinue).Attempt
                        $raw = Join-Path $logRoot "setup-$($data[0]).log"
                        if ($attempt -eq $data[0] -and (Test-Path -LiteralPath $raw) -and (Test-Path -LiteralPath $exe) -and
                            (Select-String -LiteralPath $raw -SimpleMatch 'Dest filename:' -Quiet -ErrorAction SilentlyContinue)) { $log = $raw; break }
                    }
                }
                Start-Sleep -Milliseconds 40
            } while ([DateTime]::UtcNow -lt $deadline)
            if (-not $log) { throw 'Setup did not reach a recoverable interruption point. The PC was not restarted.' }
            $witness = Join-Path $KitProfile 'restart-witness.txt'
            [IO.File]::WriteAllLines($witness, @($boot, 'Signed update, matching transaction and previous-install folder'))
            Copy-Item -LiteralPath $witness -Destination $Results
            Save-Text $log (Join-Path $Results 'interrupted-setup.log')
            & (Join-Path $env:SystemRoot 'System32\shutdown.exe') /r /f /t 0
            if ($LASTEXITCODE) { throw "Windows refused to restart (code $LASTEXITCODE)." }
            Start-Sleep -Seconds 20
            throw 'Windows had not finished restarting yet. Sign in and resume.'
        }
        Wait-KitCondition {
            $ver = try { Read-InstalledVersion 'HKCU' $InstallRoot } catch { '' }
            $ver -eq $expected -and -not (Test-Path -LiteralPath "$InstallRoot.previous-install")
        } 150 'the current version to replace the old one' | Out-Null
        Assert-InstalledFiles $InstallRoot (Join-Path $Kit 'portable')
        if (Test-Path -LiteralPath $KeyMarker) { Invoke-TestKey 'verify' }
    } finally {
        Save-KitUpdateLogs 'update' $launched
        if ($launcher) { Stop-KitLauncher $launcher $Results }
        foreach ($relaunch in Get-Process -Name 'CoD2Unbound.Windows' -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -ieq $exe -and $_.StartTime -ge $launched }) {
            [void]$relaunch.CloseMainWindow()
            if (-not $relaunch.WaitForExit(10000)) { $relaunch.Kill(); [void]$relaunch.WaitForExit(5000) }
        }
        if (-not $feed.Process.HasExited) { $feed.Process.Kill() }
        Complete-KitProcess $feed $entry.Evidence 8 | Out-Null
    }
}


function Invoke-KitRestore {
    $witness = Join-Path $KitProfile 'restart-witness.txt'
    Stop-KitIfMissing $witness 'No interrupted update was recorded.'
    $before = Get-Content -LiteralPath $witness -TotalCount 1
    $boot = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToUniversalTime().ToString('o')
    if ($before -eq $boot) { throw 'Windows has not restarted since the interruption.' }
    $exe = Get-KitStartTarget
    $settings = Get-KitLauncherSettings
    $started = Get-Date
    $run = Start-KitProcess $exe '' $LauncherHome $settings -WorkingDirectory (Join-Path $env:SystemRoot 'System32')
    $session = $run.Process.SessionId
    $starterId = $run.Process.Id
    try {
        Wait-KitCondition {
            $version = try { Read-InstalledVersion 'HKCU' $InstallRoot } catch { '' }
            $version -eq '0.3.9' -and -not (Test-Path -LiteralPath "$InstallRoot.previous-install")
        } 150 'the old version to be restored' | Out-Null
        Invoke-TestKey 'verify'
        $trace = Join-Path $LauncherHome 'startup-timing.log'
        Wait-KitCondition {
            if (Test-Path -LiteralPath $trace) {
                foreach ($process in Get-Process -Name 'CoD2Unbound.Windows' -ErrorAction SilentlyContinue) {
                    if ($process.Path -ieq $exe -and $process.SessionId -eq $session -and $process.Id -ne $starterId -and
                        $process.StartTime -ge $started -and
                        (Select-String -LiteralPath $trace -SimpleMatch " $($process.Id) first content frame" -Quiet)) { $process }
                }
            }
        } 45 'the restored launcher to show its first content' | Out-Null
        Save-Text $trace (Join-Path $Results 'restored-startup-timing.log')
    } finally {
        Save-KitUpdateLogs 'restore' $started
        Stop-KitLauncher $run $Results
        foreach ($relaunch in Get-Process -Name 'CoD2Unbound.Windows' -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -ieq $exe -and $_.SessionId -eq $session -and $_.StartTime -ge $started }) {
            [void]$relaunch.CloseMainWindow()
            if (-not $relaunch.WaitForExit(10000)) { $relaunch.Kill(); [void]$relaunch.WaitForExit(5000) }
        }
    }
}

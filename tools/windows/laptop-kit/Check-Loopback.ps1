function Request-KitServer([int]$Port, [string]$Command, [int]$Timeout = 600) {
    $udp = [Net.Sockets.UdpClient]::new()
    try {
        $udp.Client.ReceiveTimeout = $Timeout
        $udp.Connect([Net.IPAddress]::Loopback, $Port)
        [byte[]]$packet = @(255,255,255,255) + [Text.Encoding]::ASCII.GetBytes($Command)
        [void]$udp.Send($packet, $packet.Length)
        $peer = [Net.IPEndPoint]::new([Net.IPAddress]::Loopback, 0)
        try { $reply = $udp.Receive([ref]$peer) }
        catch {
            if ($_.Exception.GetBaseException() -is [Net.Sockets.SocketException]) { return }
            throw
        }
        if ($reply.Length -gt 4) { [Text.Encoding]::UTF8.GetString($reply, 4, $reply.Length - 4) }
    } finally { $udp.Dispose() }
}

function Set-KitEdit([IntPtr]$Window, [string]$Value, [string]$Text, [switch]$Name) {
    $node = Wait-KitCondition { Find-KitControl $Window $Value -Name:$Name } 12 "edit field $Value"
    $action = $node.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)
    $action.SetValue($Text)
}

function Find-KitHostedServer([string]$Name) {
    $savedHosts = Join-Path $LauncherHome 'hosting\hosted-servers.json'
    if (Test-Path -LiteralPath $savedHosts) {
        $configured = Get-Content -LiteralPath $savedHosts -Raw | ConvertFrom-Json
        foreach ($saved in $configured) {
            if ($saved.Name -eq $Name) { return $saved }
        }
    }
}

function Assert-KitHostedLookups([string]$Id, [DateTime]$Since) {
    $folder = Join-Path $LauncherHome "hosting\servers\$Id\logs"
    $logs = @(Get-ChildItem -LiteralPath $folder -File -Filter 'engine-*.log' |
        Where-Object { $_.CreationTime -ge $Since -and $_.Length -gt 0 })
    if (-not $logs.Count) { throw 'No engine log was captured for the launcher-hosted server.' }

    $services = 'master|authoriz|authoris|updat'
    $attempt = Select-String -LiteralPath $logs.FullName -Pattern (
        "resolv.*(?:$services)|(?:$services).*resolv|(?:auto-updater:\s*)?checking for updates"
    ) | Select-Object -First 1
    if ($attempt) { throw ('The launcher-hosted server attempted an external lookup or update: ' + $attempt.Line) }
}

function Get-KitOwnedGame([string]$Edition, [DateTime]$Since, [int]$Session, [int]$Seconds = 45) {
    $exe = Get-Binary $Edition -Root $InstallRoot
    Wait-KitCondition {
        Get-Process -Name 'cod2_win32' -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -ieq $exe -and $_.StartTime -ge $Since -and $_.SessionId -eq $Session } |
            Select-Object -First 1
    } $Seconds 'the installed game process'
}

function Stop-KitOwnedGame($Game, [IntPtr]$Window) {
    if (-not $Game -or $Game.HasExited) { return }
    try {
        if ($Window -ne [IntPtr]::Zero) { Send-KitCommand $Window '/quit' }
        else { [void]$Game.CloseMainWindow() }
    } catch { [void]$Game.CloseMainWindow() }
    if (-not $Game.WaitForExit(8000)) { $Game.Kill(); [void]$Game.WaitForExit(5000) }
}

function Open-KitAssociation([string]$Target, [string]$Class, [string]$Exe) {
    Assert-Offline
    $key = "HKCU:\Software\Classes\$Class\shell\open\command"
    Stop-KitIfMissing $key 'The kit association was not registered.'
    if ((Get-Item -LiteralPath $key).GetValue('') -ine ('"{0}" "%1"' -f $Exe)) {
        throw 'The association points to a different program.'
    }
    $environment = Get-KitLauncherSettings
    $environment.HOME = $environment.CFFIXED_USER_HOME = $LauncherHome
    $environment.COD2X_TEST_SERVICE_URL = 'http://127.0.0.1:9'
    $environment.COD2X_TEST_UPDATER_ADDRESS = '127.0.0.1:9'
    $environment.COD2X_TEST_UPDATER_URL = 'http://127.0.0.1:9/api/updater/check'
    $original = @{}
    try {
        foreach ($name in $environment.Keys) {
            $original[$name] = [Environment]::GetEnvironmentVariable($name)
            [Environment]::SetEnvironmentVariable($name, $environment[$name])
        }
        [KitDesktop]::OpenAssociation($Target, $Class)
    } finally {
        foreach ($name in $original.Keys) { [Environment]::SetEnvironmentVariable($name, $original[$name]) }
    }
}

function Invoke-KitOpenDemo([string]$Edition) {
    Stop-KitIfNoGame
    $exe = Get-KitStartTarget
    $demo = Join-Path $RunResults "match-$Edition-connect\kit-match-$Edition.dm_1"
    Stop-KitIfMissing $demo 'The loopback match did not save a demo.'
    $LauncherHome = New-KitLauncherTestHome $LauncherHome $Private
    Initialize-KitDesktop
    Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
    Set-KitLauncherProfile $Edition 'dx9'
    $console = Join-Path $LauncherHome 'main\console_mp.log'
    if (Test-Path -LiteralPath $console) { Remove-Item -LiteralPath $console }
    $launcher = Start-KitProcess $exe '' $LauncherHome (Get-KitLauncherSettings)
    $game = $null
    $window = [IntPtr]::Zero
    try {
        $app = Get-KitLauncherWindow $launcher.Process
        Wait-KitCondition { Find-KitControl $app 'LaunchButton' } 45 'the launcher to be ready' | Out-Null
        Open-KitAssociation $demo 'CoD2Unbound.Demo' $exe
        $since = Get-Date
        Invoke-KitControl $app 'Play demo' -Name
        $game = Get-KitOwnedGame $Edition $since $launcher.Process.SessionId
        $window = Get-KitGameWindow $game
        Wait-KitCondition {
            (Test-Path -LiteralPath $console) -and
                (Select-String -LiteralPath $console -SimpleMatch "demos/kit-match-$Edition.dm_1" -Quiet) -and
                (Select-String -LiteralPath $console -SimpleMatch 'CL_InitCGame:' -Quiet)
        } 65 'game demo playback' | Out-Null
        Focus-KitWindow $window
        Save-KitDesktop $window (Join-Path $Results 'kit-shell-demo-desktop.png')
    } finally {
        Stop-KitOwnedGame $game $window
        Save-GameEvidence $LauncherHome $Results
        Stop-KitLauncher $launcher $Results
    }
}

function Invoke-KitLoopback([string]$Edition, [string]$Route) {
    Stop-KitIfNoGame
    $exe = Get-KitStartTarget
    Stop-KitIfMissing $KeyMarker 'The sample key check was skipped.'
    Invoke-TestKey 'verify'
    # A previous demo or match can rewrite the active profile's engine config.
    # Start each connection route with the same setup completion and a new profile.
    $LauncherHome = New-KitLauncherTestHome $LauncherHome $Private
    Initialize-KitDesktop
    Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
    Set-KitLauncherProfile $Edition 'dx9'
    $console = Join-Path $LauncherHome 'main\console_mp.log'
    if (Test-Path -LiteralPath $console) { Remove-Item -LiteralPath $console }
    $demoName = "kit-match-$Edition"
    $recording = Join-Path $LauncherHome "main\demos\$demoName.dm_1"
    if ($Route -eq 'connect' -and (Test-Path -LiteralPath $recording)) { Remove-Item -LiteralPath $recording }
    $port = if ($Edition -eq 'original') { 29792 } else { 29791 }
    $settings = Get-KitLauncherSettings
    $launcher = $server = $master = $game = $hostedProcess = $null
    $hostName = "Kit $Edition"
    $window = [IntPtr]::Zero
    $password = 'kit' + [guid]::NewGuid().ToString('N')
    try {
        if ($Route -ne 'hosting') {
            $entry = New-RunContext 'local-server'
            $serverHome = $entry.Folder
            $argsText = (Get-EngineArgs $serverHome $GamePath -Port $port) +
                " +set dedicated 1 +set g_gametype dm +set scr_dm_timelimit 2 +set g_logSync 1 +set rcon_password $password" +
                ' +set sv_hostname Kit-Loopback +set sv_mapRotation "gametype dm map mp_toujane gametype dm map mp_harbor" +map_rotate'
            $server = Start-KitProcess (Get-Binary $Edition -Server -Root $InstallRoot) $argsText $serverHome -NoWindow
            Wait-KitCondition {
                if ($server.Process.HasExited) { throw 'The server exited before it answered.' }
                (Request-KitServer $port 'getstatus Kit') -match '\\sv_hostname\\Kit-Loopback'
            } 45 'the kit loopback server' | Out-Null
        }
        if ($Route -eq 'list') {
            $entryMaster = New-RunContext 'local-master'
            $ready = Join-Path $entryMaster.Folder 'port.txt'
            $masterPath = Join-Path $Kit 'Local-Master.ps1'
            $argsText = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}" -ServerPort {1} -Ready "{2}"' -f $masterPath,$port,$ready
            $ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
            $master = Start-KitProcess $ps $argsText $entryMaster.Folder -NoWindow
            $settings.COD2_UNBOUND_TEST_MASTER_PORT = Wait-KitCondition {
                if (Test-Path -LiteralPath $ready) { Get-Content -LiteralPath $ready -Raw }
            } 10 'the loopback server-list stand-in'
        }
        $launcher = Start-KitProcess $exe '' $LauncherHome $settings
        $app = Get-KitLauncherWindow $launcher.Process
        Wait-KitCondition { Find-KitControl $app 'LaunchButton' } 45 'the launcher to be ready' | Out-Null
        Invoke-KitControl $app 'ServersItem'
        $since = Get-Date
        switch ($Route) {
            'connect' {
                Invoke-KitControl $app ('Connect to address' + [char]0x2026) -Name
                Set-KitEdit $app 'AddressBox' "127.0.0.1:$port"
                Invoke-KitControl $app 'Connect' -Name
            }
            'link' {
                $scheme = if ($Edition -eq 'original') { 'cod2' } else { 'cod2x' }
                Open-KitAssociation ("${scheme}://127.0.0.1:$port") $scheme $exe
                Invoke-KitControl $app 'Connect' -Name
            }
            'list' {
                Invoke-KitControl $app 'All' -Name
                Invoke-KitControl $app 'RefreshButton'
                # The only fixture result is selected by the browser. Its TextBlock has no action.
                Wait-KitCondition { Find-KitControl $app 'Kit-Loopback' -Name } 25 'the loopback server in the list' | Out-Null
                Invoke-KitControl $app 'JoinButton'
            }
            'hosting' {
                Invoke-KitControl $app 'Hosting' -Name
                Invoke-KitControl $app 'NewHost'
                Set-KitEdit $app 'Server name' $hostName -Name
                Invoke-KitControl $app 'Start Server' -Name
                $hosted = Wait-KitCondition { Find-KitHostedServer $hostName } 15 'the saved Hosting entry'
                $port = $hosted.Port
                $hostnameField = [regex]::Escape("\sv_hostname\$hostName\")
                Wait-KitCondition { (Request-KitServer $port 'getstatus Kit') -match $hostnameField } 45 'the launcher-hosted server' | Out-Null
                $dedicated = Get-Binary $Edition -Server -Root $InstallRoot
                $owned = Get-CimInstance Win32_Process -Filter "ParentProcessId=$($launcher.Process.Id)" |
                    Where-Object { $_.ExecutablePath -ieq $dedicated } | Select-Object -First 1
                if ($owned) {
                    try {
                        $hostedProcess = [Diagnostics.Process]::GetProcessById($owned.ProcessId)
                        $null = $hostedProcess.Handle
                        if ($hostedProcess.Path -ine $dedicated -or
                            [Math]::Abs(($hostedProcess.StartTime - $owned.CreationDate).TotalMilliseconds) -ge 1) {
                            $hostedProcess.Dispose()
                            $hostedProcess = $null
                        }
                    } catch [ArgumentException], [InvalidOperationException] {
                        if ($hostedProcess) { $hostedProcess.Dispose() }
                        $hostedProcess = $null
                    }
                }
                Invoke-KitControl $app 'Join' -Name
                try { $game = Get-KitOwnedGame $Edition $since $launcher.Process.SessionId -Seconds 8 }
                catch {
                    if ($launcher.Process.HasExited) { throw }
                    Focus-KitWindow $app
                    Invoke-KitControl $app 'Join' -Name
                }
            }
        }
        if (-not $game) { $game = Get-KitOwnedGame $Edition $since $launcher.Process.SessionId }
        $process = Get-CimInstance Win32_Process -Filter ("ProcessId=" + $game.Id)
        if ($process.ParentProcessId -ne $launcher.Process.Id) { throw 'The test launcher did not start the game.' }
        Protect-KitText $process.CommandLine | Set-Content -LiteralPath (Join-Path $Results 'client-command.txt') -Encoding UTF8
        $window = Get-KitGameWindow $game
        # The launcher can open the game window before the map, renderer and menu are ready.
        # Keep the wire reply from a loading slot as well as the active score row.
        $script:KitLoopbackStatus = ''
        $reply = Wait-KitCondition {
            $status = Request-KitServer $port 'getstatus Kit'
            if ($status -and $status -cne $script:KitLoopbackStatus) {
                $script:KitLoopbackStatus = $status
                Protect-KitText $status | Add-Content -LiteralPath (Join-Path $Results 'server-status-trace.txt') -Encoding UTF8
            }
            if ($status -match '(?m)^-?\d+ +(\d+) +"[^"\r\n]+"\r?$' -and [int]$Matches[1] -lt 999) { $status }
        } 75 'a named, active player in the loopback match'
        Protect-KitText $reply | Set-Content -LiteralPath (Join-Path $Results 'player-status.txt') -Encoding UTF8
        if ($Route -in 'connect','hosting') {
            # The server can report an active slot before the client accepts game input.
            Wait-KitCondition {
                (Test-Path -LiteralPath $console) -and
                    (Select-String -LiteralPath $console -SimpleMatch 'dvar set cl_ingame 1' -Quiet)
            } 35 'the joined client to finish loading its first map' | Out-Null
            Enter-KitPlay $window
            Save-KitFrame $window $LauncherHome $Results 'kit-loopback-playing'
        }
        if ($Route -eq 'connect') {
            $summaryPath = Join-Path $LauncherHome 'main\stats\matches.jsonl'
            $previousSummaries = if (Test-Path -LiteralPath $summaryPath) { @(Get-Content -LiteralPath $summaryPath).Count } else { 0 }
            Send-KitCommand $window "/toggleconsole; record $demoName"
            Wait-KitCondition {
                Test-Path -LiteralPath $recording
            } 14 'the game to begin recording the demo' | Out-Null
            Start-Sleep -Seconds 5
            Send-KitCommand $window '/toggleconsole; stoprecord'
            Wait-KitCondition { Select-String -LiteralPath $console -SimpleMatch 'Stopped demo.' -Quiet } 12 'the demo writer to close' | Out-Null
            Copy-Item -LiteralPath $recording -Destination $Results
            $games = Join-Path $serverHome 'main\games_mp.log'
            Wait-KitCondition { (Test-Path -LiteralPath $games) -and (Select-String -LiteralPath $games -SimpleMatch 'ExitLevel' -Quiet) } 150 'the timed round to end' | Out-Null
            Wait-KitCondition { (Request-KitServer $port 'getstatus Kit') -match '\\mapname\\mp_harbor\\' } 45 'automatic rotation to Harbor' | Out-Null
            Wait-KitCondition {
                $status = Request-KitServer $port 'getstatus Kit'
                $status -match '\\mapname\\mp_harbor\\' -and
                    $status -match '(?m)^-?\d+ +(\d+) +"[^"\r\n]+"\r?$' -and [int]$Matches[1] -lt 999
            } 75 'the player to rejoin on Harbor' | Out-Null
            Save-KitFrame $window $LauncherHome $Results 'kit-loopback-harbor'
            $summary = Wait-KitCondition {
                if (Test-Path -LiteralPath $summaryPath) {
                    Get-Content -LiteralPath $summaryPath | Select-Object -Skip $previousSummaries | ForEach-Object {
                        $_ | ConvertFrom-Json
                    } | Where-Object { $_.map -eq 'mp_toujane' -and $_.endReason -eq 'intermission' } | Select-Object -Last 1
                }
            } 15 'the completed Toujane match summary'
            $summary | Select-Object map,mode,endReason | ConvertTo-Json |
                Set-Content -LiteralPath (Join-Path $Results 'match-summary.json') -Encoding UTF8
        }
        Stop-KitOwnedGame $game $window
        if ($Route -eq 'connect') {
            Focus-KitWindow $app
            [void][KitDesktop]::ShowWindow($app, 3)
            Wait-KitCondition {
                if ($history = Find-KitControl $app 'All matches' -Name -AllowOffscreen) { return $history }
                if (-not (Find-KitControl $app 'LaunchButton' -AllowOffscreen)) {
                    Invoke-KitControl $app 'HomeItem'
                }
            } 30 'Home to show its match history after game exit' | Out-Null
            # Quitting on Harbor can add a newer summary. Home shows the last
            # match, so open the history to find the completed Toujane match.
            $title = 'Toujane ' + [char]0x00B7 + ' Deathmatch'
            $navigation = @{ Attempts = 0; RetryAt = [DateTime]::MinValue }
            try {
                Wait-KitCondition {
                    if ($row = Find-KitControl $app $title -Name) { return $row }
                    if ($navigation.Attempts -lt 3 -and [DateTime]::UtcNow -ge $navigation.RetryAt -and
                        (Find-KitControl $app 'All matches' -Name -AllowOffscreen)) {
                        Invoke-KitControl $app 'All matches' -Name -AllowOffscreen
                        $navigation.Attempts++
                        $navigation.RetryAt = [DateTime]::UtcNow.AddSeconds(4)
                    }
                } 24 'the Toujane match in the launcher' | Out-Null
            } catch {
                if ([KitDesktop]::GetForegroundWindow() -eq $app) {
                    try { Save-KitDesktop $app (Join-Path $Results 'launcher-match-timeout.png') } catch { }
                }
                throw
            }
            Save-KitDesktop $app (Join-Path $Results 'launcher-match.png')
        }
        if ($Route -eq 'hosting') {
            $closeTrace = Join-Path $Results 'hosting-close.txt'
            Focus-KitWindow $app
            [KitDesktop]::Chord(18, 115)
            Wait-KitCondition { Find-KitControl $app 'Stop and quit' -Name } 12 'the hosted server close confirmation' | Out-Null
            Focus-KitWindow $app
            [KitDesktop]::Tap(27)
            Wait-KitCondition {
                -not (Find-KitControl $app 'Stop and quit' -Name) -and
                    (Request-KitServer $port 'getstatus Kit') -match $hostnameField
            } 12 'the confirmation to close and the server to stay up after Cancel' | Out-Null
            'Cancel dismissed the confirmation and the server still answered.' | Set-Content -LiteralPath $closeTrace
            Start-Sleep -Milliseconds 700
            Focus-KitWindow $app
            [KitDesktop]::Chord(18, 115)
            $confirm = Wait-KitCondition { Find-KitControl $app 'Stop and quit' -Name } 12 'the reopened quit confirmation'
            $confirm.SetFocus()
            [KitDesktop]::Tap(13)
            'Confirmed Stop and quit.' | Add-Content -LiteralPath $closeTrace
            Wait-KitCondition {
                $launcher.Process.HasExited -and -not (Request-KitServer $port 'getstatus Kit')
            } 20 'the launcher and its hosted server to stop' | Out-Null
            'The launcher exited and the hosted server stopped answering.' | Add-Content -LiteralPath $closeTrace
            Assert-KitHostedLookups $hosted.Id $since
        }
    } catch {
        Protect-KitText $_.Exception.Message | Set-Content -LiteralPath (Join-Path $Results 'operation-error.txt') -Encoding UTF8
        throw
    } finally {
        Stop-KitOwnedGame $game $window
        Save-GameEvidence $LauncherHome $Results
        if ($launcher) { Stop-KitLauncher $launcher $Results }
        if ($Route -eq 'hosting') {
            if ($hostedProcess) {
                try {
                    if (-not $hostedProcess.HasExited) {
                        $hostedProcess.Kill()
                        if (-not $hostedProcess.WaitForExit(8000)) { throw 'The owned test server did not exit.' }
                        'The kit stopped the remaining dedicated process it had verified as a child of the test launcher.' |
                            Set-Content -LiteralPath (Join-Path $Results 'hosting-cleanup.txt')
                    }
                } finally { $hostedProcess.Dispose() }
            }
            $logs = Join-Path $LauncherHome 'hosting\servers'
            foreach ($file in Get-ChildItem -LiteralPath $logs -Recurse -File -Filter '*.log' -ErrorAction SilentlyContinue) {
                if ($file.LastWriteTime -ge $since) { Save-Text $file.FullName (Join-Path $Results $file.Name) }
            }
        }
        if ($master) {
            if (-not $master.Process.HasExited) { $master.Process.Kill() }
            Complete-KitProcess $master $entryMaster.Evidence 5 | Out-Null
        }
        if ($server) {
            if (-not $server.Process.HasExited) { Stop-SetupServer $password $port }
            Complete-KitProcess $server $entry.Evidence 8 | Out-Null
            Save-GameEvidence $serverHome $entry.Evidence
        }
    }
}

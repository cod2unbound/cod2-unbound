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
    foreach ($log in $logs) { Save-KitEngineAudit (Read-KitText $log.FullName) $Results }

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
        Wait-KitHome $app
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
        Save-KitWindow $window (Join-Path $Results 'kit-shell-demo-window.png')
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
    $launcher = $server = $second = $master = $game = $hostedProcess = $null
    $hostName = "Kit $Edition"
    $window = [IntPtr]::Zero
    $password = 'kit' + [guid]::NewGuid().ToString('N')
    $secondPort = $port + 20
    $joinPassword = 'kit' + [guid]::NewGuid().ToString('N')
    if ($Route -eq 'handoff') {
        # Keep the saved test password in Fixture's in-process store.
        # The engine continues to read the sample key through Windows.
        $settings.COD2_UNBOUND_TEST_WINDOWS_CREDENTIALS = '0'
    }
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
        if ($Route -eq 'handoff') {
            $secondEntry = New-RunContext 'password-server'
            $secondArgs = (Get-EngineArgs $secondEntry.Folder $GamePath -Port $secondPort) +
                " +set dedicated 1 +set g_gametype dm +set sv_hostname Kit-Password +set g_password $joinPassword" +
                " +set rcon_password $password +map mp_toujane"
            $second = Start-KitProcess (Get-Binary $Edition -Server -Root $InstallRoot) $secondArgs $secondEntry.Folder -NoWindow
            Wait-KitCondition {
                if ($second.Process.HasExited) { throw 'The password server exited before answering.' }
                (Request-KitServer $secondPort 'getstatus Kit') -match 'Kit-Password'
            } 45 'the password-protected loopback server' | Out-Null
        }
        if ($Route -in 'list','handoff') {
            $entryMaster = New-RunContext 'local-master'
            $ready = Join-Path $entryMaster.Folder 'port.txt'
            $masterPath = Join-Path $Kit 'Local-Master.ps1'
            $argsText = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}" -ServerPort {1} -Ready "{2}"' -f $masterPath,$port,$ready
            if ($Route -eq 'handoff') { $argsText += " -SecondPort $secondPort" }
            $ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
            $master = Start-KitProcess $ps $argsText $entryMaster.Folder -NoWindow
            $settings.COD2_UNBOUND_TEST_MASTER_PORT = Wait-KitCondition {
                if (Test-Path -LiteralPath $ready) { Get-Content -LiteralPath $ready -Raw }
            } 10 'the loopback server-list stand-in'
        }
        $launcher = Start-KitProcess $exe '' $LauncherHome $settings
        $app = Get-KitLauncherWindow $launcher.Process
        Wait-KitHome $app
        Invoke-KitControl $app 'ServersItem'
        $since = Get-Date
        switch ($Route) {
            'connect' {
                Invoke-KitControl $app ('Connect to address' + [char]0x2026) -Name
                Set-KitEdit $app 'AddressBox' "127.0.0.1:$port"
                Invoke-KitControl $app 'PrimaryButton'
            }
            'link' {
                $scheme = if ($Edition -eq 'original') { 'cod2' } else { 'cod2x' }
                Open-KitAssociation ("${scheme}://127.0.0.1:$port") $scheme $exe
                Invoke-KitControl $app 'PrimaryButton'
            }
            { $_ -in 'list','handoff' } {
                Invoke-KitControl $app 'All' -Name
                Invoke-KitControl $app 'RefreshButton'
                # The only fixture result is selected by the browser. Its TextBlock has no action.
                Wait-KitCondition { Find-KitControl $app 'Kit-Loopback' -Name } 25 'the loopback server in the list' | Out-Null
                if ($Route -eq 'handoff') {
                    Select-KitServerRow $app 'Kit-Loopback'
                    Wait-KitCondition {
                        (Find-KitControl $app 'Check again' -Name -AllowOffscreen) -or
                        (Find-KitControl $app 'DetailReady' -AllowOffscreen)
                    } 15 'the server ready check' | Out-Null
                    Focus-KitWindow $app
                    Save-KitWindow $app (Join-Path $Results 'servers-ready.png')
                }
                Invoke-KitControl $app 'JoinButton'
            }
            'hosting' {
                Invoke-KitControl $app 'Hosting' -Name
                Invoke-KitControl $app 'New server' -Name
                Set-KitEdit $app 'Server name' $hostName -Name
                Invoke-KitControl $app 'Start server' -Name
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
        if ($Route -in 'connect','hosting','handoff') {
            # The server can report an active slot before the client accepts game input.
            Wait-KitCondition {
                (Test-Path -LiteralPath $console) -and
                    (Select-String -LiteralPath $console -SimpleMatch 'dvar set cl_ingame 1' -Quiet)
            } 35 'the joined client to finish loading its first map' | Out-Null
            Enter-KitPlay $window
            Save-KitFrame $window $LauncherHome $Results 'kit-loopback-playing'
        }
        if ($Route -eq 'handoff') {
            Invoke-KitSecondServer $app $game $port $secondPort $joinPassword
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
                    try { Save-KitWindow $app (Join-Path $Results 'launcher-match-timeout.png') } catch { }
                }
                throw
            }
            Save-KitWindow $app (Join-Path $Results 'launcher-match.png')
        }
        if ($Route -eq 'hosting') {
            $closeTrace = Join-Path $Results 'hosting-close.txt'
            $closeAction = 'Stop server and close'
            Focus-KitWindow $app
            [KitDesktop]::Chord(18, 115)
            Wait-KitCondition { Find-KitControl $app $closeAction -Name } 12 'the hosted server close confirmation' | Out-Null
            Focus-KitWindow $app
            [KitDesktop]::Tap(27)
            Wait-KitCondition {
                -not (Find-KitControl $app $closeAction -Name) -and
                    (Request-KitServer $port 'getstatus Kit') -match $hostnameField
            } 12 'the confirmation to close and the server to stay up after Cancel' | Out-Null
            'Cancel dismissed the confirmation and the server still answered.' | Set-Content -LiteralPath $closeTrace
            Start-Sleep -Milliseconds 700
            Focus-KitWindow $app
            [KitDesktop]::Chord(18, 115)
            $confirm = Wait-KitCondition { Find-KitControl $app $closeAction -Name } 12 'the reopened quit confirmation'
            $confirm.SetFocus()
            [KitDesktop]::Tap(13)
            "Confirmed $closeAction." | Add-Content -LiteralPath $closeTrace
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
                if ($since -and $file.LastWriteTime -ge $since) {
                    if ($file.Name -like 'engine-*.log') { Save-KitEngineAudit (Read-KitText $file.FullName) $Results }
                    Save-Text $file.FullName (Join-Path $Results $file.Name)
                }
            }
        }
        if ($master) {
            if (-not $master.Process.HasExited) { $master.Process.Kill() }
            Complete-KitProcess $master $entryMaster.Evidence 5 | Out-Null
        }
        if ($second) {
            if (-not $second.Process.HasExited) { Stop-SetupServer $password $secondPort }
            Complete-KitProcess $second $secondEntry.Evidence 8 | Out-Null
            Save-GameEvidence $secondEntry.Folder $secondEntry.Evidence
        }
        if ($server) {
            if (-not $server.Process.HasExited) { Stop-SetupServer $password $port }
            Complete-KitProcess $server $entry.Evidence 8 | Out-Null
            Save-GameEvidence $serverHome $entry.Evidence
        }
    }
}

function Get-KitNamedPlayer([int]$Port) {
    $status = Request-KitServer $Port 'getstatus Kit'
    if ($status -match '(?m)^-?\d+ +(\d+) +"Kit Player"\r?$' -and [int]$Matches[1] -lt 999) { $status }
}

function Select-KitServerRow([IntPtr]$Window, [string]$ServerName) {
    $selection = Wait-KitCondition {
        $node = Find-KitControl $Window $ServerName -Name
        while ($node) {
            $action = $null
            if ($node.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$action)) {
                return $action
            }
            $node = [Windows.Automation.TreeWalker]::RawViewWalker.GetParent($node)
        }
    } 30 "the server row for $ServerName"
    $selection.Select()
}

function Complete-KitJoinPassword([IntPtr]$Window, [string]$Name, [string]$Password) {
    $field = Wait-KitCondition {
        Find-KitControl $Window "Password for $Name" -Name
    } 18 'the password prompt for the second server'
    Focus-KitWindow $Window
    $field.SetFocus()
    [Windows.Forms.SendKeys]::SendWait($Password)
    $remember = Find-KitControl $Window 'Remember for this server, on this PC only' -Name
    if (-not $remember) { throw 'The remember-password choice was missing.' }
    $remember.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
    Save-KitWindow $Window (Join-Path $Results 'server-password.png')
    Invoke-KitControl $Window 'PrimaryButton'
}

function Invoke-KitSecondServer([IntPtr]$App, $Game, [int]$FirstPort, [int]$SecondPort, [string]$JoinPassword) {
    $evidence = Join-Path $Results 'handoff.txt'
    $tag = 0x434F4432
    foreach ($probe in @(
        @{ Text="connect 127.0.0.1:$SecondPort"; Tag=0 }
        @{ Text='connect 127.0.0.1:65536'; Tag=$tag }
        @{ Text=("connect 127.0.0.1:$SecondPort " + ('x' * 2048)); Tag=$tag }
    )) {
        if ([KitDesktop]::SendJoinProbe($Game.Id, $probe.Text, $probe.Tag) -or -not (Get-KitNamedPlayer $FirstPort)) {
            throw 'The game accepted an invalid request or left the first server.'
        }
    }
    'Wrong message tag, invalid port and oversized payload refused. Player stayed on the first server.' |
        Set-Content -LiteralPath $evidence -Encoding UTF8
    Focus-KitWindow $App
    Select-KitServerRow $App 'Kit-Password'
    Invoke-KitControl $App 'JoinButton'
    Complete-KitJoinPassword $App 'Kit-Password' $JoinPassword
    $status = Wait-KitCondition { Get-KitNamedPlayer $SecondPort } 60 'Kit Player to join the password-protected server'
    Wait-KitCondition { -not (Get-KitNamedPlayer $FirstPort) } 15 'Kit Player to leave the first server' | Out-Null
    if ($Game.HasExited) { throw 'The game process that joined the first server exited during the handoff.' }
    "Same game PID $($Game.Id). First server no longer lists Kit Player. Second server status:" |
        Add-Content -LiteralPath $evidence
    Protect-KitText $status | Add-Content -LiteralPath $evidence

    Focus-KitWindow $App
    Invoke-KitControl $App 'JoinButton'
    Wait-KitCondition { Find-KitControl $App 'Saved on this PC. Untick to forget it when you launch.' -Name } 15 'the remembered password' | Out-Null
    Save-KitWindow $App (Join-Path $Results 'server-password-saved.png')
    Invoke-KitControl $App 'Cancel' -Name
    Wait-KitCondition { -not (Find-KitControl $App 'Password for Kit-Password' -Name) } 15 'the password dialog to close' | Out-Null
    $ready = $false
    foreach ($attempt in 1..3) {
        Focus-KitWindow $App
        [KitDesktop]::Chord(17, 49)
        try {
            Wait-KitCondition {
                (Find-KitControl $App 'Return to game' -Name -AllowOffscreen) -and
                    (Find-KitControl $App 'Close game' -Name -AllowOffscreen)
            } 5 'the Home controls for the running game' | Out-Null
            $ready = $true
            break
        } catch {
            if ($Game.HasExited) { throw 'The game stopped before its Home controls appeared.' }
        }
    }
    if (-not $ready) {
        Save-KitWindow $App (Join-Path $Results 'launcher-playing-timeout.png')
        throw 'The running game controls did not appear after the handoff.'
    }
    Save-KitWindow $App (Join-Path $Results 'launcher-playing.png')
    [KitDesktop]::Chord(18, 115)
    foreach ($name in 'Close launcher only','Close both','Cancel') {
        Wait-KitCondition { Find-KitControl $App $name -Name } 15 $name | Out-Null
    }
    Save-KitWindow $App (Join-Path $Results 'launcher-close-question.png')
    Invoke-KitControl $App 'Cancel' -Name
    Invoke-KitControl $App 'Return to game' -Name
    $gameWindow = Get-KitGameWindow $Game
    Wait-KitCondition { [KitDesktop]::GetForegroundWindow() -eq $gameWindow } 8 'Return to game to bring the game forward' | Out-Null
    Focus-KitWindow $App
    Invoke-KitControl $App 'Close game' -Name
    if (-not $Game.WaitForExit(18000)) { throw 'Close game did not stop the test game.' }
    'Return to game brought the same game to the foreground. Close game stopped it.' | Add-Content -LiteralPath $evidence
}

function Invoke-KitFullscreenMatch([string]$Edition) {
    Initialize-KitDesktop
    $serverEntry = New-RunContext 'fullscreen-server'
    $clientEntry = New-RunContext 'fullscreen-client'
    $desktop = [KitDesktop]::CurrentMode()
    $port = if ($Edition -eq 'original') { 29803 } else { 29804 }
    $admin = 'kit' + [guid]::NewGuid().ToString('N')
    $serverArgs = (Get-EngineArgs $serverEntry.Folder $GamePath -Port $port) +
        " +set dedicated 1 +set g_gametype dm +set rcon_password $admin +map mp_toujane"
    New-TestProfile $clientEntry.Folder
    'bind w "+forward"' | Add-Content -LiteralPath (Join-Path $clientEntry.Folder 'main\players\kit\config_mp.cfg') -Encoding ASCII
    $clientArgs = (Get-EngineArgs $clientEntry.Folder $GamePath -Port ($port+100)) +
        " +set r_rendererPreference dx9 +set r_aaSamples 1 +set r_fullscreen 1 +set r_borderless 0 +set r_mode $desktop" +
        " +set com_introPlayed 1 +set cl_allowDownload 0 +connect 127.0.0.1:$port"
    $run = $null
    $window = [IntPtr]::Zero
    $server = Start-KitProcess (Get-Binary $Edition -Server) $serverArgs $serverEntry.Folder -NoWindow
    try {
        Wait-KitCondition {
            (Request-KitServer $port 'getstatus Kit') -match '\\mapname\\mp_toujane\\'
        } 45 'the exclusive match server' | Out-Null
        $run = Start-KitProcess (Get-Binary $Edition) $clientArgs $clientEntry.Folder
        $window = Get-KitGameWindow $run.Process
        Wait-KitCondition { Get-KitNamedPlayer $port } 65 'Kit Player to join the exclusive match' | Out-Null
        $log = Join-Path $clientEntry.Folder 'main\console_mp.log'
        Wait-KitCondition {
            (Test-Path $log) -and (Select-String -LiteralPath $log -SimpleMatch 'dvar set cl_ingame 1' -Quiet)
        } 35 'the joined client to finish loading' | Out-Null
        Enter-KitPlay $window
        Save-KitFrame $window $clientEntry.Folder $clientEntry.Evidence 'kit-match'
        Invoke-KitTaskSwitch $window $clientEntry.Folder $clientEntry.Evidence 'kit-match'
        $viewPattern = '(?m)^\( *-?\d+ +-?\d+ +-?\d+\) *: *-?\d+'
        Send-KitCommand $window '/toggleconsole; viewpos'
        $before = [regex]::Matches((Read-KitText $log), $viewPattern) | Select-Object -Last 1
        if (-not $before) { throw 'The game did not report its position after returning.' }
        [KitDesktop]::KeyEvent(87, $false)
        try { Start-Sleep -Milliseconds 850 } finally { [KitDesktop]::KeyEvent(87, $true) }
        Send-KitCommand $window '/toggleconsole; viewpos'
        $after = [regex]::Matches((Read-KitText $log), $viewPattern) | Select-Object -Last 1
        @($before.Value, $after.Value) | Set-Content -LiteralPath (Join-Path $clientEntry.Evidence 'position.txt')
        if (-not $after -or $before.Value -eq $after.Value -or -not (Get-KitNamedPlayer $port)) {
            throw 'The named player did not move after returning to the match.'
        }
        $recovery = @(Select-String -LiteralPath $log -Pattern 'lost device|device lost|recover.*device' | ForEach-Object Line)
        if (-not $recovery) { $recovery = 'The restored game rendered without a lost-device line in the log.' }
        $recovery | Set-Content -LiteralPath (Join-Path $clientEntry.Evidence 'device-recovery.txt')
    } finally {
        if ($run) {
            Stop-KitOwnedGame $run.Process $window
            Complete-KitProcess $run $clientEntry.Evidence 8 | Out-Null
            Save-GameEvidence $clientEntry.Folder $clientEntry.Evidence
        }
        if (-not $server.Process.HasExited) { Stop-SetupServer $admin $port }
        Complete-KitProcess $server $serverEntry.Evidence 8 | Out-Null
        Save-GameEvidence $serverEntry.Folder $serverEntry.Evidence
    }
    if ([KitDesktop]::CurrentMode() -ne $desktop) { throw 'Windows did not return to the desktop resolution.' }
}

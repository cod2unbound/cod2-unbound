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

function Set-KitEdit($Window, [string]$Value, [string]$Text, [switch]$Name) {
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

function Find-KitLoopbackDialog([IntPtr]$App, [string]$Title) {
    Get-KitControls $App $Title -Name |
        Where-Object { Find-KitControl $_ 'PrimaryButton' } | Select-Object -First 1
}

function Invoke-KitOpenDemo([string]$Edition) {
    Stop-KitIfNoGame
    $exe = Get-KitStartTarget
    Stop-KitIfMissing $KeyMarker 'The sample key check was skipped.'
    Invoke-TestKey 'verify'
    $recording = Join-Path $RunResults "match-$Edition-connect\kit-match-$Edition.dm_1"
    Stop-KitIfMissing $recording 'The loopback match did not save a demo.'
    $demo = Join-Path $Private "kit match $Edition.dm_1"
    Copy-Item -LiteralPath $recording -Destination $demo
    $playableName = "kit_match_$Edition"
    $LauncherHome = New-KitLauncherTestHome $LauncherHome $Private
    Initialize-KitDesktop
    Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
    Set-KitLauncherProfile $Edition 'dx9'
    $console = Join-Path $LauncherHome 'main\console_mp.log'
    if (Test-Path -LiteralPath $console) { Remove-Item -LiteralPath $console }
    $trace = New-KitFrameTrace $Results
    $settings = Get-KitLauncherSettings
    $settings.COD2_FRAME_TRACE_FILE = $trace.Path
    $launcher = Start-KitProcess $exe '' $LauncherHome $settings
    $game = $null
    $window = [IntPtr]::Zero
    try {
        $app = Get-KitLauncherWindow $launcher.Process
        Wait-KitHome $app
        $since = Get-Date
        Open-KitAssociation $demo $FixtureDemo $exe $settings
        Invoke-KitControl $app 'Play Recording' -Name
        $game = Get-KitOwnedGame $Edition $since $launcher.Process.SessionId
        $command = (Get-CimInstance Win32_Process -Filter "ProcessId=$($game.Id)").CommandLine
        if ($command -notmatch ('(?:^|\s)"?\+demo"?\s+"?' + [regex]::Escape($playableName) + '"?(?=\s|$)')) {
            throw 'The game did not start with the expected playable demo name.'
        }
        $window = Get-KitGameWindow $game
        Wait-KitCondition {
            (Test-Path -LiteralPath $console) -and
                (Select-String -LiteralPath $console -SimpleMatch "demos/$playableName.dm_1" -Quiet) -and
                (Select-String -LiteralPath $console -SimpleMatch 'CL_InitCGame:' -Quiet)
        } 65 'game demo playback' | Out-Null
        "+demo $playableName started and playback loaded." |
            Set-Content -LiteralPath (Join-Path $Results 'demo-playback.txt')
        Start-KitFrameSample $trace $window $console
        Focus-KitWindow $window
        Save-KitWindow $window (Join-Path $Results 'kit-shell-demo-window.png')
        Start-Sleep -Seconds 2
    } finally {
        $trace.End = [Diagnostics.Stopwatch]::GetTimestamp()
        Stop-KitOwnedGame $game $window
        Save-GameEvidence $LauncherHome $Results
        Save-KitFrameTrace $trace $Results $console
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
    $trace = New-KitFrameTrace $Results
    $settings.COD2_FRAME_TRACE_FILE = $trace.Path
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
            # Keep Toujane running until the client joins after the cooling pause.
            $roundMinutes = if ($env:COD2_KIT_COOL_DOWN -eq '1') { 3 } else { 2 }
            $argsText = (Get-EngineArgs $serverHome $GamePath -Port $port) +
                " +set dedicated 1 +set g_gametype dm +set scr_dm_timelimit $roundMinutes +set g_logSync 1 +set rcon_password $password" +
                ' +set sv_hostname Kit-Loopback +set sv_mapRotation "gametype dm map mp_toujane gametype dm map mp_harbor" +map_rotate'
            $server = Start-KitProcess (Get-Binary $Edition -Server -Root $InstallRoot) $argsText $serverHome -NoWindow
            Wait-KitCondition {
                if ($server.Process.HasExited) { throw 'The server exited before it answered.' }
                (Request-KitServer $port 'getstatus Kit') -match '\\sv_hostname\\Kit-Loopback'
            } 45 'the kit loopback server' | Out-Null
        }
        if ($Route -eq 'handoff') {
            Wait-KitCooling
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
        if ($Route -ne 'hosting') { Wait-KitCooling }
        $launcher = Start-KitProcess $exe '' $LauncherHome $settings
        $app = Get-KitLauncherWindow $launcher.Process
        Wait-KitHome $app
        Invoke-KitControl $app 'ServersItem'
        $gameSince = Get-Date
        $hostedSince = $null
        switch ($Route) {
            'connect' {
                Open-KitConnect $app
                Set-KitEdit $app 'AddressBox' "127.0.0.1:$port"
                Invoke-KitControl $app 'PrimaryButton'
            }
            'link' {
                $scheme = if ($Edition -eq 'original') { 'cod2-test' } else { 'cod2x-test' }
                Open-KitAssociation ("${scheme}://127.0.0.1:$port") $scheme $exe
                Invoke-KitControl $app 'PrimaryButton'
            }
            { $_ -in 'list','handoff' } {
                Invoke-KitControl $app 'All' -Name
                Start-KitPublicRefresh $app
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
                $hostedSince = Get-Date
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
                Wait-KitCooling
                $gameSince = Get-Date
                Invoke-KitControl $app 'Join' -Name
                try { $game = Get-KitOwnedGame $Edition $gameSince $launcher.Process.SessionId -Seconds 8 }
                catch {
                    if ($launcher.Process.HasExited) { throw }
                    Focus-KitWindow $app
                    Invoke-KitControl $app 'Join' -Name
                }
            }
        }
        if (-not $game) { $game = Get-KitOwnedGame $Edition $gameSince $launcher.Process.SessionId }
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
        Wait-KitCondition {
            (Test-Path -LiteralPath $console) -and
                (Select-String -LiteralPath $console -SimpleMatch 'dvar set cl_ingame 1' -Quiet)
        } 35 'the joined client to finish loading its first map' | Out-Null
        if ($Route -in 'connect','hosting','handoff') {
            Enter-KitPlay $window
        }
        Start-KitFrameSample $trace $window $console
        if ($Route -in 'connect','hosting','handoff') {
            Save-KitFrame $window $LauncherHome $Results 'kit-loopback-playing'
        }
        if ($Route -ne 'connect') {
            Start-Sleep -Seconds 2
            $trace.End = [Diagnostics.Stopwatch]::GetTimestamp()
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
            $trace.End = [Diagnostics.Stopwatch]::GetTimestamp()
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
                if ($history = Find-KitControl $app 'AllMatches' -AllowOffscreen) { return $history }
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
                        (Find-KitControl $app 'AllMatches' -AllowOffscreen)) {
                        Invoke-KitControl $app 'AllMatches' -AllowOffscreen
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
            Assert-KitHostedLookups $hosted.Id $hostedSince
        }
    } catch {
        Protect-KitText $_.Exception.Message | Set-Content -LiteralPath (Join-Path $Results 'operation-error.txt') -Encoding UTF8
        throw
    } finally {
        Stop-KitOwnedGame $game $window
        Save-GameEvidence $LauncherHome $Results
        Save-KitFrameTrace $trace $Results $console
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
                if ($hostedSince -and $file.LastWriteTime -ge $hostedSince) {
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

function Close-KitRunningGame([IntPtr]$App, $Game) {
    for ($attempt = 1; $attempt -le 3 -and -not $Game.HasExited; $attempt++) {
        Focus-KitWindow $App
        Invoke-KitControl $App 'CloseGameButton' -AllowOffscreen
        try {
            Wait-KitCondition {
                $Game.HasExited -or -not (Find-KitControl $App 'CloseGameButton' -AllowOffscreen)
            } 4 'the Close game action to begin' | Out-Null
            break
        } catch {
            if ($attempt -eq 3) { throw }
        }
    }
    # The launcher allows up to twenty seconds for its graceful-close and
    # recovery path. Keep pumping our own foreground and security checks.
    Wait-KitCondition {
        if ($Game.HasExited) { return $true }
        if (Find-KitControl $App "The game didn't close. Switch to it and close it from its own menu." -Name -AllowOffscreen) {
            throw 'The launcher reported that the game did not close.'
        }
    } 25 'the launcher to stop the game' | Out-Null
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
    $choice = $remember.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
    if ($choice.Current.ToggleState -ne [Windows.Automation.ToggleState]::On) { $choice.Toggle() }
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
                (Find-KitControl $App 'ReturnGameButton' -AllowOffscreen) -and
                    (Find-KitControl $App 'CloseGameButton' -AllowOffscreen)
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
    $title = 'Call of Duty 2 is still running'
    $dialog = Wait-KitCondition { Find-KitLoopbackDialog $App $title } 15 'the running-game close question'
    foreach ($id in 'SecondaryButton','CloseButton') {
        Wait-KitCondition { Find-KitControl $dialog $id } 12 $id | Out-Null
    }
    Save-KitWindow $App (Join-Path $Results 'launcher-close-question.png')
    $gameWindow = Get-KitGameWindow $Game
    Invoke-KitControl $dialog 'CloseButton'
    Wait-KitCondition { -not (Find-KitLoopbackDialog $App $title) } 12 'the running-game close question to dismiss' | Out-Null
    Invoke-KitControl $App 'ReturnGameButton'
    Wait-KitCondition { [KitDesktop]::GetForegroundWindow() -eq $gameWindow } 8 'Return to game to bring the game forward' | Out-Null
    Close-KitRunningGame $App $Game
    'Return to game brought the same game to the foreground. Close game stopped it.' | Add-Content -LiteralPath $evidence
}

function Invoke-KitFullscreenMatch([string]$Edition, [string]$Renderer = 'dx9') {
    Initialize-KitDesktop
    $serverEntry = New-RunContext 'fullscreen-server'
    $clientEntry = New-RunContext 'fullscreen-client'
    Assert-KitExclusiveForeground ([IntPtr]::Zero) $clientEntry.Evidence -ModalOnly
    $desktop = [KitDesktop]::CurrentMode()
    $port = if ($Edition -eq 'original') { 29803 } else { 29804 }
    $admin = 'kit' + [guid]::NewGuid().ToString('N')
    $serverArgs = (Get-EngineArgs $serverEntry.Folder $GamePath -Port $port) +
        " +set dedicated 1 +set g_gametype dm +set rcon_password $admin +map mp_toujane"
    New-TestProfile $clientEntry.Folder
    'bind w "+forward"' | Add-Content -LiteralPath (Join-Path $clientEntry.Folder 'main\players\kit\config_mp.cfg') -Encoding ASCII
    $clientArgs = (Get-EngineArgs $clientEntry.Folder $GamePath -Port ($port+100)) +
        " +set r_rendererPreference $Renderer +set r_aaSamples 1 +set r_fullscreen 1 +set r_borderless 0 +set r_mode $desktop" +
        " +set com_introPlayed 1 +set cl_allowDownload 0 +connect 127.0.0.1:$port"
    $run = $switchWindow = $null
    $window = [IntPtr]::Zero
    $log = Join-Path $clientEntry.Folder 'main\console_mp.log'
    $trace = New-KitFrameTrace $clientEntry.Evidence
    $server = Start-KitProcess (Get-Binary $Edition -Server) $serverArgs $serverEntry.Folder `
        @{ COD2_UNBOUND_HIDE_SERVER_CONSOLE='1' } -NoWindow
    try {
        Wait-KitCondition {
            (Request-KitServer $port 'getstatus Kit') -match '\\mapname\\mp_toujane\\'
        } 45 'the exclusive match server' | Out-Null
        Wait-KitCooling
        $switchWindow = Start-KitTaskSwitchWindow $clientEntry.Folder
        Wait-KitCondition {
            $switchWindow.Process.Refresh()
            if ($switchWindow.Process.HasExited) { throw 'The task-switch window exited during startup.' }
            $switchWindow.Process.MainWindowHandle -ne [IntPtr]::Zero
        } 10 'the task-switch window' | Out-Null
        $run = Start-KitProcess (Get-Binary $Edition) $clientArgs $clientEntry.Folder @{ COD2_FRAME_TRACE_FILE=$trace.Path }
        $window = Get-KitGameWindow $run.Process $clientEntry.Evidence
        Wait-KitMapLoad {
            Assert-KitExclusiveForeground $window $clientEntry.Evidence -ModalOnly
            (Get-KitNamedPlayer $port) -and
            (Test-Path $log) -and (Select-String -LiteralPath $log -SimpleMatch 'dvar set cl_ingame 1' -Quiet)
        } 65 'exclusive-match' $run.Process $log $clientEntry.Evidence -Exclusive
        if (-not (Select-String -LiteralPath $log -SimpleMatch "Using Direct3D $($Renderer.Substring(2)) code path" -Quiet)) {
            throw "The exclusive match did not open in $(Get-GraphicsMode $Renderer) mode."
        }
        Assert-KitExclusiveForeground $window $clientEntry.Evidence
        Enter-KitPlay $window
        $inputReceipt = Join-Path $clientEntry.Evidence 'fullscreen-input.txt'
        "Play cursor clip before switch $([KitDesktop]::Clip())" | Set-Content -LiteralPath $inputReceipt
        Start-KitFrameSample $trace $window $log
        Save-KitFrame $window $clientEntry.Folder $clientEntry.Evidence 'kit-match'
        Invoke-KitTaskSwitch $window $clientEntry.Folder $clientEntry.Evidence 'kit-match' $switchWindow.Process.Id
        "Play cursor clip after switch $([KitDesktop]::Clip())" | Add-Content -LiteralPath $inputReceipt
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
        try {
            if ($run) {
                $trace.End = [Diagnostics.Stopwatch]::GetTimestamp()
                if (-not $run.Process.HasExited) { [KitDesktop]::CloseOwnedWindow($window) }
                $clientDone = Complete-KitProcess $run $clientEntry.Evidence 8
                Save-GameEvidence $clientEntry.Folder $clientEntry.Evidence
                Save-KitFrameTrace $trace $clientEntry.Evidence $log
            }
        } finally {
            try {
                if (-not $server.Process.HasExited) { Stop-SetupServer $admin $port }
                Complete-KitProcess $server $serverEntry.Evidence 8 | Out-Null
                Save-GameEvidence $serverEntry.Folder $serverEntry.Evidence
            } finally {
                if ($switchWindow) {
                    if (-not $switchWindow.Process.HasExited) { [void]$switchWindow.Process.CloseMainWindow() }
                    Complete-KitProcess $switchWindow (New-Folder (Join-Path $clientEntry.Evidence 'switch-window')) 5 | Out-Null
                }
            }
        }
    }
    if ($clientDone.TimedOut -or $clientDone.ExitCode -ne 0) { throw 'The game did not close cleanly after the switch.' }
    if ([KitDesktop]::CurrentMode() -ne $desktop) { throw 'Windows did not return to the desktop resolution.' }
}

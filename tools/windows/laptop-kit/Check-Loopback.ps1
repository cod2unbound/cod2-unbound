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

function Get-KitOwnedGame([string]$Edition, [DateTime]$Since, [int]$Session) {
    $exe = Get-Binary $Edition -Root $InstallRoot
    Wait-KitCondition {
        Get-Process -Name 'cod2_win32' -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -ieq $exe -and $_.StartTime -ge $Since -and $_.SessionId -eq $Session } |
            Select-Object -First 1
    } 45 'the installed game process'
}

function Stop-KitOwnedGame($Game, [IntPtr]$Window) {
    if (-not $Game -or $Game.HasExited) { return }
    try {
        if ($Window -ne [IntPtr]::Zero) { Send-KitCommand $Window '/quit' }
        else { [void]$Game.CloseMainWindow() }
    } catch { [void]$Game.CloseMainWindow() }
    if (-not $Game.WaitForExit(8000)) { $Game.Kill(); [void]$Game.WaitForExit(5000) }
}

function Invoke-KitLoopback([string]$Edition, [string]$Route) {
    Stop-KitIfNoGame
    $exe = Get-KitStartTarget
    $complete = Join-Path $LauncherHome 'launcher\.launcher-setup-complete'
    Stop-KitIfMissing $complete 'The launcher setup did not finish.'
    Stop-KitIfMissing $KeyMarker 'The sample key check was skipped.'
    Invoke-TestKey 'verify'
    Initialize-KitDesktop
    Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
    Set-KitLauncherProfile $Edition 'dx9'
    $console = Join-Path $LauncherHome 'main\console_mp.log'
    if (Test-Path -LiteralPath $console) { Remove-Item -LiteralPath $console }
    $port = 29791
    $settings = Get-KitLauncherSettings
    $launcher = $server = $master = $game = $null
    $window = [IntPtr]::Zero
    $password = 'kit' + [guid]::NewGuid().ToString('N')
    try {
        if ($Route -ne 'hosting') {
            $entry = New-RunContext 'local-server'
            $serverHome = $entry.Folder
            $argsText = (Get-EngineArgs $serverHome $GamePath -Port $port) +
                " +set dedicated 1 +set g_gametype dm +set scr_dm_timelimit 1 +set rcon_password $password" +
                ' +set sv_hostname Kit-Loopback +set sv_mapRotation "gametype dm map mp_toujane gametype dm map mp_harbor" +map mp_toujane'
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
                $key = "HKCU:\Software\Classes\$scheme\shell\open\command"
                Stop-KitIfMissing $key 'The kit link handler was not registered.'
                if ((Get-Item -LiteralPath $key).GetValue('') -ine ('"{0}" "%1"' -f $exe)) {
                    throw 'The link handler points to a different program.'
                }
                # The signed-in Shell uses the registered association.
                Start-Process -FilePath ("${scheme}://127.0.0.1:$port")
                Invoke-KitControl $app 'Connect' -Name
            }
            'list' {
                Invoke-KitControl $app 'All' -Name
                Invoke-KitControl $app 'RefreshButton'
                Invoke-KitControl $app 'Kit-Loopback' -Name
                Invoke-KitControl $app 'JoinButton'
            }
            'hosting' {
                Invoke-KitControl $app 'Hosting' -Name
                Invoke-KitControl $app 'New server' -Name
                Set-KitEdit $app 'Server name' 'Kit host' -Name
                Invoke-KitControl $app 'Start server' -Name
                $port = 28960
                Wait-KitCondition { (Request-KitServer $port 'getstatus Kit') -match '\\sv_hostname\\Kit host' } 45 'the launcher-hosted server' | Out-Null
                Invoke-KitControl $app 'Join' -Name
            }
        }
        $game = Get-KitOwnedGame $Edition $since $launcher.Process.SessionId
        $window = Get-KitGameWindow $game
        $reply = Wait-KitCondition {
            $status = Request-KitServer $port 'getstatus Kit'
            if ($status -match '(?m)^-?\d+ +\d+ +".*"') { $status }
        } 50 'a player in the loopback match'
        Protect-KitText $reply | Set-Content -LiteralPath (Join-Path $Results 'player-status.txt') -Encoding UTF8
        if ($Route -eq 'connect') {
            Send-KitCommand $window '/toggleconsole; record kit-match'
            Start-Sleep -Seconds 5
            Send-KitCommand $window '/toggleconsole; stoprecord'
            $demo = Join-Path $LauncherHome 'main\demos\kit-match.dm_1'
            Wait-KitCondition { (Test-Path -LiteralPath $demo) -and (Get-Item -LiteralPath $demo).Length -gt 0 } 12 'a recorded demo' | Out-Null
            Copy-Item -LiteralPath $demo -Destination $Results
            $games = Join-Path $serverHome 'main\games_mp.log'
            Wait-KitCondition { (Test-Path -LiteralPath $games) -and (Select-String -LiteralPath $games -SimpleMatch 'ExitLevel' -Quiet) } 80 'the round to end' | Out-Null
            Request-KitServer $port "rcon $password map mp_harbor" | Out-Null
            Wait-KitCondition { (Request-KitServer $port 'getstatus Kit') -match 'mp_harbor' } 45 'Harbor to load' | Out-Null
            Save-KitFrame $window $LauncherHome $Results 'kit-loopback-harbor'
        }
        Stop-KitOwnedGame $game $window
        if ($Route -eq 'hosting') {
            Invoke-KitControl $app 'Stop server' -Name
            Wait-KitCondition { -not (Request-KitServer $port 'getstatus Kit') } 15 'Hosting to stop' | Out-Null
        }
    } finally {
        Stop-KitOwnedGame $game $window
        Save-GameEvidence $LauncherHome $Results
        if ($launcher) { Stop-KitLauncher $launcher $Results }
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

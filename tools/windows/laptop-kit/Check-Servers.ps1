function Receive-Status($Socket, $Process, [string]$Query, [string]$Kind) {
    $challenge = 'laptop-' + [guid]::NewGuid().ToString('N').Substring(0,12)
    $body = [Text.Encoding]::ASCII.GetBytes("$Query $challenge")
    [byte[]]$packet = @(255, 255, 255, 255) + $body
    $deadline = (Get-Date).AddSeconds(60)
    $peer = New-Object Net.IPEndPoint([Net.IPAddress]::Any, 0)
    do {
        try {
            [void]$Socket.Send($packet, $packet.Length)
            $response = $Socket.Receive([ref]$peer)
        } catch [Net.Sockets.SocketException] { continue }
        if ($response.Length -lt 5 -or ($response[0..3] -join ',') -ne '255,255,255,255') { continue }
        $text = [Text.Encoding]::ASCII.GetString($response, 4, $response.Length - 4)
        if ($text.StartsWith($Kind + "`n") -and $text -match ('\\challenge\\' + $challenge + '(\\|\r?\n|$)')) { return $text }
    } while ((Get-Date) -lt $deadline -and -not $Process.HasExited)
    throw 'The game server did not answer on this PC.'
}

function Save-ServerQueries($Process, [string]$Evidence, [int]$Port, [string]$Protocol,
    [string]$Address = '127.0.0.1', [string]$MapName = 'mp_toujane', [string]$GameType = 'dm', [switch]$SampleConfig) {
    $socket = New-Object Net.Sockets.UdpClient
    $socket.Client.ReceiveTimeout = 500
    try {
        $socket.Connect($Address, $Port)
        foreach ($spec in @(
            @{ Query = 'getinfo'; Kind = 'infoResponse'; Protocol = '118'; GameKey = 'gametype'; HostKey = 'hostname' },
            @{ Query = 'getstatus'; Kind = 'statusResponse'; Protocol = $Protocol; GameKey = 'g_gametype'; HostKey = 'sv_hostname' }
        )) {
            $reply = Receive-Status $socket $Process $spec.Query $spec.Kind
            $reply | Set-Content -LiteralPath (Join-Path $Evidence ($spec.Query + '.txt')) -Encoding ASCII
            $pairs = ($reply -split "`n")[1].TrimEnd("`r").Split('\')
            $fields = @{}
            for ($i = 1; $i + 1 -lt $pairs.Count; $i += 2) { $fields[$pairs[$i]] = $pairs[$i + 1] }
            if ($fields.protocol -ne $spec.Protocol -or $fields.mapname -ne $MapName -or $fields[$spec.GameKey] -ne $GameType) {
                throw 'The server reply showed different game details than expected. See results.'
            }
            if ($SampleConfig -and $fields[$spec.HostKey] -ne 'CoD2 Unbound LAN') {
                throw 'The server did not use the included sample configuration.'
            }
        }
    } finally { $socket.Dispose() }
}

function Expand-ServerPackage {
    $zip = Get-ChildItem -LiteralPath (Join-Path $Kit 'install') -Filter 'CoD2-Unbound-*-server.zip' -File
    $target = Join-Path $Private 'Server zip contents'
    Expand-Archive -LiteralPath $zip.FullName -DestinationPath $target
    foreach ($line in Get-Content -LiteralPath (Join-Path $target 'SHA256SUMS')) {
        if ($line -notmatch '^([a-f0-9]{64})  (.+)$') { throw 'The server ZIP checksum list is damaged.' }
        $expected = $Matches[1]
        $path = Join-Path $target $Matches[2]
        if ((Get-FileHash -LiteralPath $path).Hash -ine $expected) { throw 'A server ZIP member has the wrong checksum.' }
    }
    return $target
}

function Invoke-ServerCheck([string]$Edition, [string]$GamePath, [string]$BinaryRoot, [string]$Label) {
    $entry = New-RunContext $Label
    $homePath = New-Folder (Join-Path $entry.Folder 'game')
    $main = New-Folder (Join-Path $homePath 'main')
    Copy-Item -LiteralPath (Join-Path $BinaryRoot 'server.cfg') -Destination $main
    $argsText = (Get-EngineArgs $homePath $GamePath) +
        ' +set dedicated 1 +exec server.cfg +set sv_master1 "" +set logfile 2 +map mp_toujane'
    $protocol = if ($Edition -eq 'original') { '118' } else { '120' }
    $envs = @{ SYSDIFF_STATEHASH = 'statehash.txt'; SYSDIFF_MAXFRAMES = '200' }
    $run = Start-KitProcess (Get-Binary $Edition -Server -Root $BinaryRoot) $argsText $entry.Folder $envs
    try { Save-ServerQueries $run.Process $entry.Evidence 29676 $protocol -SampleConfig }
    finally {
        $done = Complete-KitProcess $run $entry.Evidence 90
        Save-GameEvidence $homePath $entry.Evidence
    }
    $trace = Join-Path $entry.Folder 'statehash.txt'
    if ($done.TimedOut -or $done.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $trace) -or
        @(Get-Content -LiteralPath $trace).Count -ne 200) { throw 'The server did not finish its 200-tick check.' }
    Copy-Item -LiteralPath $trace -Destination $entry.Evidence
}

function Read-LanAddress {
    $address = Read-Host 'Enter the host PC local IPv4 address on this network, or press Enter to go back'
    if (-not $address) { return }
    $ip = $null
    if ($address -notmatch '^\d{1,3}(\.\d{1,3}){3}$' -or -not [Net.IPAddress]::TryParse($address, [ref]$ip) -or
        $ip.ToString() -ne $address -or $address -notmatch '^(10\.|172\.(1[6-9]|2\d|3[01])\.|192\.168\.|169\.254\.)') {
        throw 'Use the host PC address from your private test network.'
    }
    $address
}

function Invoke-LanSessions([string]$GamePath) {
    $number = 0
    do {
        $edition = Read-Host 'Choose O for Original or X for CoD2x, or press Enter to finish LAN checks'
        if (-not $edition) { break }
        if ($edition -notin @('o','x')) { continue }
        $edition = if ($edition -eq 'o') { 'original' } else { 'cod2x' }
        try {
            $address = Read-LanAddress
            if (-not $address) { continue }
            $number++
            $role = Read-Host 'Enter H to host, B to open the LAN browser, or J to join the host directly'
            if ($role -in 'b','j') {
                if ($role -eq 'j') { Invoke-ClientCheck $edition $GamePath -Label "lan-$number-join" -JoinAddress "${address}:29676" }
                else { Invoke-ClientCheck $edition $GamePath -Label "lan-$number-browser" -Lan }
            } elseif ($role -eq 'h') {
                $choice = Read-Host 'Choose D for Toujane Deathmatch, S for Brecourt Search & Destroy, C for Carentan Capture the Flag, H for Dawnville Headquarters, B for Burgundy Deathmatch, A for Harbor Deathmatch or L for Leningrad Team Deathmatch'
                $cases = @{ d = @('mp_toujane','dm'); s = @('mp_brecourt','sd'); c = @('mp_carentan','ctf')
                    h = @('mp_dawnville','hq'); a = @('mp_harbor','dm'); b = @('mp_burgundy','dm'); l = @('mp_leningrad','tdm') }
                if ($choice -notin $cases.Keys) { continue }
                $mapName, $gameType = $cases[$choice]
                $entry = New-RunContext "lan-$number-host"
                $homePath = New-Folder (Join-Path $entry.Folder 'game')
                $argsText = (Get-EngineArgs $homePath $GamePath -Address $address) + ' +set dedicated 1 +set sv_voice 1 +set g_logSync 1'
                if ($edition -eq 'cod2x' -and (Read-Host 'Enter C for competitive rules, or press Enter for normal rules') -eq 'c') {
                    $argsText += ' +set g_competitive 1 +set sv_fps 40'
                }
                $argsText += " +set g_gametype $gameType +map $mapName"
                $run = Start-KitProcess (Get-Binary $edition -Server) $argsText $entry.Folder
                try {
                    $protocol = if ($edition -eq 'original') { '118' } else { '120' }
                    Save-ServerQueries $run.Process $entry.Evidence 29676 $protocol $address $mapName $gameType
                    Write-Host "Host ready on ${address}:29676."
                    if ((Read-Host 'Type P to play on this PC as well, or press Enter to wait for the other PC') -eq 'p') {
                        try {
                            Invoke-ClientCheck $edition $GamePath -Label "lan-$number-host-player" -JoinAddress "${address}:29676"
                        } catch { Write-Warning $_.Exception.Message }
                    }
                    Write-Host 'When finished, type quit in the CoD2 server console.'
                } finally {
                    $done = Complete-KitProcess $run $entry.Evidence 1800
                    Save-GameEvidence $homePath $entry.Evidence
                }
                if ($done.TimedOut -or $done.ExitCode -ne 0) { throw 'The LAN host did not shut down cleanly.' }
            }
        } catch { Write-Warning $_.Exception.Message }
    } while ($true)
}

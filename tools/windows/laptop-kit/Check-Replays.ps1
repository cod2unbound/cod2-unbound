function Read-StockScript([string[]]$Archives, [string]$Member) {
    foreach ($file in $Archives) {
        $zip = [IO.Compression.ZipFile]::OpenRead($file)
        try {
            $part = $zip.GetEntry($Member)
            if ($part) {
                $reader = New-Object IO.StreamReader($part.Open(), [Text.Encoding]::GetEncoding(28591))
                try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
            }
        } finally { $zip.Dispose() }
    }
    throw "The game archives do not contain $Member."
}

function New-TestScripts([string]$GamePath, [string]$HomePath, [string]$GameType, [string]$Scenario) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    Add-Type -AssemblyName System.IO.Compression
    $archives = (Get-ChildItem -LiteralPath (Join-Path $GamePath 'main') -File -Filter '*.iwd' | Sort-Object Name -Descending).FullName
    $gameMember = "maps/mp/gametypes/$GameType.gsc"
    $teamsMember = 'maps/mp/gametypes/_teams.gsc'
    $gameCode = Read-StockScript $archives $gameMember
    $teamsCode = Read-StockScript $archives $teamsMember
    # Same stock test-client bootstrap as tools/parity/record.py.
    $activation = '(?m)^([ \t]*)//[ \t]*(thread[ \t]+maps\\mp\\gametypes\\_teams::addTestClients\(\);[ \t]*\r?)$'
    $entry = '\bthread TestClient\(team\);'
    if ([regex]::Matches($gameCode, $activation).Count -ne 1 -or [regex]::Matches($teamsCode, $entry).Count -ne 1) {
        throw 'The installed scripts differ from the stock replay setup.'
    }
    $gameCode = $gameCode -creplace $activation, '$1$2'
    if ($Scenario) {
        $gameCode = $gameCode -creplace '(?m)^([ \t]*)thread[ \t]+maps\\mp\\gametypes\\_teams::addTestClients\(\);[ \t]*\r?$', ('$0' + "`n" + '$1thread oracleFixture();')
        $gameCode += "`n" + [IO.File]::ReadAllText($Scenario) + "`n"
    }
    $teamsCode = $teamsCode -creplace $entry, 'thread oracleTestClient(team);'
    $teamsCode += @'

oracleTestClient(team)
{
    self endon("disconnect");
    wait 0.25;
    if(team == "allies")
        self [[level.allies]]();
    else
        self [[level.axis]]();
    wait 0.5;
    if(team == "allies")
        self [[level.weapon]]("thompson_mp");
    else
        self [[level.weapon]]("mp40_mp");
}
'@
    $main = New-Folder (Join-Path $HomePath 'main')
    $scripts = [ordered]@{ $gameMember = $gameCode; $teamsMember = $teamsCode + "`n" }
    if (-not $Scenario -and $GameType -eq 'dm') {
        foreach ($member in $scripts.Keys) {
            $path = Join-Path $main $member
            New-Folder (Split-Path $path -Parent) | Out-Null
            [IO.File]::WriteAllText($path, $scripts[$member], [Text.Encoding]::GetEncoding(28591))
        }
    } else {
        $zip = [IO.Compression.ZipFile]::Open((Join-Path $main 'zzz_oracle.iwd'), 'Create')
        try {
            foreach ($member in $scripts.Keys) {
                $file = $zip.CreateEntry($member)
                $file.LastWriteTime = [DateTimeOffset]::Parse('1980-01-01T00:00:00+00:00')
                $writer = New-Object IO.StreamWriter($file.Open(), [Text.Encoding]::GetEncoding(28591))
                try { $writer.Write($scripts[$member]) } finally { $writer.Dispose() }
            }
        } finally { $zip.Dispose() }
    }
}

function Read-GzipLines([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    try {
        $gzip = New-Object IO.Compression.GzipStream($stream, [IO.Compression.CompressionMode]::Decompress)
        $reader = New-Object IO.StreamReader($gzip)
        try { while (-not $reader.EndOfStream) { $reader.ReadLine() } }
        finally { $reader.Dispose() }
    } finally { $stream.Dispose() }
}

function Invoke-Replay([string]$Edition, [string]$GamePath, [string]$Name, [string]$MapName, [string]$GameType,
    [int]$Seed, [int]$Ticks, [int]$Clients, [string]$InputPath, [string]$Scenario) {
    $entry = New-RunContext "replay-$Edition-$Name"
    $homePath = New-Folder (Join-Path $entry.Folder 'game')
    New-TestScripts $GamePath $homePath $GameType $Scenario
    Copy-Item -LiteralPath $InputPath -Destination (Join-Path $entry.Folder 'usercmds.txt')
    $vars = @{ SYSDIFF_STATEHASH = 'statehash.txt'; SYSDIFF_PLAYERS = 'players.txt'; SYSDIFF_DUMP = 'entities.txt'
        SYSDIFF_REPLAY = 'usercmds.txt'; SYSDIFF_SEED = "$Seed"; SYSDIFF_MAXFRAMES = "$Ticks"; SYSDIFF_CLIENTS = "$Clients" }
    $argsText = (Get-EngineArgs $homePath $GamePath) + ' +set dedicated 1 +set sv_pure 0 +set sv_fps 20 +set fixedtime 50' +
        " +set g_logSync 1 +set scr_testclients $Clients +set g_gametype $GameType +map $MapName"
    $run = Start-KitProcess (Get-Binary $Edition -Server) $argsText $entry.Folder $vars
    $done = Complete-KitProcess $run $entry.Evidence 120
    Save-GameEvidence $homePath $entry.Evidence
    foreach ($file in 'statehash.txt','players.txt','entities.txt') {
        $path = Join-Path $entry.Folder $file
        if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination $entry.Evidence }
    }
    if ($done.TimedOut -or $done.ExitCode -ne 0) { throw "Replay $Name exited early." }
    $hashes = @(Get-Content -LiteralPath (Join-Path $entry.Evidence 'statehash.txt'))
    $players = @(Get-Content -LiteralPath (Join-Path $entry.Evidence 'players.txt'))
    if ($hashes.Count -ne $Ticks -or -not ($players -match "^frame \d+ clients $Clients$")) {
        throw "Replay $Name did not reach $Ticks ticks and $Clients active players."
    }
    if ($Name -eq 'standing') {
        foreach ($pair in @(@('statehash',$hashes), @('players',$players))) {
            $reference = @(Read-GzipLines (Join-Path $Kit "replays\standing\$Edition-$($pair[0]).txt.gz"))
            $captured = $pair[1]
            if ($reference.Count -ne $captured.Count) { throw "Unexpected $($pair[0]) length in $Name." }
            for ($i = 0; $i -lt $reference.Count; $i++) {
                if ($reference[$i] -cne $captured[$i]) { throw "$Name $($pair[0]) differs at line $($i + 1)." }
            }
        }
    }
    "$Edition $Name reached $Ticks ticks and $Clients active players." |
        Set-Content -LiteralPath (Join-Path $entry.Evidence 'comparison.txt')
}

function Invoke-RetailReplay([string]$RetailPath, [string]$GamePath) {
    $retailExe = Join-Path $RetailPath 'CoD2MP_s.exe'
    if ((Get-FileHash -LiteralPath $retailExe).Hash -ine '8f482e647b250babe098c6e3deb10129f9a935d013e852c99689cf8dff0d12cc') {
        throw 'The retail game does not match the executable for the saved replay.'
    }
    $entry = New-RunContext 'retail-replay'
    $copy = New-Folder (Join-Path $entry.Folder 'game')
    New-ClassicCopy $RetailPath $GamePath $copy
    Rename-Item -LiteralPath (Join-Path $copy 'mss32.dll') -NewName 'miles_retail.dll'
    Copy-Item -LiteralPath (Join-Path $Kit 'probes\retail-replay\mss32.dll') -Destination $copy
    Copy-Item -LiteralPath (Join-Path $Kit 'replays\standing\usercmds.txt') -Destination $entry.Folder
    New-TestScripts $GamePath $copy 'dm' ''
    $settings = @{ COD2_RETAIL_REPLAY = '1'; COD2_RETAIL_TICKS = '600'; COD2_RETAIL_CLIENTS = '2'
        COD2_RETAIL_COMMANDS = '1004'; COD2_RETAIL_SEED = '12345' }
    $argsText = (Get-EngineArgs $copy $copy) +
        ' +set fs_cdpath "" +set dedicated 1 +set g_gametype dm +set sv_pure 0 +set sv_fps 20 +set scr_testclients 2 +map mp_toujane'
    $run = Start-KitProcess (Join-Path $copy 'CoD2MP_s.exe') $argsText $copy $settings
    $done = Complete-KitProcess $run $entry.Evidence 120
    Save-GameEvidence $copy $entry.Evidence
    $binary = Join-Path $entry.Folder 'capture.bin'
    $log = Join-Path $entry.Folder 'recorder.log'
    Save-Text $log (Join-Path $entry.Evidence 'recorder.log')
    if ($done.TimedOut -or $done.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $binary) -or
        -not (Select-String -LiteralPath $log -Pattern 'Retail recorder wrote 600 ticks and injected 1004 commands for 2 clients' -Quiet)) {
        throw 'The retail replay did not finish. See results for its recorder log.'
    }
    Copy-Item -LiteralPath $binary -Destination $entry.Evidence
}

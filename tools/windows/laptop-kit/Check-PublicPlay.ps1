function Assert-KitPublicPlay([bool]$Online, [bool]$Rehearsal) {
    if (-not $Online -or $Rehearsal) { throw 'Internet play requires an online physical laptop run.' }
}

function Get-KitPublicRows([IntPtr]$Window) {
    $list = Find-KitControl $Window 'ServerList'
    if (-not $list) { return }
    $items = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem)
    foreach ($item in $list.FindAll([Windows.Automation.TreeScope]::Descendants, $items)) {
        $title = $item.Current.Name
        if ($title -match '\b(\d+) of (\d+) players\b') {
            [pscustomobject]@{ Element=$item; Title=$title; Players=[int]$Matches[1]; Slots=[int]$Matches[2] }
        }
    }
}

function Get-KitJoinCandidates($Rows, [string]$Edition) {
    $label = if ($Edition -eq 'original') { 'Original' } else { 'CoD2x' }
    @($Rows | Where-Object {
        $_.Players -gt 0 -and $_.Players -lt $_.Slots -and $_.Title -match ", $label\b" -and
            $_.Title -notmatch 'needs a password|PunkBuster|anti.cheat'
    } | Sort-Object @{Expression={ if ($_.Title -match ', (Toujane|Harbor|Burgundy|Carentan|Dawnville|Brecourt|Leningrad),') { 0 } else { 1 } }})
}

function Find-KitPublicStop([string]$Text) {
    ($Text -split '\r?\n' | Where-Object {
        $_ -match '(?i)\b(?:kicked|banned|anti-cheat|cheat violation|invalid checksum|protocol mismatch|version mismatch|unpure client)\b' -and
            $_ -notmatch '^(?:dvar set |CoD2x:\s*module detection)'
    } | Select-Object -First 1)
}

function Read-KitPublicConsole([string]$Log, [long]$Start) {
    if (-not (Test-Path -LiteralPath $Log)) { return '' }
    $text = Read-KitText $Log
    if ($text.Length -gt $Start) { return $text.Substring($Start) }
    ''
}

function Get-KitPublicHostResult([IntPtr]$App, [string]$RoomName) {
    foreach ($row in Get-KitPublicRows $App) {
        if ($row.Title.StartsWith("$RoomName, ", [StringComparison]::Ordinal)) {
            $row.Element.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
            $detail = Find-KitControl $App 'DetailAddress' -AllowOffscreen
            if ($detail -and $detail.Current.Name -match '^[a-zA-Z0-9.-]+:\d+$') { return $detail.Current.Name }
        }
    }
}

function Select-KitComboChoice([IntPtr]$Window, [string]$Name, [string]$Choice) {
    $node = Wait-KitCondition { Find-KitControl $Window $Name -Name } 12 $Name
    $node.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    $item = Wait-KitCondition { Find-KitControl $Window $Choice -Name } 8 $Choice
    $item.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $node.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
}

function Invoke-KitPublicHosting([IntPtr]$App, [string]$Edition, [switch]$Local) {
    $LauncherHome = $AppDataRoot
    $name = 'Unbound test ' + [guid]::NewGuid().ToString('N').Substring(0, 10)
    $version = if ($Edition -eq 'original') { 'Original 1.3' } else { 'CoD2x 1.4.6.8' }
    $reach = if ($Local) { 'Local network' } else { 'Public server list' }
    $entry = New-RunContext "hosting-$Edition-$($Local.IsPresent)"
    $room = $null
    $failure = $null
    $since = Get-Date
    try {
        Focus-KitWindow $App
        Invoke-KitControl $App 'ServersItem'
        Invoke-KitControl $App 'Hosting' -Name
        Invoke-KitControl $App 'New server' -Name
        Set-KitEdit $App 'Server name' $name -Name
        Select-KitComboChoice $App 'Game version' $version
        Select-KitComboChoice $App 'Who can join' $reach
        Invoke-KitControl $App 'Start server' -Name
        $room = Wait-KitCondition { Find-KitHostedServer $name } 20 'the saved test room'
        Wait-KitCondition { (Request-KitServer $room.Port 'getstatus Kit') -like "*$name*" } 45 'the test room to answer locally' | Out-Null
        if (-not $Local) {
            $watch = [Diagnostics.Stopwatch]::StartNew()
            Invoke-KitControl $App 'RefreshButton'
            $address = $null
            while ($watch.Elapsed.TotalSeconds -lt 60) {
                Wait-KitSecurity
                if (-not $address) { $address = Get-KitPublicHostResult $App $name }
                if (-not $address -and $watch.Elapsed.TotalSeconds -ge 45 -and (Find-KitControl $App 'RefreshButton')) {
                    Invoke-KitControl $App 'RefreshButton'
                }
                Start-Sleep -Seconds 2
            }
            if (-not $address) { throw 'The test room answered locally but was not visible in the public list within one minute.' }
            "$reach. $name appeared at $address." |
                Set-Content -LiteralPath (Join-Path $entry.Evidence 'hosting.txt') -Encoding UTF8
        } else {
            Start-Sleep -Seconds 10
            Assert-KitHostedLookups $room.Id $since
            "$reach. The room answered on loopback and the engine log had no external lookup." |
                Set-Content -LiteralPath (Join-Path $entry.Evidence 'hosting.txt') -Encoding UTF8
        }
    } catch { $failure = $_ }
    finally {
        if ($room) {
            Focus-KitWindow $App
            Invoke-KitControl $App 'Hosting' -Name
            if (Request-KitServer $room.Port 'getstatus Kit') {
                Invoke-KitControl $App 'Stop server' -Name
                Wait-KitCondition { -not (Request-KitServer $room.Port 'getstatus Kit') } 20 'the test room to stop' | Out-Null
            }
            $logs = Join-Path $LauncherHome "hosting\servers\$($room.Id)\logs"
            foreach ($file in Get-ChildItem -LiteralPath $logs -File -Filter 'engine-*.log' -ErrorAction SilentlyContinue) {
                if ($file.LastWriteTime -ge $since) { Save-Text $file.FullName (Join-Path $entry.Evidence $file.Name) }
            }
        }
    }
    if ($failure) { throw $failure }
}

function Watch-KitPublicConnection([string]$Log, [long]$Start, [string]$Needle, [int]$Seconds, [string]$Evidence) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        Wait-KitSecurity
        $text = Read-KitPublicConsole $Log $Start
        if ($line = Find-KitPublicStop $text) {
            Protect-KitText $line | Add-Content -LiteralPath (Join-Path $Evidence 'server-refusal.txt') -Encoding UTF8
            throw $line
        }
        if ($Needle -and $text -match $Needle) { return }
        Start-Sleep -Milliseconds 500
    } while ($watch.Elapsed.TotalSeconds -lt $Seconds)
    if ($Needle) { throw "The game did not report $Needle within $Seconds seconds." }
}

function Join-KitPublicRow([IntPtr]$App, $Row, [string]$Edition, [string]$Evidence) {
    $Row.Element.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $address = Wait-KitCondition {
        $node = Find-KitControl $App 'DetailAddress' -AllowOffscreen
        if ($node -and $node.Current.Name -match '^[a-zA-Z0-9.-]+:\d+$') { $node.Current.Name }
    } 12 'the selected server address'
    Protect-KitText "$address $($Row.Title)" | Add-Content -LiteralPath (Join-Path $Evidence 'servers-joined.txt') -Encoding UTF8
    Invoke-KitControl $App 'JoinButton'
    $version = if ($Edition -eq 'original') { 'Original 1.3' } else { 'CoD2x' }
    if (Find-KitControl $App "Launch with $version" -Name) { Invoke-KitControl $App "Launch with $version" -Name }
}

function Invoke-KitPublicEdition([IntPtr]$App, $Launcher, [string]$Edition) {
    $entry = New-RunContext "play-$Edition"
    $log = Join-Path $AppDataRoot 'main\console_mp.log'
    $game = $null
    $gameWindow = [IntPtr]::Zero
    $activeEdition = if ($Edition -eq 'original') { 'Original 1.3' } else { 'CoD2x' }
    $traceOffset = 0
    try {
        Focus-KitWindow $App
        Invoke-KitControl $App 'ServersItem'
        Invoke-KitControl $App 'All' -Name
        $watch = [Diagnostics.Stopwatch]::StartNew()
        Invoke-KitControl $App 'RefreshButton'
        Wait-KitCondition { Get-KitPublicRows $App | Select-Object -First 1 } 65 'the first public server' | Out-Null
        "First server after $($watch.ElapsedMilliseconds) ms." |
            Set-Content -LiteralPath (Join-Path $entry.Evidence 'server-list.txt') -Encoding UTF8
        Wait-KitCondition { Find-KitControl $App 'RefreshButton' } 65 'the server refresh to finish' | Out-Null
        $rows = @(Get-KitPublicRows $App)
        "Accessible rows: $($rows.Count)." | Add-Content -LiteralPath (Join-Path $entry.Evidence 'server-list.txt')
        Save-KitWindow $App (Join-Path $entry.Evidence 'public-list.png')
        $candidates = @(Get-KitJoinCandidates $rows $Edition)
        if ($candidates.Count -lt 2) {
            throw [NotSupportedException]::new("The list did not expose two populated $activeEdition servers with room and no password.")
        }
        $since = Get-Date
        Join-KitPublicRow $App $candidates[0] $Edition $entry.Evidence
        # A server can require a different game version, which is an explicit
        # launcher choice. A password or download dialog never gets accepted here.
        $game = Wait-KitCondition {
            if (Find-KitControl $App "Launch with $activeEdition" -Name) {
                Invoke-KitControl $App "Launch with $activeEdition" -Name
            }
            $image = Get-Binary $Edition -Root $InstallRoot
            Get-Process -Name 'cod2_win32' -ErrorAction SilentlyContinue | Where-Object {
                $_.Path -ieq $image -and $_.StartTime -ge $since -and $_.SessionId -eq $Launcher.SessionId
            } | Select-Object -First 1
        } 50 "the $activeEdition installed game process"
        $owner = Get-CimInstance Win32_Process -Filter "ProcessId=$($game.Id)"
        if ($owner.ParentProcessId -ne $Launcher.Id) { throw 'The installed play launcher did not start this process.' }
        $gameWindow = Get-KitGameWindow $game
        Watch-KitPublicConnection $log 0 'CL_InitCGame:' 65 $entry.Evidence
        Focus-KitWindow $gameWindow
        Send-KitCommand $gameWindow '/toggleconsole; cmd team spectator'
        Save-KitWindow $gameWindow (Join-Path $entry.Evidence 'public-first-game.png')
        Watch-KitPublicConnection $log 0 '' 120 $entry.Evidence
        if ($game.HasExited) { throw 'The game closed during the two-minute observation.' }
        $traceOffset = (Read-KitPublicConsole $log 0).Length

        Focus-KitWindow $App
        Join-KitPublicRow $App $candidates[1] $Edition $entry.Evidence
        Watch-KitPublicConnection $log $traceOffset 'CL_InitCGame:' 65 $entry.Evidence
        if ($game.HasExited) { throw 'The earlier game exited during the second join.' }
        Focus-KitWindow $gameWindow
        Save-KitWindow $gameWindow (Join-Path $entry.Evidence 'public-second-game.png')
        if ($Edition -eq 'cod2x') {
            $clientLog = Read-KitPublicConsole $log 0
            if ($clientLog -notmatch 'CoD2x: No updates available\.' -or $clientLog -match 'CoD2x: Update available|CoD2x: Downloading') {
                throw 'The CoD2x service did not confirm that version 1.4.6.8 was current without a download.'
            }
        }

        Focus-KitWindow $App
        Invoke-KitControl $App 'HomeItem'
        Invoke-KitControl $App 'Return to game' -Name
        Wait-KitCondition { [KitDesktop]::GetForegroundWindow() -eq $gameWindow } 10 'Return to game' | Out-Null
        Focus-KitWindow $App
        Invoke-KitControl $App 'Close game' -Name
        if (-not $game.WaitForExit(18000)) { throw 'Close game did not end the observed game session.' }
        'Two joins used the same game process. Return to game and Close game both worked.' |
            Add-Content -LiteralPath (Join-Path $entry.Evidence 'server-list.txt')
    } finally {
        $text = Read-KitPublicConsole $log 0
        if ($text) {
            $lines = $text -split '\r?\n' | Where-Object {
                $_ -match '(?i)challenge|authoriz|protocol|downloading|download complete|lost device|^CoD2x: (?:No updates available|Update available)|slow frame|CL_InitCGame|connecting to'
            }
            Protect-KitText ($lines -join "`n") |
                Set-Content -LiteralPath (Join-Path $entry.Evidence 'connection.txt') -Encoding UTF8
        }
        Stop-KitOwnedGame $game $gameWindow
    }
}

function Invoke-KitPublicPlay {
    Assert-KitPublicPlay ($env:COD2_KIT_ONLINE -eq '1') ($env:COD2_KIT_ARM_REHEARSAL -eq '1')
    if ($env:PROCESSOR_ARCHITECTURE -ne 'AMD64' -or
        -not (Get-NetRoute | Where-Object DestinationPrefix -in '0.0.0.0/0','::/0')) {
        throw 'Internet play needs an x64 Windows PC with a working internet route.'
    }
    Stop-KitIfNoGame
    Initialize-KitDesktop
    Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
    $exe = Join-Path $InstallRoot 'launcher\CoD2Unbound.Windows.exe'
    $assembly = [IO.Path]::ChangeExtension($exe, '.dll')
    if (-not (Test-Path -LiteralPath $assembly -PathType Leaf) -or
        [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($assembly)).Contains('CoD2Unbound.UpdateFixture')) {
        throw 'Install the separate Public play build before giving consent.'
    }
    if (Get-Process -Name 'CoD2Unbound.Windows','cod2_win32','cod2_win32_ded' -ErrorAction SilentlyContinue) {
        throw 'Close the launcher, game and hosted servers before this play check starts.'
    }
    Save-KitGraphicsPreferences $InstallRoot $Results
    # WinUI and its child game use the installed Public product path.
    # Remove any test override inherited by the scheduled worker.
    $info = [Diagnostics.ProcessStartInfo]::new($exe)
    $info.UseShellExecute = $false
    foreach ($name in @($info.EnvironmentVariables.Keys)) {
        if ($name -match '^(?:COD2X_TEST_|COD2_UNBOUND_TEST_|COD2_USAGE_URL$|COD2_CRASH_URL$)') {
            $info.EnvironmentVariables.Remove($name)
        }
    }
    $app = [Diagnostics.Process]::Start($info)
    try {
        $window = Get-KitLauncherWindow $app
        Wait-KitHome $window
        Invoke-KitControl $window 'Settings' -Name
        Invoke-KitControl $window 'Game data' -Name
        Wait-KitCondition { Find-KitControl $window ("Verified " + [char]0x00b7 + ' Stored only on this PC') -Name -AllowOffscreen } 12 'a verified CD key reported by the launcher' | Out-Null
        Invoke-KitControl $window 'Updates' -Name
        if (Find-KitControl $window 'Check now' -Name) { Invoke-KitControl $window 'Check now' -Name }
        $version = (Read-InstalledVersion 'HKCU' $InstallRoot)
        Save-KitSurface $window 'public-launcher-update' @("CoD2 Unbound $version is up to date")
        foreach ($edition in 'original','cod2x') {
            if ($edition -eq 'cod2x') { Invoke-KitHwidCheck (Get-Binary $edition -Root $InstallRoot) $GamePath }
            Invoke-KitPublicEdition $window $app $edition
            Invoke-KitPublicHosting $window $edition -Local
            Invoke-KitPublicHosting $window $edition
        }
    } finally {
        if (-not $app.HasExited) {
            [void]$app.CloseMainWindow()
            if (-not $app.WaitForExit(10000)) { $app.Kill(); [void]$app.WaitForExit(5000) }
        }
        $app.Dispose()
    }
}

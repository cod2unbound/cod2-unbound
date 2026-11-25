function Assert-KitPublicPlay([bool]$Online, [bool]$Rehearsal) {
    if (-not $Online -or $Rehearsal) { throw 'Internet play requires an online physical laptop run.' }
}

function New-KitPublicStartInfo([string]$Image) {
    $info = [Diagnostics.ProcessStartInfo]::new($Image)
    $info.UseShellExecute = $false
    foreach ($name in @($info.EnvironmentVariables.Keys)) {
        if ($name -match '^(?:COD2X_TEST_|COD2_UNBOUND_TEST_|COD2_USAGE_URL$|COD2_CRASH_URL$|COD2_FRAME_TRACE_FILE$)') {
            $info.EnvironmentVariables.Remove($name)
        }
    }
    $info
}

function Wait-KitCurrentUpdater([string]$Log, $Game, [string]$Evidence) {
    Wait-KitCondition {
        if ($Game.HasExited) { throw 'CoD2x closed before its update service replied.' }
        if (-not (Test-Path -LiteralPath $Log -PathType Leaf)) { return }
        $console = Read-KitText $Log
        if ($console -match 'CoD2x: Update available|CoD2x: Downloading') {
            throw 'The CoD2x service offered an update during the 1.4.6.8 check.'
        }
        $console -match 'CoD2x: No updates available\.'
    } 45 'the CoD2x current-version reply' | Out-Null
    (Read-KitText $Log) -split '\r?\n' | Where-Object {
        $_ -match '^(Auto-Updater:|CoD2x: No updates available\.|Direct3D 9 adapter:)'
    } | ForEach-Object { Protect-KitText $_ } |
        Set-Content -LiteralPath (Join-Path $Evidence 'cod2x-updater.txt') -Encoding UTF8
}

function Invoke-KitPublicUpdater([string]$Binary, [string]$DataPath) {
    $entry = New-RunContext 'cod2x-updater'
    New-TestProfile $entry.Folder
    $before = (Get-FileHash -LiteralPath $Binary).Hash
    $info = New-KitPublicStartInfo $Binary
    $info.WorkingDirectory = $entry.Folder
    $info.EnvironmentVariables['HOME'] = $entry.Folder
    $info.EnvironmentVariables['CFFIXED_USER_HOME'] = $entry.Folder
    $info.EnvironmentVariables['COD2_PROFILE_CHOSEN'] = '1'
    $info.Arguments = (Get-EngineArgs $entry.Folder $DataPath -Address '0.0.0.0') +
        ' +set r_fullscreen 0 +set r_mode 800x600 +set r_aaSamples 1 +set com_introPlayed 1'
    $game = [Diagnostics.Process]::Start($info)
    $window = [IntPtr]::Zero
    try {
        $window = Get-KitGameWindow $game
        Wait-KitCurrentUpdater (Join-Path $entry.Folder 'main\console_mp.log') $game $entry.Evidence
    } finally {
        try { Stop-KitOwnedGame $game $window } finally { $game.Dispose() }
    }
    if ((Get-FileHash -LiteralPath $Binary).Hash -ne $before) { throw 'The CoD2x binary changed during its update check.' }
    'The installed binary was unchanged.' | Add-Content -LiteralPath (Join-Path $entry.Evidence 'cod2x-updater.txt')
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
    } | Sort-Object @{Expression={ if ($_.Title -match ", $label [^,]+, \d+ of \d+ players") { 0 } else { 1 } }},
        @{Expression={ if ($_.Title -match ', (Toujane|Harbor|Burgundy|Carentan|Dawnville|Brecourt|Leningrad),') { 0 } else { 1 } }})
}

function Find-KitPublicStop([string]$Text) {
    ($Text -split '\r?\n' | Where-Object {
        $_ -match '(?i)\b(?:kick(?:ed)?|ban(?:ned)?|anti[ -]?cheat|cheat violation|invalid checksum|checksum (?:failure|mismatch)|protocol mismatch|version mismatch|different (?:game )?version|unpure client)\b' -and
            $_ -notmatch '^(?:dvar set |CoD2x:\s*module detection)'
    } | Select-Object -First 1)
}

function Read-KitPublicConsole([IntPtr]$Window, [string]$Dump) {
    foreach ($attempt in 1..3) {
        if (Test-Path -LiteralPath $Dump) { Remove-Item -LiteralPath $Dump }
        Send-KitCommand $Window "/toggleconsole; condump $([IO.Path]::GetFileName($Dump))"
        try {
            Wait-KitCondition { Test-Path -LiteralPath $Dump -PathType Leaf } 5 'the private game console receipt' | Out-Null
            return (Read-KitText $Dump)
        } catch {
            if ($attempt -eq 3 -or $script:KitAttentionTimedOut) { throw }
        }
    }
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

function Start-KitPublicRefresh([IntPtr]$App) {
    Wait-KitCondition { Find-KitControl $App 'ServerList' } 12 'the server list' | Out-Null
    if (-not (Find-KitControl $App 'RefreshMessage')) {
        Focus-KitWindow $App
        [KitDesktop]::Chord(17, 82)
    }
}

function Get-KitRefreshStatus([IntPtr]$App) {
    $status = Find-KitControl $App 'RefreshStatus'
    if ($status) { return $status.Current.Name }
    $overflow = Find-KitControl $App 'ToolbarOverflow'
    if ($overflow) { $overflow.Current.HelpText }
}

function Get-KitPublicAnsweredCount([string]$Message) {
    if ($Message -match '^(\d+) servers? answered\.' -or
        $Message -match ('^(\d+) servers? ' + [char]0x00b7 + ' just now$')) { [int]$Matches[1] }
}

function Get-KitPublicMatchTiming([string]$HomePath, [DateTimeOffset]$Since, [string[]]$Addresses) {
    $history = Join-Path $HomePath 'main\stats\matches.jsonl'
    if (-not (Test-Path -LiteralPath $history -PathType Leaf)) { return }
    if ((Get-Item -LiteralPath $history).Attributes -band [IO.FileAttributes]::ReparsePoint) { return }
    foreach ($line in (Read-KitText $history) -split '\r?\n') {
        if (-not $line) { continue }
        $record = try { $line | ConvertFrom-Json -ErrorAction Stop } catch { continue }
        if ($record.serverAddress -in $Addresses -and [DateTimeOffset]$record.endedAt -ge $Since -and $record.frameCount -gt 0) {
            $record | Select-Object serverAddress,map,endReason,durationSeconds,frameCount,capFPS,frameTimeP50MS,frameTimeP99MS,frameTimeMaxMS
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

function Set-KitPublicSearch([IntPtr]$App, [string]$Text) {
    $edit = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Edit)
    $value = Wait-KitCondition {
        $box = Find-KitControl $App 'SearchBox'
        if ($box) {
            $elements = @($box) + @($box.FindAll([Windows.Automation.TreeScope]::Descendants, $edit))
            foreach ($element in $elements) {
                $pattern = $null
                if ($element.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) { return $pattern }
            }
        }
    } 12 'the server search field'
    $value.SetValue($Text)
}

function Invoke-KitPublicHosting([IntPtr]$App, [string]$Edition, [switch]$Local, [string]$HomePath = $AppDataRoot) {
    $LauncherHome = $HomePath
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
            Invoke-KitControl $App 'All' -Name
            Set-KitPublicSearch $App $name
            Start-KitPublicRefresh $App
            $address = $null
            $refreshedAgain = $false
            while ($watch.Elapsed.TotalSeconds -lt 60) {
                Wait-KitSecurity
                if (-not $address) { $address = Get-KitPublicHostResult $App $name }
                if (-not $address -and -not $refreshedAgain -and $watch.Elapsed.TotalSeconds -ge 30) {
                    Start-KitPublicRefresh $App
                    $refreshedAgain = $true
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
        if (-not $room) { $room = Find-KitHostedServer $name }
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
            $row = Wait-KitCondition { Find-KitControl $App $name -Name } 12 'the stopped test room'
            $row.SetFocus()
            [KitDesktop]::Chord(16, 121)
            Invoke-KitControl $App ('Delete server' + [char]0x2026) -Name
            Invoke-KitControl $App 'Delete' -Name
            Wait-KitCondition { -not (Find-KitHostedServer $name) } 15 'the disposable room to be removed' | Out-Null
            if (-not $Local) {
                Invoke-KitControl $App 'All' -Name
                Set-KitPublicSearch $App ''
            }
        }
    }
    if ($failure) { throw $failure }
}

function Watch-KitPublicConnection([IntPtr]$Window, [string]$Dump, [string]$Marker,
    [string]$Needle, [int]$Seconds, [string]$Evidence) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        Wait-KitSecurity
        $text = Read-KitPublicConsole $Window $Dump
        if ($Marker) {
            $offset = $text.LastIndexOf($Marker, [StringComparison]::Ordinal)
            $text = if ($offset -ge 0) { $text.Substring($offset) } else { '' }
        }
        if ($line = Find-KitPublicStop $text) {
            Protect-KitText $line | Add-Content -LiteralPath (Join-Path $Evidence 'server-refusal.txt') -Encoding UTF8
            throw $line
        }
        if ($Needle -and $text -match $Needle) { return }
        Start-Sleep -Seconds 3
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
    $version = if ($Edition -eq 'original') { 'Original 1.3' } else { 'CoD2x 1.4.6.8' }
    if (Find-KitControl $App "Launch with $version" -Name) { Invoke-KitControl $App "Launch with $version" -Name }
    $address
}

function Invoke-KitPublicEdition([IntPtr]$App, $Launcher, [string]$Edition, [string]$HomePath = $AppDataRoot, [string]$TracePath) {
    $entry = New-RunContext "play-$Edition"
    $dump = Join-Path $HomePath ('main\kit-play-' + [guid]::NewGuid().ToString('N') + '.txt')
    $game = $null
    $gameWindow = [IntPtr]::Zero
    $activeEdition = if ($Edition -eq 'original') { 'Original 1.3' } else { 'CoD2x 1.4.6.8' }
    $addresses = @()
    $since = $null
    $trace = if ($TracePath) { New-KitFrameTrace $entry.Evidence }
    try {
        Focus-KitWindow $App
        Invoke-KitControl $App 'ServersItem'
        Invoke-KitControl $App 'All' -Name
        $watch = [Diagnostics.Stopwatch]::StartNew()
        Start-KitPublicRefresh $App
        Wait-KitCondition { Get-KitPublicRows $App | Select-Object -First 1 } 65 'the first public server' | Out-Null
        "First server after $($watch.ElapsedMilliseconds) ms." |
            Set-Content -LiteralPath (Join-Path $entry.Evidence 'server-list.txt') -Encoding UTF8
        Wait-KitCondition {
            (Get-KitRefreshStatus $App) -and -not (Find-KitControl $App 'RefreshMessage')
        } 65 'the server refresh to finish' | Out-Null
        $rows = @(Get-KitPublicRows $App)
        $status = Get-KitRefreshStatus $App
        $answered = Get-KitPublicAnsweredCount $status
        if ($null -eq $answered -or (Find-KitControl $App 'OfflineBanner')) {
            throw 'The public masters did not confirm a completed refresh.'
        }
        Protect-KitText "$status Accessible rows: $($rows.Count)." |
            Add-Content -LiteralPath (Join-Path $entry.Evidence 'server-list.txt') -Encoding UTF8
        Save-KitWindow $App (Join-Path $entry.Evidence 'public-list.png')
        $candidates = @(Get-KitJoinCandidates $rows $Edition)
        if ($candidates.Count -lt 2) {
            throw [NotSupportedException]::new("The list did not expose two populated $activeEdition servers with room and no password.")
        }
        # Engine history timestamps use whole seconds.
        $started = [DateTime]::UtcNow
        $since = [DateTimeOffset]::FromUnixTimeSeconds(([DateTimeOffset]$started).ToUnixTimeSeconds())
        $addresses += Join-KitPublicRow $App $candidates[0] $Edition $entry.Evidence
        # A server can require a different game version, which is an explicit
        # launcher choice. A password or download dialog never gets accepted here.
        $candidate = Wait-KitCondition {
            if (Find-KitControl $App "Launch with $activeEdition" -Name) {
                Invoke-KitControl $App "Launch with $activeEdition" -Name
            }
            $image = Get-Binary $Edition -Root $InstallRoot
            Get-Process -Name 'cod2_win32' -ErrorAction SilentlyContinue | Where-Object {
                $_.Path -ieq $image -and $_.StartTime.ToUniversalTime() -ge $started -and $_.SessionId -eq $Launcher.SessionId
            } | Select-Object -First 1
        } 50 "the $activeEdition installed game process"
        $owner = Get-CimInstance Win32_Process -Filter "ProcessId=$($candidate.Id)"
        $null = $candidate.Handle
        if ($owner.ParentProcessId -ne $Launcher.Id -or
            [Math]::Abs(($candidate.StartTime - $owner.CreationDate).TotalMilliseconds) -ge 1) {
            $candidate.Dispose()
            throw 'The installed play launcher did not start this process.'
        }
        $game = $candidate
        $gameWindow = Get-KitGameWindow $game
        Start-Sleep -Seconds 2
        Watch-KitPublicConnection $gameWindow $dump '' 'CL_InitCGame:' 65 $entry.Evidence
        if ($trace) { $trace.Start = [Diagnostics.Stopwatch]::GetTimestamp() }
        Focus-KitWindow $gameWindow
        # Wait in the initial spectator state. Do not pick a team or send chat.
        Save-KitWindow $gameWindow (Join-Path $entry.Evidence 'public-first-game.png')
        Watch-KitPublicConnection $gameWindow $dump '' '' 120 $entry.Evidence
        if ($trace) { $trace.End = [Diagnostics.Stopwatch]::GetTimestamp() }
        if ($game.HasExited) { throw 'The game closed during the two-minute observation.' }
        $marker = 'kit_join_' + [guid]::NewGuid().ToString('N')
        Send-KitCommand $gameWindow "/toggleconsole; echo $marker"

        Focus-KitWindow $App
        $addresses += Join-KitPublicRow $App $candidates[1] $Edition $entry.Evidence
        Start-Sleep -Seconds 2
        Watch-KitPublicConnection $gameWindow $dump $marker 'CL_InitCGame:' 65 $entry.Evidence
        if ($game.HasExited) { throw 'The earlier game exited during the second join.' }
        Focus-KitWindow $gameWindow
        Save-KitWindow $gameWindow (Join-Path $entry.Evidence 'public-second-game.png')

        Focus-KitWindow $App
        Invoke-KitControl $App 'HomeItem'
        Invoke-KitControl $App 'Return to game' -Name
        Wait-KitCondition { [KitDesktop]::GetForegroundWindow() -eq $gameWindow } 10 'Return to game' | Out-Null
        Close-KitRunningGame $App $game
        'Two joins used the same game process. Return to game and Close game both worked.' |
            Add-Content -LiteralPath (Join-Path $entry.Evidence 'server-list.txt')
    } finally {
        try { Stop-KitOwnedGame $game $gameWindow }
        finally { if ($game) { $game.Dispose() } }
        $text = if (Test-Path -LiteralPath $dump) {
            try { Read-KitText $dump } finally { Remove-Item -LiteralPath $dump }
        }
        if ($text) {
            if ($line = Find-KitPublicStop $text) {
                Protect-KitText $line | Set-Content -LiteralPath (Join-Path $entry.Evidence 'server-refusal.txt') -Encoding UTF8
            }
            $lines = $text -split '\r?\n' | Where-Object {
                $_ -match '(?i)challenge|authoriz|protocol|download|lost device|^CoD2x: (?:No updates available|Update available)|CL_InitCGame|connecting to|^(?:Using D3D9 adapter|Direct3D 9 adapter|Direct3D device:)'
            }
            Protect-KitText ($lines -join "`n") |
                Set-Content -LiteralPath (Join-Path $entry.Evidence 'connection.txt') -Encoding UTF8
        }
        $timing = @()
        if ($addresses.Count) {
            $timing = @(Get-KitPublicMatchTiming $HomePath $since $addresses)
            if ($timing.Count) {
                Protect-KitText (ConvertTo-Json -InputObject $timing -Depth 3) |
                    Set-Content -LiteralPath (Join-Path $entry.Evidence 'match-timing.json') -Encoding UTF8
            } else {
                'The game recorded no match timing for these server addresses during the run.' |
                    Set-Content -LiteralPath (Join-Path $entry.Evidence 'match-timing.txt') -Encoding UTF8
            }
        }
        if ($trace) {
            $cold = $timing | Where-Object serverAddress -eq $addresses[0] | Select-Object -First 1
            if ($cold) { $trace.Cap = [int]$cold.capFPS }
            if (Test-Path -LiteralPath $TracePath -PathType Leaf) {
                try { Move-Item -LiteralPath $TracePath -Destination $trace.Path }
                catch { $trace.Path = $TracePath }
            }
            Save-KitFrameTrace $trace $entry.Evidence (Join-Path $entry.Evidence 'connection.txt') $Edition
        }
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
    $info = New-KitPublicStartInfo $exe
    $tracePath = Join-Path $Private 'public-frame-times.bin'
    $info.EnvironmentVariables['COD2_FRAME_TRACE_FILE'] = $tracePath
    $app = [Diagnostics.Process]::Start($info)
    try {
        $window = Get-KitLauncherWindow $app
        Wait-KitHome $window
        $settingsWindow = Open-KitSettings $window
        try {
            $panes = Find-KitControl $settingsWindow 'Panes'
            Invoke-KitControl $panes 'Game Data' -Name
            Wait-KitCondition { Find-KitControl $settingsWindow ("Verified " + [char]0x00b7 + ' Stored only on this PC') -Name -AllowOffscreen } 12 'a verified CD key reported by the launcher' | Out-Null
            Invoke-KitControl $panes 'Updates' -Name
            if (Find-KitControl $settingsWindow 'Check Now' -Name) { Invoke-KitControl $settingsWindow 'Check Now' -Name }
            $version = Read-InstalledVersion 'HKCU' $InstallRoot
            Save-KitSurface $settingsWindow 'public-launcher-update' @() @("CoD2 Unbound $version is up to date")
        } finally { [KitDesktop]::CloseOwnedWindow($settingsWindow) }
        foreach ($edition in 'original','cod2x') {
            if ($edition -eq 'cod2x') {
                $binary = Get-Binary $edition -Root $InstallRoot
                Invoke-KitHwidCheck $binary $GamePath
                Invoke-KitPublicUpdater $binary $GamePath
            }
            Invoke-KitPublicEdition $window $app $edition -TracePath $tracePath
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

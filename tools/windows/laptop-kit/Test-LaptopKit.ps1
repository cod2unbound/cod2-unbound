#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
$Mode = 'Status'
. (Join-Path $PSScriptRoot 'Common.ps1')
. (Join-Path $PSScriptRoot 'Check-Installed.ps1')
. (Join-Path $PSScriptRoot 'Check-Machine.ps1')
. (Join-Path $PSScriptRoot 'Check-PublicPlay.ps1')

# The shared action entry point must reject these before it even queries UI Automation.
foreach ($name in @('Use key from disc install' + [char]0x2026; 'Import from the installed game';
    'Import from this install'; 'From your Call of Duty 2 install' + [char]0x2026; 'ImportInstall'; 'UseDiscKey')) {
    try {
        Invoke-KitControl ([IntPtr]::Zero) $name -Name
        throw 'Unsafe action was accepted.'
    } catch {
        if ($_.Exception -isnot [ArgumentException] -or $_.Exception.Message -notlike 'Retail action is forbidden*') {
            throw
        }
    }
}
foreach ($line in @('localhost resolved to 127.0.0.2:20710', 'AutoUpdate resolved to [::1]:28960',
    'Resolving master server localhost', 'Unlisted server started')) {
    if (Find-KitExternalLookup $line) { throw "Rejected a local line: $line" }
}
foreach ($line in @('cod2master.example resolved to 192.0.2.5:20710',
    'Resolving authorization server cod2master.example',
    'AutoUpdate resolved to [2001:db8::1]:28960',
    'public.example resolved to 127.0.0.1:20710')) {
    if ((Find-KitExternalLookup $line) -cne $line) { throw "Missed an external lookup: $line" }
}
foreach ($pair in @(@($false,$false), @($true,$true))) {
    try { Assert-KitPublicPlay $pair[0] $pair[1]; throw 'Unsafe internet route passed.' }
    catch {
        if ($_.Exception.Message -ne 'Internet play requires an online physical laptop run.') { throw }
    }
}
Assert-KitPublicPlay $true $false
$originalTrace = $env:COD2_FRAME_TRACE_FILE
try {
    $env:COD2_FRAME_TRACE_FILE = 'must-not-leak.bin'
    if ((New-KitPublicStartInfo 'cod2_win32.exe').EnvironmentVariables.ContainsKey('COD2_FRAME_TRACE_FILE')) {
        throw 'The public menu-only engine start inherited a frame trace.'
    }
} finally { $env:COD2_FRAME_TRACE_FILE = $originalTrace }
if ((Get-KitPublicAnsweredCount '1 server answered.') -ne 1 -or
    (Get-KitPublicAnsweredCount '2 servers answered. Updated just now.') -ne 2 -or
    (Get-KitPublicAnsweredCount ("246 servers " + [char]0x00b7 + ' just now')) -ne 246 -or
    $null -ne (Get-KitPublicAnsweredCount '83 of 252 answered') -or
    $null -ne (Get-KitPublicAnsweredCount 'Refresh stopped. 2 servers answered.')) {
    throw 'The public refresh must use the launcher completion text.'
}
$rows = @(
    [pscustomobject]@{Players=0; Slots=10; Title='Empty, Toujane, Original 1.3'}
    [pscustomobject]@{Players=12; Slots=12; Title='Full, Toujane, Original 1.3'}
    [pscustomobject]@{Players=4; Slots=16; Title='Locked, Toujane, Original 1.3, needs a password'}
    [pscustomobject]@{Players=4; Slots=16; Title='Other, Toujane, CoD2x'}
    [pscustomobject]@{Players=4; Slots=16; Title='Stock, Toujane, Original 1.3'}
)
$candidates = @(Get-KitJoinCandidates $rows 'original')
if ($candidates.Count -ne 1 -or $candidates[0].Title -notlike 'Stock,*') { throw 'Public candidate restrictions failed.' }
$refusal = 'Server disconnected: You were kicked for a version mismatch.'
if ((Find-KitPublicStop ("Server joined.`n$refusal")) -cne $refusal) { throw 'Public refusal message was not preserved.' }
foreach ($refusal in 'Server uses a different version.', 'Unpure client detected.', 'Anti-cheat required.', 'Checksum mismatch.') {
    if ((Find-KitPublicStop $refusal) -cne $refusal) { throw "Missed a public refusal: $refusal" }
}
$root = Join-Path ([IO.Path]::GetTempPath()) ('kit-tests-' + [guid]::NewGuid().ToString('N'))
try {
    $KitProfile = $root
    . (Join-Path $PSScriptRoot 'Laptop-Steps.ps1')
    if (@($Steps.Id | Sort-Object -Unique).Count -ne $Steps.Count) { throw 'Laptop step IDs must be unique.' }
    foreach ($edition in 'original','cod2x') {
        foreach ($renderer in 'dx9','dx7') {
            $repeats = @($Steps | Where-Object {
                $_.Id -like 'match-*-fullscreen*' -and $_.Edition -eq $edition -and $_.Renderer -eq $renderer
            })
            if ($repeats.Count -ne 3) { throw "Expected three $edition $renderer exclusive matches." }
        }
    }
    foreach ($id in 'launcher-jump-pointer','resolution-original','carentan-direct-2','carentan-direct-3',
        'carentan-installed-2','carentan-installed-3','collect-dumps') {
        if ($id -notin $Steps.Id) { throw "Laptop check is missing $id." }
    }
    $main = New-Folder (Join-Path $root 'main')
    [IO.File]::WriteAllText((Join-Path $main 'iw_00.iwd'), 'fixture archive')
    [IO.File]::WriteAllText((Join-Path $main 'config_mp.cfg'), 'synthetic retail profile marker')
    $inventory = @(Get-KitArchiveInventory $root)
    if ($inventory.Count -ne 1 -or $inventory[0] -notmatch 'main[/\\]iw_00\.iwd$') {
        throw 'The inventory must contain only archive names and sizes.'
    }
    if ($env:OS -eq 'Windows_NT' -and $root -match '^[a-zA-Z]:\\') {
        foreach ($index in 1..15) {
            [IO.File]::WriteAllText((Join-Path $main ('iw_{0:00}.iwd' -f $index)), 'fixture archive')
        }
        if ((Get-DataRoot ('\\?\' + $root)) -ne (Get-DataRoot $root)) {
            throw 'Engine discovery must return an ordinary Windows game path.'
        }
    }
    $timing = @(
        '{"serverAddress":"127.0.0.1:28960","endedAt":"2026-11-23T14:00:00Z","frameCount":120}',
        '{"serverAddress":"127.0.0.1:28961","endedAt":"2026-11-23T14:05:00Z","frameCount":120}',
        '{"serverAddress":"127.0.0.1:28960","endedAt":"2026-11-23T14:05:00Z","frameCount":120,"capFPS":250,"frameTimeP50MS":24,"unrelated":"do not retain"}'
    )
    $stats = New-Folder (Join-Path $main 'stats')
    $timing | Set-Content -LiteralPath (Join-Path $stats 'matches.jsonl') -Encoding ASCII
    $record = @(Get-KitPublicMatchTiming $root ([DateTimeOffset]'2026-11-23T14:01:00Z') @('127.0.0.1:28960'))
    if ($record.Count -ne 1 -or $record[0].frameTimeP50MS -ne 24 -or $record[0].capFPS -ne 250 -or
        $record[0].PSObject.Properties['unrelated']) {
        throw 'Public match timing included an unrelated field or record.'
    }
} finally { Remove-KitScratch $root }
'Laptop kit safety checks passed.'

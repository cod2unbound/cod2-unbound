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
$root = Join-Path ([IO.Path]::GetTempPath()) ('kit-tests-' + [guid]::NewGuid().ToString('N'))
try {
    $main = New-Folder (Join-Path $root 'main')
    [IO.File]::WriteAllText((Join-Path $main 'iw_00.iwd'), 'fixture archive')
    [IO.File]::WriteAllText((Join-Path $main 'config_mp.cfg'), 'synthetic retail profile marker')
    $inventory = @(Get-KitArchiveInventory $root)
    if ($inventory.Count -ne 1 -or $inventory[0] -notmatch 'main[/\\]iw_00\.iwd$') {
        throw 'The inventory must contain only archive names and sizes.'
    }
} finally { Remove-KitScratch $root }
'Laptop kit safety checks passed.'

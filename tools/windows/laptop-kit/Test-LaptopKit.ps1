#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
$Mode = 'Status'
. (Join-Path $PSScriptRoot 'Common.ps1')
. (Join-Path $PSScriptRoot 'Check-Installed.ps1')
. (Join-Path $PSScriptRoot 'Check-Machine.ps1')

# The shared action entry point must reject these before it even queries UI Automation.
foreach ($name in @('Use key from disc install' + [char]0x2026; 'Import from the installed game';
    'Import from this install'; 'ImportInstall'; 'UseDiscKey')) {
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

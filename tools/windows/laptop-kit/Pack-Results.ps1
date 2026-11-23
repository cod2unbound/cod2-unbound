#Requires -Version 5.1
[CmdletBinding()]
param([ValidatePattern('^[a-zA-Z0-9_-]{1,48}$')][string]$RunId)
. (Join-Path $PSScriptRoot 'Common.ps1')
$results = Join-Path $Kit 'results'
if ($RunId) { $results = Join-Path $results $RunId }
if (-not (Test-Path -LiteralPath $results)) { throw 'There are no results to pack.' }
$files = @(Get-ChildItem -LiteralPath $results -Force -Recurse)
if (-not $files) { throw 'There are no results to pack.' }
foreach ($item in $files) {
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint -or $item.Extension -in '.dmp','.exe','.dll','.iwd') {
        throw 'Remove links, programs, game archives and raw memory dumps from results.'
    }
}
$files = @($files | Where-Object { -not $_.PSIsContainer })
$script:KitSerials = Get-KitSerials
$stage = Join-Path $LocalData ('CoD2-Kit-Pack-' + [guid]::NewGuid().ToString('N'))
try {
    New-Folder $stage | Out-Null
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($results.Length + 1)
        $destination = Join-Path $stage $relative
        New-Folder (Split-Path $destination -Parent) | Out-Null
        if ($file.Extension -in '.cfg','.csv','.json','.jsonl','.log','.tsv','.txt','.xml') {
            Save-Text $file.FullName $destination
        } elseif ($file.Extension -in '.bin','.dm_1','.jpg','.jpeg','.png','.tga','.wav') {
            Copy-Item -LiteralPath $file.FullName -Destination $destination
        } else {
            throw "The results contain an unexpected file type. Review $relative before packing."
        }
        Write-Host $relative
    }
    Write-Host 'Review the listed pictures, demos and sound clips for anything personal.'
    if ((Read-Host 'Type PACK to make the ZIP') -cne 'PACK') { return }
    [AppContext]::SetSwitch('Switch.System.IO.Compression.ZipFile.UseBackslash', $false)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = Join-Path $Kit ('CoD2-Unbound-results-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.zip')
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip)
    Write-Host "Results ZIP $zip"
} finally {
    if (Test-Path -LiteralPath $stage) { Remove-KitScratch $stage }
}

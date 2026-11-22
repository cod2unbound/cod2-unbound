#Requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$results = Join-Path $PSScriptRoot 'results'
if (-not (Get-ChildItem -LiteralPath $results -Force)) { throw 'There are no results to pack.' }
foreach ($item in Get-ChildItem -LiteralPath $results -Recurse -Force) {
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint -or $item.Extension -in '.dmp','.exe','.dll','.iwd') {
        throw 'Remove copied programs, memory dumps, game archives and links from results before packing.'
    }
}
if ((Read-Host 'Review the results for private information. Type PACK to make the ZIP') -cne 'PACK') { return }
# Windows PowerShell targets older .NET behavior for ZIP entry names.
[AppContext]::SetSwitch('Switch.System.IO.Compression.ZipFile.UseBackslash', $false)
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = Join-Path $PSScriptRoot ('CoD2-Unbound-results-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.zip')
[IO.Compression.ZipFile]::CreateFromDirectory($results, $zip)
Write-Host "Bring back $zip."

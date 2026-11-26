function Save-KitEvents {
    $start = [DateTime]::Parse((Get-Content -LiteralPath $RunStart -Raw)).ToLocalTime()
    $match = [regex]::Escape($Kit + '\') + '|' + [regex]::Escape($InstallRoot + '\')
    $alerts = @()
    foreach ($logName in 'Application','Microsoft-Windows-Windows Defender/Operational') {
        $filter = @{ LogName=$logName; StartTime=$start }
        $name = if ($logName -eq 'Application') { 'application' } else { 'defender' }
        try {
            $events = @(Get-WinEvent -FilterHashtable $filter -ErrorAction Stop | Where-Object {
                $_.ToXml() -match $match
            } | ForEach-Object {
                [pscustomobject]@{ Id=$_.Id; Time=$_.TimeCreated.ToUniversalTime().ToString('o')
                    Provider=$_.ProviderName }
            })
        } catch {
            $events = @()
            if ($_.FullyQualifiedErrorId -notlike 'NoMatchingEventsFound*') {
                if ($_.Exception -isnot [UnauthorizedAccessException] -and
                    $_.CategoryInfo.Category -notin 'PermissionDenied','SecurityError') { throw }
                $alerts += $name
            }
        }
        ConvertTo-Json -InputObject $events -Depth 3 | Set-Content -LiteralPath (Join-Path $Results "$name.json") -Encoding UTF8
    }

    if ($alerts.Count) {
        throw [NotSupportedException]::new("Windows refused access to event logs: $($alerts -join ', ').")
    }
}

function Invoke-KitShaderTable {
    Stop-KitIfNoGame
    $entry = New-RunContext 'compiler'
    $probe = Join-Path $Kit 'probes\media-probe.exe'
    Stop-KitIfMissing $probe 'The shader and audio tool is missing from this kit.'
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    # Later IWDs take precedence for duplicate material paths. The licensed
    # shader sources are extracted into scratch and stay on the Windows PC.
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $requests = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $pattern = '(?i)(vertexShader|pixelShader)\s+(\d+)\.(\d+)\s+"([A-Za-z0-9_][A-Za-z0-9_.-]*)"'
    $prefix = 'materials/shaders/'
    foreach ($iwd in Get-ChildItem -LiteralPath (Join-Path $GamePath 'main') -Filter '*.iwd' -File | Sort-Object Name -Descending) {
        $archive = [IO.Compression.ZipFile]::OpenRead($iwd.FullName)
        try {
            foreach ($item in $archive.Entries) {
                $name = $item.FullName
                if ($name -notmatch '^materials/(shaders|techniques)/(?:[A-Za-z0-9_.-]+/)*[A-Za-z0-9_.-]+$' -or
                    $name -match '(^|/)\.{1,2}(/|$)' -or -not $seen.Add($name)) { continue }
                if ($name.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
                    $destination = Join-Path $entry.Folder $name.Substring($prefix.Length)
                    New-Folder (Split-Path $destination -Parent) | Out-Null
                    [IO.Compression.ZipFileExtensions]::ExtractToFile($item, $destination)
                } else {
                    $reader = [IO.StreamReader]::new($item.Open())
                    try { $source = $reader.ReadToEnd() } finally { $reader.Dispose() }
                    foreach ($match in [regex]::Matches($source, $pattern)) {
                        $stage = if ($match.Groups[1].Value -ieq 'vertexShader') { 'vs' } else { 'ps' }
                        $major = [int]$match.Groups[2].Value
                        $minor = [int]$match.Groups[3].Value
                        if ($major -lt 2) { $major = 2; $minor = 0 }
                        [void]$requests.Add(("{0}`t{1}_main`t{1}_{2}_{3}" -f $match.Groups[4].Value,$stage,$major,$minor))
                    }
                }
            }
        } finally { $archive.Dispose() }
    }
    if (-not $requests.Count) { throw 'No shader requests were found in the game files.' }
    $requests | Sort-Object | Set-Content -LiteralPath (Join-Path $entry.Folder 'shader-requests.tsv') -Encoding Ascii
    $run = Start-KitProcess $probe 'shaders' $entry.Folder -NoWindow
    $done = Complete-KitProcess $run $entry.Evidence 360
    Save-Text (Join-Path $entry.Folder 'shader-results.tsv') (Join-Path $entry.Evidence 'shader-results.tsv')
    if ($done.TimedOut -or $done.ExitCode -ne 0) { throw 'Shader compilation failed or reached its time limit. See the compiler table.' }
}

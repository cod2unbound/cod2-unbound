#Requires -Version 5.1
param([string]$Setup, [string]$Ready, [ValidatePattern('^0\.5\.[1-9][0-9]{4}$')][string]$NoticeVersion)
$ErrorActionPreference = 'Stop'
$version = [IO.Path]::GetFileName($Setup).Split('-')[2]
$name = [IO.Path]::GetFileName($Setup)
if ($NoticeVersion) {
    $name = $name.Replace("-$version-", "-$NoticeVersion-")
    $version = $NoticeVersion
}
$length = (Get-Item -LiteralPath $Setup).Length
$checksum = [Text.Encoding]::ASCII.GetBytes("$((Get-FileHash -LiteralPath $Setup).Hash.ToLowerInvariant())  $name`n")
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
$origin = "http://127.0.0.1:$port"
$asset = "/releases/download/v$version/$name"
$sums = "/releases/download/v$version/SHA256SUMS"
$release = ConvertTo-Json -InputObject @(@{
    tag_name="v$version"; name="Version $version"; body='Offline update check.'
    assets=@(@{name=$name; size=$length; browser_download_url="$origin$asset"},
        @{name='SHA256SUMS'; size=$checksum.Length; browser_download_url="$origin$sums"})
}) -Depth 5 -Compress
[IO.File]::WriteAllText($Ready, [string]$port)

function Send-Header([int]$Status, [long]$Length) {
    $header = [Text.Encoding]::ASCII.GetBytes("HTTP/1.1 $Status Result`r`nContent-Length: $Length`r`nConnection: close`r`n`r`n")
    $stream.Write($header, 0, $header.Length)
}

function Send-Bytes([byte[]]$Bytes) {
    Send-Header 200 $Bytes.Length
    $stream.Write($Bytes, 0, $Bytes.Length)
}

$deadline = [DateTime]::UtcNow.AddMinutes(6)
try {
    while ([DateTime]::UtcNow -lt $deadline) {
        if (-not $listener.Pending()) { Start-Sleep -Milliseconds 150; continue }
        $client = $listener.AcceptTcpClient()
        try {
            $client.ReceiveTimeout = 6000
            $client.SendTimeout = 15000
            $stream = $client.GetStream()
            $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::ASCII)
            try {
                if ($reader.ReadLine() -notmatch '^GET ([^ ]+) HTTP/1\.[01]$') { Send-Header 400 0; continue }
                $target = $Matches[1]
                for ($n = 0; $n -lt 80; $n++) { if (-not $reader.ReadLine()) { break } }
                if ($n -eq 80) { Send-Header 400 0; continue }
                switch ($target) {
                    '/releases' { Write-Output 'Release list'; Send-Bytes ([Text.Encoding]::UTF8.GetBytes($release)) }
                    $sums { Write-Output 'Installer checksum'; Send-Bytes $checksum }
                    $asset {
                        if ($NoticeVersion) { Send-Header 404 0; continue }
                        Write-Output 'Installer'
                        Send-Header 200 $length
                        $file = [IO.File]::OpenRead($Setup)
                        try { $file.CopyTo($stream) } finally { $file.Dispose() }
                    }
                    default { Send-Header 404 0 }
                }
            } finally { $reader.Dispose() }
        } catch [IO.IOException] { Write-Output 'The local connection closed.' }
        finally { $client.Dispose() }
    }
} finally { $listener.Stop() }

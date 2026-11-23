#Requires -Version 5.1
param([string]$Ready)
$ErrorActionPreference = 'Stop'
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
[IO.File]::WriteAllText($Ready, [string]$listener.LocalEndpoint.Port)
$deadline = [DateTime]::UtcNow.AddMinutes(4)
try {
    while ([DateTime]::UtcNow -lt $deadline) {
        if (-not $listener.Pending()) { Start-Sleep -Milliseconds 125; continue }
        $client = $listener.AcceptTcpClient()
        try {
            $client.ReceiveTimeout = 5000
            $client.SendTimeout = 5000
            $stream = $client.GetStream()
            $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::ASCII)
            try {
                $method = $reader.ReadLine()
                $length = 0
                for ($n = 0; $n -lt 64; $n++) {
                    $header = $reader.ReadLine()
                    if (-not $header) { break }
                    if ($header -match '^Content-Length: (\d{1,7})$') { $length = [int]$Matches[1] }
                }
                if ($method -match '^(POST|GET) ') { "$($Matches[1]) loopback, declared body size $length bytes" }
                $response = [Text.Encoding]::ASCII.GetBytes("HTTP/1.1 200 OK`r`nContent-Length: 2`r`nConnection: close`r`n`r`n{}")
                $stream.Write($response, 0, $response.Length)
            } finally { $reader.Dispose() }
        } catch [IO.IOException] { 'The local report connection closed early.' }
        finally { $client.Dispose() }
    }
} finally { $listener.Stop() }

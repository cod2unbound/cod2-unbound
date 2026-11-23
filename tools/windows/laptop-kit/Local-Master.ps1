#Requires -Version 5.1
param([int]$ServerPort, [string]$Ready)
$ErrorActionPreference = 'Stop'
$loop = [Net.IPAddress]::Loopback
$socket = [Net.Sockets.UdpClient]::new([Net.IPEndPoint]::new($loop, 0))
$socket.Client.ReceiveTimeout = 500
$peer = [Net.IPEndPoint]::new($loop, 0)
[byte[]]$packet = @(255,255,255,255) + [Text.Encoding]::ASCII.GetBytes('getserversResponse') +
    [byte[]](92,127,0,0,1,($ServerPort -shr 8),($ServerPort -band 255),92,69,79,84,0,0,0)
[IO.File]::WriteAllText($Ready, [string]$socket.Client.LocalEndPoint.Port)
try {
    $deadline = [DateTime]::UtcNow.AddMinutes(3)
    while ([DateTime]::UtcNow -lt $deadline) {
        try { $request = $socket.Receive([ref]$peer) }
        catch {
            if ($_.Exception.GetBaseException() -is [Net.Sockets.SocketException]) { continue }
            throw
        }
        if ($request.Length -gt 4 -and [Text.Encoding]::ASCII.GetString($request, 4, $request.Length - 4).StartsWith('getservers')) {
            [void]$socket.Send($packet, $packet.Length, $peer)
            'Listed one local server.'
        }
    }
} finally { $socket.Dispose() }

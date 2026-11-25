#Requires -Version 5.1
param([Parameter(Mandatory=$true)][string]$Stage)
$ErrorActionPreference='Stop'
trap { ($_ | Out-String) | Set-Content -LiteralPath (Join-Path $Stage 'master-error.txt'); exit 1 }
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Diagnostics;
public static class RefreshMaster {
    public static void Run(string stage) {
        const int count = 252;
        var sockets = new List<Socket>();
        var indices = new Dictionary<Socket, int>();
        var marker = new byte[]{255,255,255,255};
        var buffer = new byte[8192];
        var clock = Stopwatch.StartNew();
        int masterReplies = 0, serverReplies = 0;
        try {
            for (int index = 0; index <= count; index++) {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                sockets.Add(socket);
                socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                indices.Add(socket, index);
            }
            var masterPacket = new List<byte>(marker.Concat(Encoding.ASCII.GetBytes("getserversResponse")));
            for (int index = 0; index < count; index++) {
                int port = ((IPEndPoint)sockets[index].LocalEndPoint).Port;
                masterPacket.AddRange(new byte[]{92,127,0,0,1,(byte)(port >> 8),(byte)port});
            }
            masterPacket.AddRange(new byte[]{92,69,79,84,0,0,0});
            var listing = masterPacket.ToArray();
            File.WriteAllText(Path.Combine(stage,"master-port.txt"), ((IPEndPoint)sockets[count].LocalEndPoint).Port.ToString());
            while (clock.Elapsed.TotalMinutes < 12 && !File.Exists(Path.Combine(stage,"master-stop.txt"))) {
                // Winsock's select sets hold up to 64 sockets, so poll each small set.
                for (int batch = 0; batch < sockets.Count; batch += 60) {
                    var ready = sockets.Skip(batch).Take(60).ToList();
                    Socket.Select(ready, null, null, 1000);
                    foreach (var socket in ready) {
                        EndPoint peer = new IPEndPoint(IPAddress.Loopback, 0);
                        int used;
                        try { used = socket.ReceiveFrom(buffer, ref peer); }
                        catch (SocketException error) {
                            if (error.SocketErrorCode == SocketError.ConnectionReset) continue;
                            throw;
                        }
                        if (used < 4 || !marker.SequenceEqual(buffer.Take(4))) continue;
                        string request = Encoding.ASCII.GetString(buffer, 4, used - 4);
                        int index = indices[socket];
                        if (index == count && request.StartsWith("getservers ")) {
                            socket.SendTo(listing, peer);
                            masterReplies++;
                            continue;
                        }
                        if (index == count || index >= 78 && index < 84) continue;
                        bool status = request.StartsWith("getstatus ");
                        if (!status && !request.StartsWith("getinfo ")) continue;
                        string challenge = request.Split(' ')[1];
                        string header = status ? "statusResponse" : "infoResponse";
                        string fields = String.Format(@"\challenge\{0}\hostname\^2Fixture {1:D3}\mapname\mp_toujane\gametype\dm\shortversion\{2}\sv_maxclients\16\clients\1",
                            challenge, index, index % 2 == 0 ? "1.3" : "1.4.6.8");
                        string roster = status ? "12 30 \"Fixture Player\"\n" : "";
                        var reply = marker.Concat(Encoding.ASCII.GetBytes(header + "\n" + fields + "\n" + roster)).ToArray();
                        socket.SendTo(reply, peer);
                        serverReplies++;
                    }
                }
            }
        } finally {
            foreach (var socket in sockets) socket.Dispose();
            File.WriteAllText(Path.Combine(stage,"master-receipt.txt"), "Listed 252 addresses; 6 silent. Master replies " + masterReplies + ", status/info replies " + serverReplies + ".");
        }
    }
}
'@
[RefreshMaster]::Run($Stage)

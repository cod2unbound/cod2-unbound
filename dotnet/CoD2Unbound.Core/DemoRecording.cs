using System.Buffers.Binary;
using System.Text;

namespace CoD2Unbound.Core;

public static class DemoRecording
{
    private const int MaximumPacket = 0x20000;
    private const int ReliableAckBytes = 4;

    public static GameEdition? ReadEdition(Stream recording) => Inspect(recording, readDuration: false).Edition;

    public static TimeSpan? ReadDuration(Stream recording, CancellationToken cancellationToken = default) =>
        Inspect(recording, readDuration: true, cancellationToken).Duration;

    // CL_Record_f writes a compressed game state first; CL_WriteDemoMessage appends
    // sequence/length/payload records and CL_StopRecord_f writes two -1 integers.
    private static (GameEdition? Edition, TimeSpan? Duration) Inspect(Stream recording, bool readDuration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Span<byte> header = stackalloc byte[8];
        if (recording.ReadAtLeast(header, header.Length, false) != header.Length ||
            !ValidLength(header, recording, out int length))
            return default;

        var packet = new byte[readDuration ? MaximumPacket : length];
        var decoded = new byte[MaximumPacket];
        if (recording.ReadAtLeast(packet.AsSpan(0, length), length, false) != length ||
            GameStateEdition(packet.AsSpan(ReliableAckBytes, length - ReliableAckBytes), decoded) is not { } edition)
            return default;

        int? previous = null;
        long elapsed = 0;
        while (recording.ReadAtLeast(header, header.Length, false) == header.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BinaryPrimitives.ReadInt32LittleEndian(header[4..]) == -1)
                return BinaryPrimitives.ReadInt32LittleEndian(header) == -1 && recording.Position == recording.Length
                    ? (edition, elapsed > 0 ? TimeSpan.FromMilliseconds(elapsed) : null) : default;
            if (!ValidLength(header, recording, out length))
                return default;
            if (!readDuration)
            {
                recording.Seek(length, SeekOrigin.Current);
                continue;
            }
            if (recording.ReadAtLeast(packet.AsSpan(0, length), length, false) != length)
                return default;
            if (SnapshotTime(packet.AsSpan(ReliableAckBytes, length - ReliableAckBytes), decoded) is not { } time)
                continue;
            if (previous is { } last)
            {
                // Server time is a 32-bit millisecond clock. A forward wrap still produces a positive delta.
                int delta = unchecked(time - last);
                if (delta < 0)
                    return default;
                elapsed += delta;
                if (elapsed > TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond)
                    return default;
            }
            previous = time;
        }
        return default;
    }

    private static bool ValidLength(ReadOnlySpan<byte> header, Stream recording, out int length)
    {
        length = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        return length is > ReliableAckBytes and <= MaximumPacket && length <= recording.Length - recording.Position;
    }

    private static int? SnapshotTime(ReadOnlySpan<byte> compressed, Span<byte> buffer)
    {
        const byte nop = 0, serverCommand = 4, snapshot = 6;
        const int snapshotHeader = 7; // opcode, server time, delta number and flags
        var message = buffer[..DemoHuffman.Decode(compressed, buffer[..snapshotHeader])];
        // Most packets start with the snapshot. Only preceding commands need further decoding.
        if (!message.IsEmpty && message[0] is nop or serverCommand)
            message = buffer[..DemoHuffman.Decode(compressed, buffer)];
        while (!message.IsEmpty)
        {
            switch (message[0])
            {
                case snapshot:
                    return message.Length >= snapshotHeader ? BinaryPrimitives.ReadInt32LittleEndian(message[1..]) : null;
                case nop:
                    message = message[1..];
                    break;
                case serverCommand when message.Length >= 6:
                    int terminator = message[5..].IndexOf((byte)0); // skip opcode and command sequence
                    if (terminator < 0)
                        return null;
                    message = message[(6 + terminator)..];
                    break;
                default:
                    return null;
            }
        }
        return null;
    }

    private static GameEdition? GameStateEdition(ReadOnlySpan<byte> compressed, Span<byte> buffer)
    {
        var state = buffer[..DemoHuffman.Decode(compressed, buffer)];
        const byte gameState = 1, configString = 2, baseline = 3, end = 7;
        if (state.Length < 6 || state[0] != gameState)
            return null;

        // Configstrings precede entity baselines in a recording's initial game state.
        var info = new Dictionary<string, string>(StringComparer.Ordinal);
        state = state[5..]; // command and server-command sequence
        while (!state.IsEmpty && state[0] == configString)
        {
            if (state.Length < 4)
                return null;
            int index = BinaryPrimitives.ReadUInt16LittleEndian(state[1..]);
            int endOfString = state[3..].IndexOf((byte)0);
            if (index >= 2048 || endOfString is < 0 or >= 8192)
                return null;
            if ((index is 0 or 1) && !ReadInfo(state.Slice(3, endOfString), info))
                return null;
            state = state[(4 + endOfString)..];
        }

        if (state.IsEmpty || state[0] is not (baseline or end) ||
            (state[0] == end && (state.Length < 10 || state[9] != end)))
            return null;
        string? protocol = info.GetValueOrDefault("protocol");
        if (protocol is not (null or "118" or "120") ||
            !info.ContainsKey("mapname") && !info.ContainsKey("sv_serverid"))
            return null;

        return protocol == "120" || WireParser.Integer(info.GetValueOrDefault("g_cod2x", "")) > 0 ||
            info.GetValueOrDefault("shortversion")?.StartsWith("1.4", StringComparison.Ordinal) == true
            ? GameEdition.Cod2x : GameEdition.Original;
    }

    private static bool ReadInfo(ReadOnlySpan<byte> bytes, Dictionary<string, string> info)
    {
        string[] pairs = Encoding.Latin1.GetString(bytes).Split('\\');
        if (pairs[0].Length != 0 || pairs.Length % 2 == 0)
            return false;
        for (int i = 1; i < pairs.Length; i += 2)
        {
            string key = pairs[i], value = pairs[i + 1];
            if (key is not ("protocol" or "mapname" or "sv_serverid" or "g_cod2x" or "shortversion"))
                continue;
            if (!info.TryAdd(key, value) && info[key] != value)
                return false;
        }
        return true;
    }
}

using System.Buffers.Binary;
using System.Text;

namespace CoD2Unbound.Core;

public static class DemoRecording
{
    private const int MaximumPacket = 0x20000;
    private const int ReliableAckBytes = 4;

    // CL_Record_f writes a compressed game state first; CL_WriteDemoMessage appends
    // sequence/length/payload records and CL_StopRecord_f writes two -1 integers.
    public static GameEdition? ReadEdition(Stream recording)
    {
        Span<byte> header = stackalloc byte[8];
        if (recording.ReadAtLeast(header, header.Length, false) != header.Length ||
            !ValidLength(header, recording, out int length))
            return null;

        var first = new byte[length];
        if (recording.ReadAtLeast(first, first.Length, false) != first.Length ||
            GameStateEdition(first.AsSpan(ReliableAckBytes)) is not { } edition)
            return null;

        while (recording.ReadAtLeast(header, header.Length, false) == header.Length)
        {
            if (BinaryPrimitives.ReadInt32LittleEndian(header[4..]) == -1)
                return BinaryPrimitives.ReadInt32LittleEndian(header) == -1 && recording.Position == recording.Length
                    ? edition : null;
            if (!ValidLength(header, recording, out length))
                return null;
            recording.Seek(length, SeekOrigin.Current);
        }
        return null;
    }

    private static bool ValidLength(ReadOnlySpan<byte> header, Stream recording, out int length)
    {
        length = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        return length is > ReliableAckBytes and <= MaximumPacket && length <= recording.Length - recording.Position;
    }

    private static GameEdition? GameStateEdition(ReadOnlySpan<byte> compressed)
    {
        var buffer = new byte[MaximumPacket];
        var state = buffer.AsSpan(0, DemoHuffman.Decode(compressed, buffer));
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

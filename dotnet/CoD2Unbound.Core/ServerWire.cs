using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace CoD2Unbound.Core;

public sealed record ColorRun(string Text, int Code);

public static class QuakeColors
{
    public static ImmutableArray<ColorRun> Runs(string name)
    {
        var runs = ImmutableArray.CreateBuilder<ColorRun>();
        var text = new StringBuilder();
        int color = 7;
        var elements = StringInfo.GetTextElementEnumerator(name);
        bool caret = false;
        for (int count = 0; count < 1024 && elements.MoveNext(); count++)
        {
            string element = elements.GetTextElement();
            if (caret)
            {
                double digit = Rune.TryGetRuneAt(element, 0, out Rune rune) && rune.Utf16SequenceLength == element.Length
                    ? Rune.GetNumericValue(rune) : -1;
                if (digit is >= 0 and < 10 && digit == Math.Truncate(digit))
                {
                    if (text.Length > 0)
                        runs.Add(new(text.ToString(), color));
                    text.Clear();
                    color = (int)digit;
                    caret = false;
                    continue;
                }
                text.Append('^');
            }
            caret = element == "^";
            if (!caret)
                text.Append(element);
        }
        if (caret)
            text.Append('^');
        if (text.Length > 0)
            runs.Add(new(text.ToString(), color));
        return runs.ToImmutable();
    }

    public static string Plain(string name) => string.Concat(Runs(name).Select(run => run.Text));
}

public sealed record ServerPlayer(int Id, long Score, long Ping, string Name);

public sealed record GameServer(string Address, ImmutableDictionary<string, string> Fields, int Ping,
    ImmutableArray<ServerPlayer> Players, DateTimeOffset? ObservedAt = null, ImmutableArray<string> QueryConflicts = default)
{
    public string Name => Field("sv_hostname") ?? Field("hostname") ?? Address;
    public string Map => Field("mapname") ?? "Unknown";
    public string GameType => Field("g_gametype") ?? Field("gametype") ?? "—";
    public long MaxPlayers => WireParser.Integer(Field("sv_maxclients") ?? Field("sv_maxClients") ?? "") ?? 0;
    public long PlayerCount => WireParser.Integer(Field("clients") ?? "") ?? Players.Length;
    public string Version => Field("shortversion") ?? (Field("protocol") == "120" ? "1.4.x" : "1.3");
    public string Mod => Field("fs_game") ?? "";
    public bool PasswordRequired => Field("pswrd") == "1" || Field("g_password") == "1";
    public bool IsCod2x => Version.StartsWith("1.4", StringComparison.Ordinal) || Field("protocol") == "120";

    private string? Field(string name) => Fields.GetValueOrDefault(name);

    public GameServer MergeInfo(GameServer info)
    {
        var conflicts = (QueryConflicts.IsDefault ? [] : QueryConflicts).ToHashSet(StringComparer.Ordinal);
        var fields = Fields.ToBuilder();
        foreach (var (key, value) in info.Fields)
        {
            string name = key.ToLowerInvariant();
            if (fields.Any(previous => previous.Key.ToLowerInvariant() == name && previous.Value != value))
                conflicts.Add(name);
            fields[key] = value;
        }
        return this with
        {
            Fields = fields.ToImmutable(),
            QueryConflicts = conflicts.Count == 0 ? default : [.. conflicts.Order(StringComparer.Ordinal)]
        };
    }
}

public enum WireResponseFailure { InvalidPacket, InvalidHeader, InvalidFields, OversizedField, ConflictingFields }

public sealed class WireResponseException(WireResponseFailure failure) : Exception(failure switch
{
    WireResponseFailure.InvalidPacket => "Invalid server response.",
    WireResponseFailure.InvalidHeader => "Invalid server response header.",
    WireResponseFailure.InvalidFields => "Invalid server info fields.",
    WireResponseFailure.OversizedField => "Oversized server field.",
    _ => "Conflicting server info fields."
})
{
    public WireResponseFailure Failure { get; } = failure;
}

public static class WireParser
{
    public const int MaxPacketBytes = 65535;
    private static readonly byte[] MarkerBytes = [255, 255, 255, 255];
    public static ReadOnlySpan<byte> Marker => MarkerBytes;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    public static ImmutableArray<string> Master(ReadOnlySpan<byte> packet)
    {
        var prefix = "getserversResponse"u8;
        if (packet.Length > MaxPacketBytes || !packet.StartsWith(Marker) || !packet[Marker.Length..].StartsWith(prefix))
            return [];

        int index = Marker.Length + prefix.Length;
        while (index < packet.Length && packet[index] is 0 or 10 or 13)
            index++;
        var addresses = new SortedSet<string>(StringComparer.Ordinal);
        for (; index + 7 <= packet.Length && packet[index] == '\\'; index += 7)
        {
            if (packet[(index + 1)..(index + 4)].SequenceEqual("EOT"u8))
                break;
            int port = (packet[index + 5] << 8) + packet[index + 6];
            if (port > 0 && packet[index + 1] is > 0 and < 224)
                addresses.Add(FormattableString.Invariant($"{packet[index + 1]}.{packet[index + 2]}.{packet[index + 3]}.{packet[index + 4]}:{port}"));
        }
        return [.. addresses];
    }

    public static GameServer Status(ReadOnlySpan<byte> packet, string address, int ping, DateTimeOffset observedAt)
    {
        if (packet.Length > MaxPacketBytes || !packet.StartsWith(Marker))
            throw new WireResponseException(WireResponseFailure.InvalidPacket);

        string text;
        var payload = packet[Marker.Length..];
        try { text = StrictUtf8.GetString(payload); }
        catch (DecoderFallbackException) { text = Encoding.Latin1.GetString(payload); }
        var lines = text.Split('\n');
        string header = lines[0].Trim();
        if (lines.Length < 2 || header is not ("statusResponse" or "infoResponse"))
            throw new WireResponseException(WireResponseFailure.InvalidHeader);

        var tokens = lines[1].Split('\\');
        if (tokens[0].Length != 0 || tokens.Length % 2 != 1 || tokens.Length > 1025)
            throw new WireResponseException(WireResponseFailure.InvalidFields);

        var fields = ImmutableDictionary.CreateBuilder<string, string>(CanonicalStringComparer.Instance);
        for (int index = 1; index < tokens.Length; index += 2)
        {
            string name = tokens[index], value = tokens[index + 1];
            if (new StringInfo(name).LengthInTextElements > 128 || new StringInfo(value).LengthInTextElements > 2048)
                throw new WireResponseException(WireResponseFailure.OversizedField);

            // Swift String compares canonically equivalent keys and values as equal.
            if (fields.TryGetValue(name, out string? previous) && previous.Normalize() != value.Normalize())
                throw new WireResponseException(WireResponseFailure.ConflictingFields);
            fields[name] = value;
        }

        var players = ImmutableArray.CreateBuilder<ServerPlayer>();
        foreach (string line in lines.Skip(2).Take(256))
        {
            int start = line.IndexOf('"'), end = line.LastIndexOf('"');
            if (start < 0 || end <= start)
                continue;
            var numbers = line[..start].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (numbers.Length != 2 || Integer(numbers[0]) is not long score || Integer(numbers[1]) is not long latency)
                continue;
            string name = line[(start + 1)..end];
            var elements = new StringInfo(name);
            players.Add(new(players.Count, score, latency, elements.LengthInTextElements > 128 ? elements.SubstringByTextElements(0, 128) : name));
        }
        return new(address, fields.ToImmutable(), ping, players.ToImmutable(), header == "statusResponse" ? observedAt : null);
    }

    private sealed class CanonicalStringComparer : IEqualityComparer<string>
    {
        internal static readonly CanonicalStringComparer Instance = new();
        public bool Equals(string? left, string? right) => left?.Normalize() == right?.Normalize();
        public int GetHashCode(string value) => StringComparer.Ordinal.GetHashCode(value.Normalize());
    }

    internal static long? Integer(string value)
    {
        // .NET numeric parsing can accept terminal NUL characters; the Swift wire grammar does not.
        var digits = value.AsSpan();
        if (!digits.IsEmpty && digits[0] is '+' or '-')
            digits = digits[1..];
        if (digits.IsEmpty || digits.ContainsAnyExceptInRange('0', '9'))
            return null;
        return long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long number) ? number : null;
    }
}

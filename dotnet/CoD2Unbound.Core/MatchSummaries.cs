using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoD2Unbound.Core;

public sealed record MatchHitch(ulong AtMS, ulong Frame, double Msec, ImmutableDictionary<string, ImmutableArray<double>>? Renderer)
{
    public string Time => FormattableString.Invariant($"{AtMS / 60_000}:{AtMS / 1000 % 60:D2}");
}

public sealed record MatchSummary
{
    public required int SchemaVersion { get; init; }
    public required string EndedAt { get; init; }
    public required string EndReason { get; init; }
    public required string Map { get; init; }
    public required string Mode { get; init; }
    public required double DurationSeconds { get; init; }
    public required string ServerAddress { get; init; }
    public required ImmutableDictionary<string, string> RuleHints { get; init; }
    public required ulong FrameCount { get; init; }
    public double? FrameTimeP50MS { get; init; }
    public double? FrameTimeP99MS { get; init; }
    public double? FrameTimeMaxMS { get; init; }
    public double? FrameTimeHistogramLimitMS { get; init; }
    public ulong? FramesUnderCap { get; init; }
    public required ulong CapSampleFrames { get; init; }
    public required int CapFPS { get; init; }
    public double? PingAverageMS { get; init; }
    public double? PingMaximumMS { get; init; }
    public required ulong PingSamples { get; init; }
    public required ulong PacketsReceived { get; init; }
    public required ulong PacketsLost { get; init; }
    public double? PacketLossPercent { get; init; }
    public string? PacketLossSource { get; init; }
    public required ulong SnapshotsReceived { get; init; }
    public ulong? HitchCount { get; init; }
    public ulong? HitchesOmitted { get; init; }
    public ImmutableArray<MatchHitch>? Hitches { get; init; }
    public ImmutableDictionary<string, ImmutableArray<double>>? RendererActivity { get; init; }

    [JsonIgnore] public string Id => $"{EndedAt} {ServerAddress} {Map}";
    [JsonIgnore] public bool FrameQuantilesAreExact => FrameTimeP99MS < FrameTimeHistogramLimitMS;

    [JsonIgnore]
    public string? FreezeSummary
    {
        get
        {
            if (FrameTimeMaxMS is not > 100)
                return null;
            string duration = FreezeDuration(FrameTimeMaxMS.Value);
            if (HitchCount is not > 0)
                return $"A frame froze for {duration}. This older summary didn’t record when it happened.";
            var longest = Hitches?.FirstOrDefault(hitch => hitch.Msec == FrameTimeMaxMS);
            string when = longest is null ? "" : $" at {longest.Time}";
            return FormattableString.Invariant($"{HitchCount} {(HitchCount == 1 ? "freeze" : "freezes")} over 0.1 s, the longest {duration}{when}.");
        }
    }

    [JsonIgnore]
    public string? FreezeTimes => Hitches is { IsDefaultOrEmpty: false } hitches
        ? "Time into the match: " + string.Join(", ", hitches.Select(item => $"{item.Time} ({FreezeDuration(item.Msec)})")) + "." +
          (HitchesOmitted is > 0 ? FormattableString.Invariant($" {HitchesOmitted} more not listed here. Check the game log for details.") : "")
        : null;

    public static string FreezeDuration(double milliseconds) => (milliseconds / 1000).ToString("F2", CultureInfo.InvariantCulture) + " s";

    internal bool IsValid()
    {
        if (SchemaVersion != 1 || EndedAt is null || EndReason is null || Map is null || Mode is null ||
            ServerAddress is null || RuleHints is null || RuleHints.Values.Any(value => value is null) ||
            CapFPS is < 0 or > 1000 || CapSampleFrames > FrameCount || PingSamples > SnapshotsReceived)
            return false;
        double?[] measured = [DurationSeconds, FrameTimeP50MS, FrameTimeP99MS, FrameTimeMaxMS,
            FrameTimeHistogramLimitMS, PingAverageMS, PingMaximumMS, PacketLossPercent];
        if (measured.Any(value => value is double number && (!double.IsFinite(number) || number < 0)) ||
            DurationSeconds > (double)ulong.MaxValue / 1000 || FrameTimeHistogramLimitMS is <= 0 or > int.MaxValue ||
            !ValidActivity(RendererActivity))
            return false;
        if (Hitches is { } hitches &&
            (hitches.Length > 8 || hitches.Any(item => item is null || !double.IsFinite(item.Msec) ||
                item.Msec is <= 100 or > int.MaxValue || item.Frame > FrameCount || !ValidActivity(item.Renderer)) ||
             (HitchCount is ulong count && (count < (ulong)hitches.Length || count > FrameCount ||
                 (HitchesOmitted.HasValue && HitchesOmitted != count - (ulong)hitches.Length)))))
            return false;

        if (FrameCount == 0 ? FrameTimeP50MS.HasValue || FrameTimeP99MS.HasValue || FrameTimeMaxMS.HasValue :
            !(FrameTimeP50MS <= FrameTimeP99MS && FrameTimeP99MS <= FrameTimeMaxMS && FrameTimeMaxMS <= int.MaxValue))
            return false;
        if (CapSampleFrames == 0 ? FramesUnderCap.HasValue : !(FramesUnderCap <= CapSampleFrames))
            return false;
        if (PingSamples == 0 ? PingAverageMS.HasValue || PingMaximumMS.HasValue :
            !(PingAverageMS <= PingMaximumMS && PingMaximumMS <= int.MaxValue))
            return false;
        if (PacketsReceived == 0)
            return PacketsLost == 0 && PacketLossPercent is null;
        return PacketsLost <= ulong.MaxValue - PacketsReceived && PacketLossPercent is >= 0 and <= 100 &&
            Math.Abs(PacketLossPercent.Value - (100d * PacketsLost / (PacketsReceived + PacketsLost))) <= 0.001;
    }

    private static bool ValidActivity(ImmutableDictionary<string, ImmutableArray<double>>? values) =>
        values is null || values.Values.All(pair => !pair.IsDefault && pair.Length == 2 &&
            pair.All(value => double.IsFinite(value) && value >= 0));
}

public interface IMatchHistoryFiles
{
    // Open only a contained, readable regular main/stats/matches.jsonl. Missing or rejected paths
    // return null. Report access failures so Check-up can distinguish them from an empty history.
    Stream? OpenMatchHistory();
}

public static class MatchSummaries
{
    public const int RecordLimit = 200;
    public const int MaximumBytes = RecordLimit * 8192;

    public static ImmutableArray<MatchSummary> Read(IMatchHistoryFiles files)
    {
        using Stream? stream = files.OpenMatchHistory();
        return stream is null ? [] : Read(stream);
    }

    public static ImmutableArray<MatchSummary> Read(Stream stream)
    {
        long length = stream.Length;
        long offset = Math.Max(0, length - MaximumBytes);
        stream.Seek(offset, SeekOrigin.Begin);
        byte[] buffer = new byte[(int)(length - offset)];
        var data = buffer.AsSpan(0, stream.ReadAtLeast(buffer, buffer.Length, false));
        if (offset > 0)
        {
            int newline = data.IndexOf((byte)'\n');
            if (newline < 0)
                return [];
            data = data[(newline + 1)..];
        }

        var found = new Queue<MatchSummary>();
        while (!data.IsEmpty)
        {
            int newline = data.IndexOf((byte)'\n');
            if (newline < 0)
                break;
            var line = data[..newline];
            data = data[(newline + 1)..];
            if (line.IsEmpty)
                continue;
            try
            {
                var record = JsonSerializer.Deserialize(line, MatchSummaryJson.Default.MatchSummary);
                if (record?.IsValid() != true)
                    continue;
                if (found.Count == RecordLimit)
                    found.Dequeue();
                found.Enqueue(record);
            }
            catch (JsonException) { }
        }
        return [.. found];
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MatchSummary))]
internal sealed partial class MatchSummaryJson : JsonSerializerContext;

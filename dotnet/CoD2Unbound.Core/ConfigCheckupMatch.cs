using System.Collections.Immutable;
using System.Globalization;

namespace CoD2Unbound.Core;

public enum ConfigCheckupTone { Neutral, Information, Positive, Caution, Negative }
public sealed record ConfigCheckupGauge(ConfigCheckupArea Area, string Value, string Status, ConfigCheckupTone Tone,
    string Detail, string? Figures = null);

public sealed record ConfigCheckupMatch(MatchSummary? Latest, MatchSummary? Shown, int Count, bool RequestedMissing)
{
    public static ConfigCheckupMatch Select(ImmutableArray<MatchSummary> matches, string? requestedId)
    {
        var latest = matches.LastOrDefault();
        var requested = requestedId is null ? null : matches.LastOrDefault(match => match.Id == requestedId);
        return new(latest, requested ?? latest, matches.Length, requestedId is not null && requested is null);
    }

    public string? Title => Shown is { } match ? $"{MapCatalog.DisplayName(match.Map)} · {GameModes.Long(match.Mode)}" : null;
    public ConfigCheckupMeasurements Measurements => Shown is { } match
        ? new(match.FrameQuantilesAreExact ? match.FrameTimeP99MS : null, PingMS: match.PingAverageMS,
            PacketLossPercent: match.PacketLossPercent, Source: Title)
        : new();

    public ConfigCheckupGauge Movement()
    {
        if (Shown is not { FrameCount: > 0, FrameTimeP50MS: { } p50, FrameTimeP99MS: { } p99, FrameTimeMaxMS: { } max } match)
            return new(ConfigCheckupArea.Movement, "—", "Not measured", ConfigCheckupTone.Neutral,
                "Play a match to see how smoothly the game runs on this PC.",
                "Movement timing is available after a match records it.");

        string figures = $"Frame time {Quantile("p50", p50)} · {Quantile("p99", p99)} · maximum {Figure(max)} ms · {match.FrameCount:N0} samples.";
        double? share = match.FramesUnderCap is { } within && match.CapSampleFrames > 0
            ? (double)within / match.CapSampleFrames : null;
        if (share is { } fraction)
            figures += $" {match.FramesUnderCap:N0} of {match.CapSampleFrames:N0} samples met their timing target ({fraction:P1}).";
        if (max > 100 || !match.FrameQuantilesAreExact && match.FrameTimeHistogramLimitMS is >= 100)
            return Gauge("Pauses", "Long pauses", ConfigCheckupTone.Caution,
                $"The longest pause lasted {MatchSummary.FreezeDuration(max)}.");
        return share switch
        {
            >= 0.99 => Gauge("Steady", "Smooth", ConfigCheckupTone.Positive,
                "The game met its timing target for almost the whole match."),
            >= 0.95 => Gauge("Some dips", "Watch", ConfigCheckupTone.Caution,
                "An occasional delay can make jumps feel different."),
            not null => Gauge("Uneven", "Could be better", ConfigCheckupTone.Caution,
                "Frequent timing delays can make jumps and aim feel less even."),
            _ => Gauge("—", "No timing target", ConfigCheckupTone.Neutral, "This match recorded frame times without a timing target.")
        };

        ConfigCheckupGauge Gauge(string value, string status, ConfigCheckupTone tone, string detail) =>
            new(ConfigCheckupArea.Movement, value, status, tone, detail, figures);

        string Quantile(string name, double value) => match.FrameTimeHistogramLimitMS switch
        {
            null => $"{name} at most {Figure(value)} ms",
            double limit when value >= limit => $"{name} between {Figure(limit)} and {Figure(value)} ms",
            _ => $"{name} {Figure(value)} ms"
        };
    }

    public ConfigCheckupGauge Connection()
    {
        if (Shown is not { } match)
            return new(ConfigCheckupArea.Connection, "—", "Not measured", ConfigCheckupTone.Neutral,
                "Play a match to see your ping and how steady your connection was.",
                "Ping and missing server updates are available after a match records them.");
        bool measured = match.PingSamples > 0 && match.PingAverageMS.HasValue && match.PingMaximumMS.HasValue;
        string value = measured ? $"{match.PingAverageMS:F0} ms" : "—";
        string ping = measured
            ? $"Ping averaged {Figure(match.PingAverageMS!.Value)} ms and peaked at {Figure(match.PingMaximumMS!.Value)} ms across {match.PingSamples:N0} samples."
            : "This match did not record ping.";
        double? loss = match.PacketsReceived > 0 ? match.PacketLossPercent : null;
        string figures = ping + (loss is { } percent
            ? $" Missing server updates {Figure(percent)}% ({match.PacketsLost:N0} of {match.PacketsReceived + match.PacketsLost:N0}), estimated from gaps in the update sequence."
            : " Missing server updates were not measured.");
        return loss switch
        {
            null => Gauge("Not measured", ConfigCheckupTone.Neutral, "This match did not record how steady the connection was."),
            < 1 => Gauge("Steady", ConfigCheckupTone.Positive, "Almost every update from the server arrived."),
            < 5 => Gauge("Watch", ConfigCheckupTone.Caution, "Some server updates were missing. Other players may have skipped now and then."),
            _ => Gauge("Could be better", ConfigCheckupTone.Caution, "Many server updates were missing. Other players may have skipped often.")
        };

        ConfigCheckupGauge Gauge(string status, ConfigCheckupTone tone, string detail) =>
            new(ConfigCheckupArea.Connection, value, status, tone, detail, figures);
    }

    public static string Duration(double seconds)
    {
        var whole = (long)Math.Round(seconds);
        return whole < 60 ? $"{whole} s" : whole < 3600 ? $"{whole / 60} min" : $"{whole / 3600} h {whole / 60 % 60} min";
    }

    private static string Figure(double value) => value.ToString("0.#", CultureInfo.CurrentCulture);
}

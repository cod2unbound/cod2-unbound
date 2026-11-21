using System.Globalization;

namespace CoD2Unbound.Core;

public enum MatchConnection { Steady, Dips, NotMeasured }
public static class MatchFacts
{
    public static string Duration(double seconds)
    {
        ulong value = double.IsFinite(seconds) && seconds > 0 ? (ulong)Math.Min(Math.Round(seconds, MidpointRounding.AwayFromZero), 9_223_372_036_854_775_808d) : 0;
        if (value < 60)
            return FormattableString.Invariant($"{value} s");
        ulong minutes = value / 60;
        return minutes < 60 ? FormattableString.Invariant($"{minutes} min") :
            FormattableString.Invariant($"{minutes / 60} h {minutes % 60} min");
    }

    public static DateTimeOffset? Date(string text) => DateTimeOffset.TryParseExact(text,
        ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"], CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal, out var date) ? date : null;

    public static string Played(DateTimeOffset value, DateTimeOffset now, TimeZoneInfo timeZone, bool includeTime)
    {
        var recorded = TimeZoneInfo.ConvertTime(value, timeZone);
        var today = TimeZoneInfo.ConvertTime(now, timeZone).Date;
        int age = (today - recorded.Date).Days;
        string day = age switch { 0 => "TODAY", 1 => "YESTERDAY", _ => LauncherText.StencilDay(recorded) };
        return includeTime ? day + recorded.ToString(" HH:mm", CultureInfo.InvariantCulture) : day;
    }

    public static MatchConnection Connection(long packets, double? loss) => packets > 0 && loss is { } value
        ? value < 1 ? MatchConnection.Steady : MatchConnection.Dips : MatchConnection.NotMeasured;

    public static string ConnectionText(MatchConnection connection) => connection switch
    {
        MatchConnection.Steady => "Steady", MatchConnection.Dips => "Some dips", _ => "Not measured"
    };
}

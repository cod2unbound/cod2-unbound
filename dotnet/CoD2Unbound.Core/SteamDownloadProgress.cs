using System.Collections.Immutable;
using System.Globalization;

namespace CoD2Unbound.Core;

public enum SteamLanguage { English, French, German, Italian, Spanish }
public readonly record struct SteamDepot(int Id, long Bytes);

public sealed class SteamDownloadProgress(SteamLanguage language)
{
    private readonly ImmutableArray<SteamDepot> depots = Depots(language);
    private int current;
    private double percent;
    private long written, writtenAtLastFile;

    public const int AppId = 2630;
    public long Total => depots.Sum(depot => depot.Bytes);
    public long RoomNeeded => Total + 300_000_000;
    public long Done { get; private set; }

    public static ImmutableArray<SteamDepot> Depots(SteamLanguage language) =>
    [
        new(2631, 12_000_000), new(2632, 1_360_000_000), new(2633, 800_000_000),
        new(language switch
        {
            SteamLanguage.English => 2634, SteamLanguage.German => 2636, SteamLanguage.French => 2637,
            SteamLanguage.Spanish => 2638, SteamLanguage.Italian => 2639,
            _ => throw new ArgumentOutOfRangeException(nameof(language))
        }, 1_530_000_000)
    ];

    public void Depot(int id)
    {
        int index = 0;
        while (index < depots.Length && depots[index].Id != id)
            index++;
        if (index == depots.Length || index == current)
            return;
        current = index;
        percent = 0;
        Settle();
    }

    public void File(double value)
    {
        if (!double.IsFinite(value) || value is < 0 or > 100)
            return;
        if (value + 20 < percent && current + 1 < depots.Length)
            current++;
        percent = value;
        writtenAtLastFile = written;
        Settle();
    }

    public void Wrote(long bytes)
    {
        written = Math.Max(written, bytes);
        Settle();
    }

    public void Finish() => Done = Total;

    public static string Gigabytes(long bytes) =>
        (Math.Max(0, bytes) / 1_000_000_000d).ToString("F2", CultureInfo.InvariantCulture);

    private void Settle()
    {
        long byFiles = depots.Take(current).Sum(depot => depot.Bytes) + (long)(depots[current].Bytes * percent / 100);
        long ceiling = Total - Math.Max(1, Total / 500);
        long diskProgress = Math.Min(written - writtenAtLastFile, Math.Max(0, ceiling - byFiles));
        Done = Math.Max(Done, Math.Min(ceiling, byFiles + diskProgress));
    }
}

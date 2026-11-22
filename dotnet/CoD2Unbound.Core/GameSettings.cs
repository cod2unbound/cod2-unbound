using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace CoD2Unbound.Core;

public enum GameEdition
{
    [JsonStringEnumMemberName("cod2x")] Cod2x,
    [JsonStringEnumMemberName("original")] Original
}

public enum LegacyMacRenderer
{
    [JsonStringEnumMemberName("metal")] Metal,
    [JsonStringEnumMemberName("openGL")] OpenGL
}

public enum LauncherAppearance
{
    [JsonStringEnumMemberName("system")] System,
    [JsonStringEnumMemberName("light")] Light,
    [JsonStringEnumMemberName("dark")] Dark
}

public sealed partial record GameSettings
{
    public string Resolution { get; init; } = "1920x1080";
    public string Fullscreen { get; init; } = "borderless";
    public long Fps { get; init; } = 250;
    public bool Vsync { get; init; }
    public bool RawMouse { get; init; } = true;
    public double Sensitivity { get; init; } = 5.0;
    public double Dpi { get; init; } = 800.0;
    public double Volume { get; init; } = 0.8;
    public long Anisotropy { get; init; } = 8;
    public string Advanced { get; init; } = "";
    public bool MatchSummariesEnabled { get; init; } = true;
    public GameEdition Engine { get; init; } = GameEdition.Cod2x;
    public LegacyMacRenderer Renderer { get; init; } = LegacyMacRenderer.Metal;
    public int RenderScale { get; init; } = 100;
    public bool SendCrashReports { get; init; }
    public bool SendCoD2xReports { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool SendUsageData { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool UsageDataAsked { get; init; }
    [JsonIgnore]
    public UsageConsent UsageConsent => SendUsageData ? UsageConsent.Yes : UsageDataAsked ? UsageConsent.No : UsageConsent.NotAsked;

    public GameSettings WithUsageConsent(UsageConsent answer) =>
        this with { UsageDataAsked = answer != UsageConsent.NotAsked, SendUsageData = answer == UsageConsent.Yes };
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WindowsRenderMode? WindowsRenderer { get; init; }
    public bool CrashReportsAsked { get; init; }
    public bool HideWhilePlaying { get; init; } = true;
    public LauncherAppearance Appearance { get; init; } = LauncherAppearance.System;
    public bool RefreshServersOnOpen { get; init; }
    public bool CheckUpdatesAutomatically { get; init; } = true;
    public ImmutableHashSet<string> DismissedWarnings { get; init; } = [];
    public bool IntroFilmsPlayed { get; init; }

    [JsonIgnore]
    public double? CentimetresPer360 => MouseDistance.Centimetres(Dpi, Sensitivity);

    public GameSettings Reset(GameSettings defaults) => defaults with
    {
        IntroFilmsPlayed = IntroFilmsPlayed,
        CrashReportsAsked = CrashReportsAsked,
        UsageDataAsked = UsageDataAsked,
        SendUsageData = SendUsageData
    };

    public static FrozenSet<string> MachineDvarNames { get; } = new[]
    {
        "r_fullscreen", "r_borderless", "r_renderer", "r_renderscale", "r_metalfx", "r_hdr", "com_introplayed",
        "logfile", "developer", "fs_basepath", "fs_homepath", "fs_game", "fs_cdpath", "fs_copyfiles", "fs_restrict",
        "loc_language", "dedicated", "com_hunkmegs", "net_port", "cl_matchsummaries"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> ReservedAdvancedNames = new[]
    {
        "r_mode", "fs_basepath", "fs_homepath", "password", "cl_cdkey", "cdkey", "dpi", "r_renderer",
        "r_renderscale", "r_metalfx", "r_hdr", "cl_matchsummaries"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly (string Name, string Value)[] FirstRunPicture =
    [
        ("r_glow", "1"), ("r_distortion", "1"), ("r_rendererPreference", "none"), ("r_zfeather", "1"),
        ("sc_enable", "1"), ("r_dlightLimit", "4"), ("fx_sort", "1"), ("r_depthPrepassModels", "0")
    ];

    internal static IEnumerable<string> LoadoutDefaultNames =>
        FirstRunPicture.Select(pair => pair.Name).Concat(["com_maxfps", "m_rinput", "r_mode", "r_swapInterval", "in_rawmouse"]);

    internal List<(string Name, string Value)> Dvars()
    {
        if (!DisplayResolution.TryParse(Resolution, out _) || Fullscreen is not ("exclusive" or "borderless" or "spaces" or "windowed") ||
            Fps is < 0 or > 1000 || !(Sensitivity is >= 0.01 and <= 100) || !(Volume is >= 0 and <= 1) ||
            Anisotropy is not (2 or 4 or 8 or 16))
            throw new LaunchPlanException("Choose a valid resolution, frame cap (0–1000, where 0 is no cap), sensitivity, volume and filtering level.");

        List<(string Name, string Value)> pairs =
        [
            ("r_mode", Resolution), ("r_fullscreen", Bit(Fullscreen != "windowed")), ("r_borderless", Bit(Fullscreen == "borderless")),
            ("com_maxfps", Invariant(Fps)), ("r_swapInterval", Bit(Vsync)), ("in_rawmouse", Bit(RawMouse)), ("m_rinput", Bit(RawMouse)),
            ("sensitivity", Fraction(Sensitivity)), ("snd_volume", Fraction(Volume)), ("r_anisotropy", Invariant(Anisotropy)),
            ("m_filter", "0"), ("cl_mouseAccel", "0"), ("logfile", "0"), ("developer", "0"), ("com_introPlayed", "1"),
            ("cl_matchSummaries", Bit(MatchSummariesEnabled))
        ];
        foreach (string line in Advanced.Split('\n'))
        {
            if (TrimHorizontal(line).Length == 0)
                continue;
            var pair = AdvancedPair(line);
            if (pair is not { } valid || !AdvancedName().IsMatch(valid.Name) || ReservedAdvancedNames.Contains(valid.Name) ||
                valid.Name.Contains("password", StringComparison.OrdinalIgnoreCase) || valid.Name.Contains("codkey", StringComparison.OrdinalIgnoreCase))
                throw new LaunchPlanException("Advanced options use one “dvar value” per line. Paths, credentials and the renderer’s settings are managed separately.");
            if (!SafeValue(valid.Value))
                throw new LaunchPlanException("Dvar values cannot contain quotes, separators or control characters.");

            pairs.RemoveAll(previous => previous.Name == valid.Name);
            pairs.Add(valid);
        }
        if (pairs.Sum(pair => Encoding.UTF8.GetByteCount(pair.Name) + Encoding.UTF8.GetByteCount(pair.Value) + 12) >= 2500)
            throw new LaunchPlanException("Advanced options exceed the engine's startup limit.");
        return pairs;
    }

    internal IEnumerable<(string Name, string Value)> LaunchDvars(bool migrated) =>
        Dvars().Where(pair => !migrated || MachineDvarNames.Contains(pair.Name));

    internal IEnumerable<(string Name, string Value)> LoadoutDefaults(bool migrated, IReadOnlyDictionary<string, string> loadout)
    {
        var launched = LaunchDvars(migrated).Select(pair => pair.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        IEnumerable<(string Name, string Value)> values = FirstRunPicture;
        if (migrated)
        {
            var own = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in Dvars())
                own[pair.Name] = pair.Value;
            string raw = loadout.TryGetValue("in_rawmouse", out string? oldRaw) ? Bit(oldRaw != "0") : own.GetValueOrDefault("m_rinput", "1");
            values =
            [
                ("com_maxfps", own.GetValueOrDefault("com_maxfps", Invariant(Fps))), ("m_rinput", raw),
                ("r_mode", own.GetValueOrDefault("r_mode", Resolution)), ("r_swapInterval", own.GetValueOrDefault("r_swapInterval", Bit(Vsync))),
                .. FirstRunPicture
            ];
        }
        return values.Where(pair => !launched.Contains(pair.Name) && !loadout.ContainsKey(pair.Name));
    }

    internal static (string Name, string Value)? AdvancedPair(string line)
    {
        string trimmed = TrimHorizontal(line);
        string[] prefixes = ["+seta ", "+set ", "seta ", "set "];
        foreach (string prefix in prefixes)
        {
            if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            trimmed = TrimHorizontal(trimmed[prefix.Length..]);
            break;
        }

        int split = trimmed.AsSpan().IndexOfAny('=', ' ', '\t');
        if (split < 0)
            return null;
        string name = TrimHorizontal(trimmed[..split]);
        string value = TrimHorizontal(trimmed[(split + 1)..]);
        if (trimmed[split] != '=' && value.StartsWith('='))
            value = TrimHorizontal(value[1..]);
        return (name, value);
    }

    internal static bool SafeValue(string value) => SafeEngineText(value) && !value.Contains('\\');

    internal static bool SafeEngineText(string value) => Encoding.UTF8.GetByteCount(value) <= 512 &&
        !value.Any(character => character is < ' ' or '\x7f' or '"' or ';' or '+');

    private static string Bit(bool value) => value ? "1" : "0";
    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Fraction(double value)
    {
        string text = value.ToString("R", CultureInfo.InvariantCulture).Replace('E', 'e');
        return text.Contains('.') || text.Contains('e') ? text : text + ".0";
    }

    internal static string TrimHorizontal(string value)
    {
        static bool Horizontal(char character) => character == '\t' ||
            char.GetUnicodeCategory(character) == UnicodeCategory.SpaceSeparator;
        int start = 0, end = value.Length;
        while (start < end && Horizontal(value[start]))
            start++;
        while (end > start && Horizontal(value[end - 1]))
            end--;
        return value[start..end];
    }

    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z0-9_]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex AdvancedName();
}

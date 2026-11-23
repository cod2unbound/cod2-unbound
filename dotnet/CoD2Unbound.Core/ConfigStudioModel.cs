using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CoD2Unbound.Core;

public enum ConfigExpertFilter { Group, Changed, Locked }

public sealed record ConfigSettingSpec(ConfigCatalogSetting Setting, string Type, double? Minimum, double? Maximum,
    ImmutableArray<string> Choices, string? DefaultText, bool LauncherOwned)
{
    public string Name => Setting.Name;
    public string Title => Setting.Title;
    public string? Unit => Setting.Control.Unit;
    public double Step => Setting.Control.Step is > 0 and var step ? step : Type == "int" ? 1 : 0;

    public static ConfigSettingSpec? Create(ConfigCatalogSetting setting, ConfigCatalogMode mode)
    {
        if (!setting.IsEditable(mode) || setting.Variant(mode) is not { Type: { } type } variant)
            return null;
        ImmutableArray<string> choices = variant.EnumValues is { Status: "literal", Value: { ValueKind: JsonValueKind.Array } values }
            ? [.. values.EnumerateArray().Select(item => item.GetString()!)] : [];
        string? defaultText = variant.DefaultValue is { Status: "literal", Value: { } value } ? ConfigValues.Default(value, type, choices) : null;
        return new(setting, type, ConfigChecker.Literal(variant.Minimum), ConfigChecker.Literal(variant.Maximum),
            choices, defaultText, GameSettings.MachineDvarNames.Contains(setting.Name));
    }

    public (double Min, double Max)? SliderRange
    {
        get
        {
            if (Setting.Control.PracticalRange is { } range)
            {
                double low = Math.Max(range.Min, Minimum ?? range.Min), high = Math.Min(range.Max, Maximum ?? range.Max);
                if (low < high)
                    return (low, high);
            }
            return Minimum is { } min && Maximum is { } max && max > min &&
                ((Step <= 0) || (max - min) / Step <= 200) ? (min, max) : null;
        }
    }
}

public sealed record ConfigSettingState(ConfigSettingSpec Spec, string? Raw, bool Locked, string? Note)
{
    public string? Value => Raw ?? Spec.DefaultText;
    public bool Changed => Raw is not null && (Spec.DefaultText is null || !ConfigValues.Same(Spec, Raw, Spec.DefaultText));
    public string Display => ConfigValues.Display(Spec, Value);
}

public static partial class ConfigValues
{
    public static string MousePitch(string? value, bool inverted)
    {
        string text = value?.Trim() ?? "";
        double? number = Number(text, "float");
        if (number == 0)
            return "0";
        string magnitude = number is null ? "0.022" : NumericPrefix().Match(text).Value.TrimStart('+', '-');
        return inverted ? "-" + magnitude : magnitude;
    }

    public static int? Integer(string text)
    {
        var bytes = text.AsSpan();
        while (!bytes.IsEmpty && bytes[0] is ' ' or '\t' or '\r' or '\n' or '\v' or '\f')
            bytes = bytes[1..];
        bool negative = !bytes.IsEmpty && bytes[0] == '-';
        if (!bytes.IsEmpty && bytes[0] is '+' or '-')
            bytes = bytes[1..];
        long value = 0, max = negative ? 2_147_483_648L : int.MaxValue;
        foreach (char digit in bytes)
        {
            if (!char.IsAsciiDigit(digit))
                break;
            value = value * 10 + digit - '0';
            if (value > max)
                return null;
        }
        return (int)(negative ? -value : value);
    }

    public static double? Number(string text)
    {
        var match = NumericPrefix().Match(text.Trim());
        return match.Success && double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) &&
            double.IsFinite(value) ? value : null;
    }

    public static double? Number(string text, string type)
    {
        if (type is "bool" or "int")
            return Integer(text);
        double? value = Number(text);
        if (type != "float" || value is null)
            return value;
        float single = (float)value.Value;
        return float.IsFinite(single) ? single : null;
    }

    public static bool Same(ConfigSettingSpec spec, string a, string b) => spec.Type switch
    {
        "bool" => Integer(a) is { } left && Integer(b) is { } right ? (left != 0) == (right != 0) : a == b,
        "int" => Integer(a) is { } integer ? integer == Integer(b) : a == b,
        "float" => Number(a, "float") is { } value ? value == Number(b, "float") : a == b,
        "enum" => Choice(a, spec.Choices) is { } index ? index == Choice(b, spec.Choices) : a == b,
        "vec2" or "vec3" or "vec4" or "color" => Vector(a).SequenceEqual(Vector(b)),
        _ => a == b
    };

    public static string Display(ConfigSettingSpec spec, string? value)
    {
        if (value is null)
            return "Not set";
        bool numeric = spec.Type is "int" or "float";
        var labels = spec.Setting.Control.Labels;
        string? label = labels?.OrderBy(pair => Number(pair.Key) ?? 0).ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .FirstOrDefault(pair => Same(spec, pair.Key, value)).Value;
        if (label is not null)
            return numeric && Number(value, spec.Type) is { } number && spec.Unit is { } unit
                ? $"{label} ({Format(number, spec.Type == "int")} {unit})" : label;

        return spec.Type switch
        {
            "bool" => Integer(value) is { } flag ? flag != 0 ? "On" : "Off" : value,
            "enum" => Choice(value, spec.Choices) is { } selection ? spec.Choices[selection] : value,
            "int" or "float" => Numeric(),
            "string" => spec.Name.Contains("password", StringComparison.OrdinalIgnoreCase) ? value.Length == 0 ? "None" : "Hidden"
                : value.Length == 0 ? "Empty" : value,
            "vec2" or "vec3" or "vec4" or "color" => string.Join(' ', Vector(value).Select(n => Format(n))),
            _ => value
        };

        string Numeric()
        {
            if (Number(value, spec.Type) is not { } number)
                return value;
            if (spec.Name.Equals("snd_volume", StringComparison.OrdinalIgnoreCase))
                return Format(number * 100, integer: true) + "%";
            string text = Format(number, spec.Type == "int");
            return spec.Unit is { } unit ? $"{text} {unit}" : text;
        }
    }

    public static string Format(double value, bool integer = false)
    {
        if (!double.IsFinite(value))
            return "0";
        if (integer)
            value = Math.Round(value, MidpointRounding.AwayFromZero);
        string text = value.ToString(integer ? "0" : "0.######", CultureInfo.InvariantCulture);
        return text == "-0" ? "0" : text;
    }

    public static string Clamped(double value, ConfigSettingSpec spec)
    {
        if (spec.Minimum is { } min) value = Math.Max(min, value);
        if (spec.Maximum is { } max) value = Math.Min(max, value);
        return Format(value, spec.Type == "int");
    }

    internal static string? Default(JsonElement value, string type, ImmutableArray<string> choices) =>
        value.ValueKind switch
        {
            JsonValueKind.True when type == "bool" => "1",
            JsonValueKind.False when type == "bool" => "0",
            JsonValueKind.Number => type == "enum" && value.TryGetInt32(out int index) &&
                index >= 0 && index < choices.Length ? choices[index] : Format(value.GetDouble(), type == "int"),
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Array when value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.Number) =>
                string.Join(' ', value.EnumerateArray().Select(item => Format(item.GetDouble()))),
            _ => null
        };

    private static int? Choice(string text, ImmutableArray<string> choices)
    {
        for (int index = 0; index < choices.Length; index++)
            if (choices[index].Equals(text, StringComparison.OrdinalIgnoreCase))
                return index;
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value >= 0 && value < choices.Length
            ? value : null;
    }

    private static IEnumerable<double> Vector(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Number).OfType<double>();

    [GeneratedRegex(@"\A[+-]?(?:[0-9]+\.?[0-9]*|\.[0-9]+)(?:[eE][+-]?[0-9]+)?", RegexOptions.CultureInvariant)]
    private static partial Regex NumericPrefix();
}

public sealed class ConfigStudioModel
{
    private static readonly string[] Importance =
    [
        "com_maxfps", "cg_drawfps", "sensitivity", "in_rawmouse", "cl_mouseaccel", "m_filter", "m_pitch", "rate", "snaps",
        "cl_maxpackets", "cg_drawlagometer", "r_gamma", "r_picmip", "cg_drawcrosshair", "cg_drawcrosshairnames",
        "cg_chatheight", "snd_volume", "r_anisotropy", "cg_brass", "cg_marks", "name", "cl_allowdownload"
    ];
    private readonly ConfigCatalog catalog;
    private readonly CompareInfo compare;
    public ConfigCatalogMode Mode { get; }
    public ImmutableArray<ConfigSettingSpec> Specs { get; }

    public ConfigStudioModel(ConfigCatalog catalog, ConfigCatalogMode mode, CultureInfo culture)
    {
        this.catalog = catalog;
        Mode = mode;
        compare = culture.CompareInfo;
        var natural = StringComparer.Create(culture, CompareOptions.NumericOrdering | CompareOptions.IgnoreCase);
        Specs = [.. catalog.Settings.Select(setting => ConfigSettingSpec.Create(setting, mode)).OfType<ConfigSettingSpec>()
            .OrderBy(spec => spec.Title, natural)];
    }

    public int ProtectedCount(string group) => catalog.Settings.Count(setting => setting.Group == group &&
        setting.Availability == "registered" && setting.Variant(Mode) is not null && !setting.IsEditable(Mode));

    public ImmutableArray<ConfigSettingSpec> Guided(string group) =>
        [.. Ordered(Specs.Where(spec => spec.Setting.Group == group && spec.Setting.Guided && !spec.LauncherOwned))];

    public static ConfigSettingState State(ConfigSettingSpec spec, ConfigDocument? document, ConfigStudioMetadata? metadata) =>
        new(spec, document?.EffectiveSetting(spec.Name), metadata?.Locks.Contains(spec.Name, StringComparer.OrdinalIgnoreCase) == true,
            metadata?.Notes.FirstOrDefault(pair => pair.Key.Equals(spec.Name, StringComparison.OrdinalIgnoreCase)).Value);

    public ImmutableArray<(string Group, ImmutableArray<ConfigSettingSpec> Rows)> Expert(string group, ConfigExpertFilter filter, string query,
        ConfigDocument? document, ConfigStudioMetadata? metadata)
    {
        string text = query.Trim();
        var selected = Specs.Where(spec => text.Length > 0 ? Matches(spec.Title, text) || Matches(spec.Setting.Summary, text) || Matches(spec.Name, text) :
            filter switch
            {
                ConfigExpertFilter.Changed => !spec.LauncherOwned && State(spec, document, metadata).Changed,
                ConfigExpertFilter.Locked => State(spec, document, metadata).Locked,
                _ => spec.Setting.Group == group
            }).ToLookup(spec => spec.Setting.Group);
        return [.. catalog.Groups.Where(item => selected.Contains(item.Id)).Select(item =>
            (item.Id, Ordered(selected[item.Id]).ToImmutableArray()))];
    }

    public ImmutableArray<ConfigSettingSpec> Jump(string query)
    {
        string text = query.Trim();
        if (text.Length == 0)
            return [.. Specs.Where(spec => spec.Setting.Guided).Take(8)];
        var primary = Specs.Where(spec => Matches(spec.Title, text) || Matches(spec.Name, text)).ToArray();
        return [.. primary.Concat(Specs.Where(spec => !primary.Contains(spec) && Matches(spec.Setting.Summary, text))).Take(30)];
    }

    public string? TakeTheirsBlock(ConfigSettingSpec spec, ConfigStudioState state, ConfigRules? rules,
        ImmutableArray<string> selectedRules, string ruleLabel)
    {
        if (state.ReadOnly) return "Configs are read-only while Call of Duty 2 is running.";
        if (spec.LauncherOwned) return "CoD2 Unbound sets this from Settings each time the game starts.";
        if (State(spec, state.Current?.Document, state.Current?.Metadata).Locked) return "Locked, so it stays yours.";
        if (spec.Setting.Variant(Mode)?.ServerControlled == true) return "The server sets this when you join, so it stays yours.";
        if (rules?.IsServerOwned(spec.Name, selectedRules) == true) return $"Set by the server under {ruleLabel}, so it stays yours.";
        return null;
    }

    public static ConfigSettingState? Compare(ConfigSettingSpec spec, ConfigSettingState mine, ConfigProfileRead? other,
        IReadOnlyDictionary<string, string>? reviewedPlayer = null)
    {
        ConfigSettingState? theirs = reviewedPlayer is not null
            ? reviewedPlayer.TryGetValue(spec.Name.ToLowerInvariant(), out string? value) ? new(spec, value, false, null) : null
            : other is null ? null : State(spec, other.Document, other.Metadata);
        return mine.Value is { } saved && theirs?.Value is { } candidate && !ConfigValues.Same(spec, saved, candidate)
            ? theirs : null;
    }

    public ConfigHistoryEntry TakeTheirs(ConfigStudioSession session, ConfigSettingSpec spec, ConfigProfileVersion expected,
        string value, string source, ConfigRules? rules, ImmutableArray<string> selectedRules, string ruleLabel)
    {
        var state = session.State;
        if (TakeTheirsBlock(spec, state, rules, selectedRules, ruleLabel) is { } reason)
            throw new InvalidOperationException(reason);
        var profile = state.Current;
        if (profile is null || !profile.Version.Matches(expected))
            throw new ConfigProfileException(ConfigProfileFailure.Conflict);
        return session.Mutate(profile.Profile.Id, store => store.Update(profile.Profile.Id, profile.Document.SetSetting(spec.Name, value),
            expected, null, $"Took {spec.Title} from {source}"));
    }

    public ConfigHistoryEntry ApplyText(ConfigStudioSession session, ConfigProfileVersion expected, string text)
    {
        if (session.State.Current is not { } read || !read.Version.Matches(expected))
            throw new ConfigProfileException(ConfigProfileFailure.Conflict);
        var review = ConfigTextCheck.Inspect(text, session.Dialect, catalog, Mode);
        if (!review.CanApply)
            throw new InvalidDataException("The game can’t read the edited text.");
        session.SetDraft(review.Document!);
        return session.SaveDraft("Edited as text");
    }

    public static string? DeleteBlock(ConfigStudioState? state, string? id)
    {
        var profile = id is null ? null : state?.Profiles.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (profile is null) return "There’s no loadout to delete.";
        if (state!.ReadOnly) return "Configs are read-only while Call of Duty 2 is running.";
        return profile.IsActive ? $"{profile.DisplayName} is in use for your next game. Choose another loadout for your next game first." : null;
    }

    private bool Matches(string value, string search) => compare.IndexOf(value, search, CompareOptions.IgnoreCase) >= 0;

    private static IOrderedEnumerable<ConfigSettingSpec> Ordered(IEnumerable<ConfigSettingSpec> settings) => settings.OrderBy(spec =>
    {
        if (!spec.Setting.Guided) return Importance.Length + 1;
        int index = Array.IndexOf(Importance, spec.Name.ToLowerInvariant());
        return index < 0 ? Importance.Length : index;
    });
}

public sealed record ConfigTextIssue(int Line, int Column, string Message, string Excerpt, bool Blocking, ByteSpan Range);
public sealed record ConfigTextReview(ConfigDocument? Document, ImmutableArray<ConfigTextIssue> Issues)
{
    public bool CanApply => Document is not null && !Issues.Any(item => item.Blocking);
}

public static class ConfigTextCheck
{
    public static ConfigTextReview Inspect(string text, ConfigDialect dialect, ConfigCatalog? catalog, ConfigCatalogMode mode)
    {
        var issues = new List<ConfigTextIssue>();
        var lineStarts = new List<int> { 0 };
        for (int offset = 0; offset < text.Length; offset++)
            if (text[offset] == '\n' || text[offset] == '\r' && (offset + 1 == text.Length || text[offset + 1] != '\n'))
                lineStarts.Add(offset + 1);
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] <= 255)
                continue;
            int end = char.IsSurrogatePair(text, index) ? index + 2 : index + 1;
            Add(new(index, end), "The game stores single-byte text, so this character can’t be saved.", true);
            return new(null, [.. issues]);
        }

        var bytes = ConfigDocument.Encode(text);
        var document = new ConfigDocument(bytes, dialect);
        foreach (var diagnostic in document.Diagnostics)
        {
            var range = diagnostic.Span ?? new(65_535, Math.Min(bytes.Length, 65_536));
            switch (diagnostic.Kind)
            {
                case ConfigDiagnosticKind.UnterminatedQuote:
                    Add(range, "This value opens a quote but never closes it.", true);
                    break;
                case ConfigDiagnosticKind.LineLimit:
                    Add(range, "This line is longer than the 4,095 characters the game reads.", true);
                    break;
                case ConfigDiagnosticKind.TokenizationFailure:
                    Add(range, diagnostic.Failure == ConfigTokenizationFailure.TooManyTokens
                        ? "This line has more than 512 words, so the game skips it." : "This line is too long for the game to read.", true);
                    break;
                case ConfigDiagnosticKind.CommandBufferOverflow:
                    Add(range, "The config is longer than the 65,535 characters the game reads.", true);
                    break;
                case ConfigDiagnosticKind.IgnoredNulSuffix:
                    Add(range, "The game stops reading at an invisible NUL character here.", false);
                    break;
            }
        }
        if (catalog is not null)
        {
            foreach (var command in document.Commands.Where(item => item.Kind == ConfigCommandKind.Setting && item.Tokens.Length >= 3))
            {
                var token = command.Tokens[1];
                var setting = catalog.Setting(token.Text);
                if (setting is null)
                    Add(token.Span, "The game doesn’t register this setting. It keeps the line, but nothing reads it unless a mod does.", false);
                else if (setting.Availability == "registered" && setting.Variant(mode) is not null && !setting.IsEditable(mode))
                    Add(token.Span, $"The game protects {setting.Title.ToLowerInvariant()} and normally refuses it from a config.", false);
            }
        }
        return new(document, [.. issues.OrderBy(item => item.Line).ThenBy(item => item.Column)]);

        void Add(ByteSpan range, string message, bool blocking)
        {
            int line = lineStarts.BinarySearch(range.Start);
            if (line < 0) line = ~line - 1;
            int upper = Math.Min(text.Length, Math.Max(range.Start + 1, Math.Min(range.End, range.Start + 60)));
            string excerpt = upper > range.Start ? text[range.Start..upper].Trim() : "";
            issues.Add(new(line + 1, range.Start - lineStarts[line] + 1, message, excerpt, blocking, range));
        }
    }
}

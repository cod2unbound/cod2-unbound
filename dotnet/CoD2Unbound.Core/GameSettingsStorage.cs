using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoD2Unbound.Core;

public static class GameSettingsJson
{
    public static GameSettings Decode(ReadOnlyMemory<byte> json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Expected an object for launcher settings.");

        var defaults = new GameSettings();

        T Read<T>(string key, T fallback, Func<JsonElement, T> decode, bool lenient = false)
        {
            if (!root.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null)
                return fallback;
            try { return decode(value); }
            catch (Exception error) when (error is InvalidOperationException or FormatException or JsonException)
            {
                return lenient ? fallback : throw new JsonException($"Invalid launcher setting {key}.", error);
            }
        }

        T Known<T>(string key, T fallback, Func<string?, T> values) =>
            Read(key, fallback, value => values(value.GetString()), lenient: true);

        return defaults with
        {
            Resolution = Read("resolution", defaults.Resolution, value => value.GetString()!),
            Fullscreen = Read("fullscreen", defaults.Fullscreen, value => value.GetString()!),
            Fps = Read("fps", defaults.Fps, Integer),
            Vsync = Read("vsync", defaults.Vsync, value => value.GetBoolean()),
            RawMouse = Read("rawMouse", defaults.RawMouse, value => value.GetBoolean()),
            Sensitivity = Read("sensitivity", defaults.Sensitivity, Number),
            Dpi = Read("dpi", defaults.Dpi, Number),
            Volume = Read("volume", defaults.Volume, Number),
            Anisotropy = Read("anisotropy", defaults.Anisotropy, Integer),
            Advanced = Read("advanced", defaults.Advanced, value => value.GetString()!),
            MatchSummariesEnabled = Read("matchSummariesEnabled", defaults.MatchSummariesEnabled, value => value.GetBoolean()),
            Engine = Known("engine", defaults.Engine, value => value == "original" ? GameEdition.Original : defaults.Engine),
            Renderer = Known("renderer", defaults.Renderer, value => value == "openGL" ? LegacyMacRenderer.OpenGL : defaults.Renderer),
            RenderScale = Read("renderScale", defaults.RenderScale, value => Integer(value) switch
            {
                85 => 85, 75 => 75, 67 => 67, 50 => 50, _ => defaults.RenderScale
            }, lenient: true),
            SendCrashReports = Read("sendCrashReports", defaults.SendCrashReports, value => value.GetBoolean(), lenient: true),
            SendCoD2xReports = Read("sendCoD2xReports", defaults.SendCoD2xReports, value => value.GetBoolean(), lenient: true),
            UsageDataAsked = Read("usageDataAsked", false, value => value.GetBoolean(), lenient: true),
            SendUsageData = Read("sendUsageData", false, value => value.GetBoolean(), lenient: true),
            WindowsRenderer = Known<WindowsRenderMode?>("windowsRenderer", null, value => value switch
            {
                "Direct3D9" => WindowsRenderMode.Direct3D9, "DirectX7" => WindowsRenderMode.DirectX7, _ => null
            }),
            WindowsMonitor = Read<string?>("windowsMonitor", null, value => value.GetString() is { Length: > 0 and <= 512 } id ? id : null, lenient: true),
            LastSettingsPane = Read("lastSettingsPane", 0, value => Integer(value) is >= 0 and <= 6 and var pane ? (int)pane : 0, lenient: true),
            CrashReportsAsked = Read("crashReportsAsked", defaults.CrashReportsAsked, value => value.GetBoolean(), lenient: true),
            HideWhilePlaying = Read("hideWhilePlaying", defaults.HideWhilePlaying, value => value.GetBoolean(), lenient: true),
            Appearance = Known("appearance", defaults.Appearance, value => value switch
            {
                "light" => LauncherAppearance.Light, "dark" => LauncherAppearance.Dark, _ => defaults.Appearance
            }),
            RefreshServersOnOpen = Read("refreshServersOnOpen", defaults.RefreshServersOnOpen, value => value.GetBoolean(), lenient: true),
            CheckUpdatesAutomatically = Read("checkUpdatesAutomatically", defaults.CheckUpdatesAutomatically, value => value.GetBoolean(), lenient: true),
            DismissedWarnings = Read("dismissedWarnings", defaults.DismissedWarnings,
                value => value.EnumerateArray().Select(item => item.GetString() ?? throw new JsonException()).ToImmutableHashSet(StringComparer.Ordinal),
                lenient: true),
            // A file with no flag predates the new-player intro flow.
            IntroFilmsPlayed = Read("introFilmsPlayed", true, value => value.GetBoolean(), lenient: true)
        };
    }

    public static byte[] Encode(GameSettings settings) => JsonSerializer.SerializeToUtf8Bytes(settings, GameSettingsContext.Default.GameSettings);

    internal static long Integer(JsonElement value)
    {
        if (value.TryGetInt64(out long integer))
            return integer;
        if (value.TryGetDecimal(out decimal number) && number is >= long.MinValue and <= long.MaxValue && number == decimal.Truncate(number))
            return (long)number;
        throw new FormatException();
    }

    private static double Number(JsonElement value)
    {
        double number = value.GetDouble();
        return double.IsFinite(number) ? number : throw new FormatException();
    }
}

public sealed class GameSettingsStorage(ISettingsFolder folder)
{
    public const string FileName = "launcher-settings.json";

    public async ValueTask<GameSettings> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (await folder.ReadAsync(FileName, cancellationToken).ConfigureAwait(false) is not { } bytes)
            return new();
        return GameSettingsJson.Decode(bytes.AsMemory());
    }

    public ValueTask WriteAsync(GameSettings settings, CancellationToken cancellationToken = default) =>
        folder.WriteAsync(FileName, [.. GameSettingsJson.Encode(settings)], cancellationToken);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(GameSettings))]
internal sealed partial class GameSettingsContext : JsonSerializerContext;

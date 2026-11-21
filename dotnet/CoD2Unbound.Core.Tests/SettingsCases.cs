using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CoD2Unbound.Core.Tests;

internal static partial class Program
{
    private static void SettingsCases()
    {
        using var file = Fixtures("settings");
        var root = file.RootElement;
        Check("fresh settings", () => SettingsEqual(root.GetProperty("fresh"), new()));
        foreach (var row in root.GetProperty("settings").EnumerateArray())
        {
            Check($"settings {row.String("id")}", () =>
            {
                bool accepted = row.TryGetProperty("decoded", out var decoded);
                GameSettings? actual = null;
                try { actual = GameSettingsJson.Decode(Encoding.UTF8.GetBytes(row.String("json")!)); }
                catch (JsonException) { }
                Equal(accepted, actual is not null);
                if (actual is null)
                    return;
                SettingsEqual(decoded, actual);
                SettingsEqual(row.GetProperty("reset"), actual.Reset(new()));
                Equal(row.GetProperty("centimetres").GetDouble(), actual.CentimetresPer360!.Value);
            });
        }
        foreach (var row in root.GetProperty("tags").EnumerateArray())
            Check($"release tag {row.String("input")}", () => Equal(row.String("version"), ReleaseNotes.Version(row.String("input")!)));
        foreach (var row in root.GetProperty("versions").EnumerateArray())
            Check($"release version {row.String("candidate")} over {row.String("current")}", () =>
                Equal(row.GetProperty("newer").GetBoolean(), ReleaseNotes.IsNewer(row.String("candidate")!, row.String("current")!)));
        foreach (var row in root.GetProperty("release").EnumerateArray())
            Check($"release feed {row.Int("id")}", () => ReleaseEqual(row, ReleaseNotes.Parse(Encoding.UTF8.GetBytes(row.String("json")!))));
        foreach (var row in root.GetProperty("offers").EnumerateArray())
            Check($"release offer {row.String("id")}", () =>
                Equal(row.GetProperty("offered").GetBoolean(), ReleasePackage.Offer(row.String("version")!, row.String("archive")!,
                    row.GetProperty("assets").EnumerateArray().Select(Asset)) is not null));
        foreach (var row in root.GetProperty("checksums").EnumerateArray())
            Check("release checksum", () => Equal(row.String("hash"), ReleasePackage.Checksum(
                Encoding.UTF8.GetBytes(row.String("input")!), row.String("archive")!)));
        foreach (var row in root.GetProperty("joins").EnumerateArray())
        {
            Check($"join eligibility {row.String("engine")}", () =>
            {
                var fields = row.GetProperty("fields");
                var server = fields.ValueKind == JsonValueKind.Null ? null : new GameServer("127.0.0.1", Dictionary(fields), 50, []);
                Equal(row.String("result"), Key(GameCapabilities.CheckJoin(Edition(row), server, row.GetProperty("lookedUp").GetBoolean())));
            });
        }
        foreach (var row in root.GetProperty("displays").EnumerateArray())
        {
            Check($"display capability {row.String("native")}", () =>
            {
                static DisplayResolution Display(string value)
                {
                    var parts = value.Split('x');
                    return new(int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
                }
                var native = Display(row.String("native")!);
                var sizes = DisplayResolutions.Offered(native, row.Strings("modes").Select(value => Display(value!)));
                Sequence(row.Strings("offered"), sizes.Select(size => size.ToString()));
                Equal(row.String("initial"), DisplayResolutions.Initial(native).ToString());
                Equal(row.String("fitting"), DisplayResolutions.Fitting("3024x1964", sizes).ToString());
                Equal(row.String("invalid"), DisplayResolutions.Fitting("wide", sizes).ToString());
            });
        }
        Check("settings storage private seam", () => SettingsStorage().GetAwaiter().GetResult());
        Check("optional launcher settings round trip and reset", () =>
        {
            var legacy = GameSettingsJson.Decode("{}"u8.ToArray());
            Equal(false, legacy.SendUsageData);
            Equal<WindowsRenderMode?>(null, legacy.WindowsRenderer);
            var edited = legacy with { SendUsageData = true, WindowsRenderer = WindowsRenderMode.DirectX7 };
            var restored = GameSettingsJson.Decode(GameSettingsJson.Encode(edited));
            Equal(true, restored.SendUsageData);
            Equal<WindowsRenderMode?>(WindowsRenderMode.DirectX7, restored.WindowsRenderer);
            Equal(false, restored.Reset(new()).SendUsageData);
            Equal<WindowsRenderMode?>(null, restored.Reset(new()).WindowsRenderer);
        });
        Check("stream archive SHA-256 verification", () =>
        {
            byte[] input = "archive fixture"u8.ToArray();
            string digest = Convert.ToHexString(SHA256.HashData(input));
            using var stream = new MemoryStream(input);
            Equal(true, ReleasePackage.VerifyArchive(stream, input.Length, digest));
            stream.Position = 0;
            Equal(false, ReleasePackage.VerifyArchive(stream, input.Length - 1, digest));
            stream.Position = 0;
            Equal(false, ReleasePackage.VerifyArchive(stream, input.Length + 1, digest));
            stream.Position = 0;
            Equal(false, ReleasePackage.VerifyArchive(stream, input.Length, new string('f', 64)));
        });
        Check("update activity interlock", () =>
        {
            Equal(null, ReleasePackage.Block(gameRunning: false, serversRunning: false));
            Equal(UpdateBlock.GameRunning, ReleasePackage.Block(gameRunning: true, serversRunning: true));
            Equal(UpdateBlock.ServersRunning, ReleasePackage.Block(gameRunning: false, serversRunning: true));
        });
    }

    private static void SettingsEqual(JsonElement expected, GameSettings settings)
    {
        using var actualDoc = JsonDocument.Parse(GameSettingsJson.Encode(settings));
        var actual = actualDoc.RootElement;
        Sequence(expected.EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal),
            actual.EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal));
        foreach (var field in expected.EnumerateObject())
        {
            if (field.Name == "dismissedWarnings")
                Sequence(expected.Strings(field.Name), actual.Strings(field.Name).Order(StringComparer.Ordinal));
            else if (field.Name is "sensitivity" or "dpi" or "volume")
                Equal(field.Value.GetDouble(), actual.GetProperty(field.Name).GetDouble());
            else if (!JsonElement.DeepEquals(field.Value, actual.GetProperty(field.Name)))
                throw new InvalidDataException($"Setting {field.Name}: expected {field.Value}, got {actual.GetProperty(field.Name)}.");
        }
    }

    private static ReleaseAsset Asset(JsonElement asset) =>
        new(asset.String("name")!, asset.GetProperty("size").GetInt64(), new(asset.String("url")!, UriKind.RelativeOrAbsolute));

    private static void ReleaseEqual(JsonElement expected, ReleaseFeed actual)
    {
        Equal(expected.String("latestVersion"), actual.LatestVersion);
        Sequence(expected.GetProperty("assets").EnumerateArray().Select(Asset), actual.LatestAssets);
        Sequence(expected.GetProperty("notes").EnumerateArray().Select(note =>
            new ReleaseNoteItem(note.String("tag")!, note.String("title")!, note.String("detail")!)), actual.LatestNotes);

        var dispatches = expected.GetProperty("dispatches").EnumerateArray().Select(dispatch =>
        {
            var date = dispatch.GetProperty("date");
            return new ReleaseDispatch(dispatch.String("stamp")!, dispatch.String("title")!, dispatch.String("summary")!,
                dispatch.String("link"), date.ValueKind == JsonValueKind.Null ? null : DateTimeOffset.FromUnixTimeSeconds(date.GetInt64()),
                dispatch.String("version"));
        });
        Sequence(dispatches, actual.Dispatches);
    }

    private static async Task SettingsStorage()
    {
        var folder = new MemoryPlatform("/private/fixture");
        var store = new GameSettingsStorage(folder);
        Equal(false, (await store.ReadAsync()).IntroFilmsPlayed);
        await folder.WriteAsync("launcher-settings.json", [.. "{}"u8], CancellationToken.None);
        Equal(true, (await store.ReadAsync()).IntroFilmsPlayed);
        var saved = new GameSettings { Dpi = 1600, Engine = GameEdition.Original, DismissedWarnings = ["switch-version"] };
        await store.WriteAsync(saved);
        var restored = await store.ReadAsync();
        Equal(saved.Dpi, restored.Dpi);
        Equal(saved.Engine, restored.Engine);
        Equal(saved.IntroFilmsPlayed, restored.IntroFilmsPlayed);
        Equal(true, saved.DismissedWarnings.SetEquals(restored.DismissedWarnings));
    }
}

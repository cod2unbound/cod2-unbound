using System.Text.Json;

namespace CoD2Unbound.Core.Tests;

internal static partial class Program
{
    private static void ExtraCases()
    {
        using var fixture = Fixtures("extras");
        var root = fixture.RootElement;
        foreach (var item in root.GetProperty("artwork").EnumerateArray())
            Check("Swift artwork " + item.GetProperty("id").GetString(), () =>
            {
                byte[] bytes = item.GetProperty("bytes").GetBytesFromBase64();
                if (!item.GetProperty("archive").GetBoolean())
                {
                    var pixels = IwiImageDecoder.Decode(bytes);
                    Equal(item.GetProperty("width").GetInt32(), pixels.Width);
                    Equal(item.GetProperty("height").GetInt32(), pixels.Height);
                    Sequence(item.GetProperty("rgba").GetBytesFromBase64(), pixels.Rgba);
                    foreach (int cut in Enumerable.Range(0, bytes.Length))
                        RejectData(() => IwiImageDecoder.Decode(bytes.AsSpan()[..cut]));
                    return;
                }
                using var source = new MemoryStream(bytes);
                if (!item.GetProperty("validArchive").GetBoolean())
                {
                    RejectData(() => new IwdArtworkArchive(source));
                    return;
                }
                var archive = new IwdArtworkArchive(source);
                const string name = "images/loadscreen_mp_toujane.iwi";
                var image = item.GetProperty("image");
                if (image.ValueKind == JsonValueKind.Null)
                    RejectData(() => archive.Image(name));
                else
                    Sequence(image.GetBytesFromBase64(), archive.Image(name));
            });

        foreach (var item in root.GetProperty("cod2x").EnumerateArray())
            Check("Swift bounded CoD2x archive extraction", () =>
            {
                var expected = item.GetProperty("archive");
                if (expected.ValueKind == JsonValueKind.Null)
                    RejectData(() => Cod2xFiles.ExtractArchive(item.GetProperty("dll").GetBytesFromBase64()));
                else
                {
                    byte[] bytes = item.GetProperty("dll").GetBytesFromBase64();
                    Sequence(expected.GetBytesFromBase64(), Cod2xFiles.ExtractArchive(bytes));
                }
            });

        var catalog = ConfigCatalog.LoadBundled();
        foreach (var row in root.GetProperty("config").EnumerateArray())
            Check("Swift Studio text diagnostics", () =>
            {
                var result = ConfigTextCheck.Inspect(row.GetProperty("text").GetString()!, ConfigDialect.Cod2x, catalog, ConfigCatalogMode.Cod2x);
                var expectedBytes = row.GetProperty("bytes");
                if (expectedBytes.ValueKind == JsonValueKind.Null)
                    Equal<ConfigDocument?>(null, result.Document);
                else
                    Sequence(expectedBytes.GetBytesFromBase64(), result.Document!.Serialized);
                var expected = row.GetProperty("issues").EnumerateArray().ToArray();
                Equal(expected.Length, result.Issues.Length);
                foreach (var (want, got) in expected.Zip(result.Issues))
                    Equal((want.GetProperty("line").GetInt32(), want.GetProperty("column").GetInt32(), want.GetProperty("message").GetString(),
                        want.GetProperty("excerpt").GetString(), want.GetProperty("blocking").GetBoolean(), want.GetProperty("start").GetInt32(),
                        want.GetProperty("length").GetInt32()), (got.Line, got.Column, got.Message, got.Excerpt, got.Blocking, got.Range.Start, got.Range.Length));
            });
        foreach (var row in root.GetProperty("values").EnumerateArray())
            Check("Swift Studio setting " + row.GetProperty("setting").GetString(), () =>
            {
                var spec = ConfigSettingSpec.Create(catalog.Setting(row.GetProperty("setting").GetString()!)!, ConfigCatalogMode.Cod2x)!;
                Equal(row.GetProperty("default").GetString(), spec.DefaultText);
                Sequence(Texts(row.GetProperty("choices")), spec.Choices);
                foreach (var item in row.GetProperty("values").EnumerateArray())
                {
                    string raw = item.GetProperty("raw").GetString()!;
                    Equal(item.GetProperty("text").GetString(), ConfigValues.Display(spec, raw));
                    Equal(item.GetProperty("same").GetBoolean(), ConfigValues.Same(spec, raw, "5"));
                    var typed = item.GetProperty("typed");
                    Equal(typed.ValueKind == JsonValueKind.Null ? (double?)null : typed.GetDouble(), ConfigValues.Number(raw, spec.Type));
                }
            });
        foreach (var row in root.GetProperty("loadouts").EnumerateArray())
            Check("Swift loadout identifier", () => Equal(row.GetProperty("id").GetString(),
                LoadoutName.Identifier(row.GetProperty("input").GetString()!, Texts(root.GetProperty("existing")))));

        foreach (var row in root.GetProperty("addresses").EnumerateArray())
            Check("Swift direct server address", () =>
            {
                string input = row.GetProperty("input").GetString()!;
                var address = ServerAddress.Parse(input);
                Equal(row.GetProperty("address").GetString(), address?.Address);
                Equal(row.GetProperty("password").GetString(), address?.Password);
                Equal(row.GetProperty("link").GetString(), ServerAddress.JoinLink(input));
                Equal(row.GetProperty("problem").GetString(), ServerAddress.PasswordProblem(input));
            });
        Check("Swift library format round-trips, keeps unrelated state and bounds history", () =>
        {
            var row = root.GetProperty("library");
            var library = LauncherLibrary.Read(row.GetProperty("data").GetBytesFromBase64());
            Sequence(Texts(row.GetProperty("played")), library.Played.Keys.Order(StringComparer.Ordinal));
            Equal(new DateTimeOffset(2001, 1, 1, 0, 2, 3, TimeSpan.Zero).AddMilliseconds(500), library.CachedAt);
            Equal("configs", library.NewsNext?.Open);
            Equal("Synthetic", library.Cache.Single().Players.Single().Name);
            var roundTrip = LauncherLibrary.Read(library.Write().AsMemory());
            Sequence(library.Recent, roundTrip.Recent);
            Equal(library.CachedAt, roundTrip.CachedAt);
            Equal(library.Played.Single(), roundTrip.Played.Single());
            Equal(library.NewsNext, roundTrip.NewsNext);
            Equal(library.Cache.Single().Address, roundTrip.Cache.Single().Address);
            for (int index = 0; index < 32; index++)
                library = library.Joined($"example{index}.invalid:28960", DateTimeOffset.UnixEpoch.AddSeconds(index));
            Equal(30, library.Recent.Length);
            Equal(30, library.Played.Count);
        });

        foreach (var row in root.GetProperty("preferences").EnumerateArray())
            Check("Swift server preference format", () =>
            {
                var prefs = ServerBrowserPreferences.Read(System.Text.Encoding.UTF8.GetBytes(row.GetProperty("raw").GetString()!));
                Equal(row.GetProperty("count").GetInt32(), prefs.Filters.Count);
                var label = prefs.Filters.Version switch
                {
                    ServerEditionFilter.Stock => "Original 1.3",
                    ServerEditionFilter.Cod2x => "CoD2x 1.4.6.8",
                    _ => "All"
                };
                Equal(row.GetProperty("version").GetString(), label);
                var library = LauncherLibrary.Read(root.GetProperty("library").GetProperty("data").GetBytesFromBase64());
                Equal(row.GetProperty("admits").GetBoolean(), prefs.Filters.Admits(library.Cache[0]));
                var expected = row.GetProperty("launched").EnumerateObject().Select(item => (item.Name, item.Value.GetDouble()));
                Sequence(expected, prefs.LaunchedAt.Select(pair => (pair.Key, (pair.Value - new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero)).TotalSeconds)));
                var written = ServerBrowserPreferences.Read(prefs.Write().AsMemory());
                Equal(prefs.Filters, written.Filters);
                Sequence(prefs.LaunchedAt, written.LaunchedAt);
            });
        Check("Swift typed History diff and sentence", () =>
        {
            var row = root.GetProperty("diff");
            var changes = ConfigDiff.Changes(new([.. row.GetProperty("original").GetBytesFromBase64()], null),
                new([.. row.GetProperty("modified").GetBytesFromBase64()], null, [.. "echo hello"u8]), ConfigDialect.Cod2x, catalog);
            Sequence(row.GetProperty("changes").EnumerateArray().Select(item => (
                item.GetProperty("id").GetString()!, item.GetProperty("label").GetString()!, item.GetProperty("before").GetString()!,
                item.GetProperty("after").GetString()!)), changes.Select(item => (item.Id, item.Label, item.Before, item.After)));
            Equal(row.GetProperty("summary").GetString(), ConfigDiff.Sentence(changes));
        });
        foreach (var row in root.GetProperty("duration").EnumerateArray())
            Check("Swift recorded match duration", () => Equal(row.GetProperty("result").GetString(), MatchFacts.Duration(row.GetProperty("seconds").GetDouble())));
        foreach (var row in root.GetProperty("contrast").EnumerateArray())
            Check("Swift artwork contrast", () =>
            {
                Equal(true, Math.Abs(row.GetProperty("dark").GetDouble() - ScrimMath.DarkScrim(row.GetProperty("high").GetDouble(), 0.9)) < 1e-12);
                Equal(true, Math.Abs(row.GetProperty("light").GetDouble() - ScrimMath.LightWash(row.GetProperty("low").GetDouble(), 0.02, 0.95)) < 1e-12);
            });

        Check("Swift pointer button learning precludes duplicates and keeps extras", () =>
        {
            var learner = new PointerButtonLearning();
            foreach (var row in root.GetProperty("input").EnumerateArray())
            {
                int button = row.GetProperty("button").GetInt32();
                if (button == -1) learner.Skip();
                else learner.Click(button);
                string phase = learner.Position is { } position ? Camel(position.ToString()) : learner.IsDone ? "done" : "extras";
                Equal(row.GetProperty("position").GetString(), phase);
                Equal(row.GetProperty("note").GetString(), learner.Note);
            }
            Sequence(new[] { 9 }, learner.Result!.Extras);
        });
        Check("Swift Studio session recovers exact active selection after restart", () =>
        {
            var row = root.GetProperty("session");
            var files = new FixtureProfileFiles();
            var store = new ConfigProfileStore(files, ConfigDialect.Cod2x);
            string main = row.GetProperty("main").GetString()!, next = row.GetProperty("launch").GetString()!;
            _ = store.Create(main, main, new("seta sensitivity 2\n"u8, ConfigDialect.Cod2x));
            _ = store.Create(next, next, new("seta sensitivity 5\n"u8, ConfigDialect.Cod2x));
            files.Files[ActivePath] = [.. row.GetProperty("originalSelection").GetBytesFromBase64()];
            bool running = false;
            var clock = new FixedClock();
            var identity = new GameProcessIdentity(12345, clock.GetUtcNow());
            var studio = new ConfigStudioSession(files, ConfigDialect.Cod2x, () => running, clock);
            Equal(next, studio.PrepareLaunch(connectionProfileId: next)!.ProfileId);
            Sequence(row.GetProperty("temporary").GetBytesFromBase64(), files.Files[ActivePath]);
            studio.CommitLaunch(identity);
            running = true;
            var restarted = new ConfigStudioSession(files, ConfigDialect.Cod2x, () => running, clock);
            restarted.ReconcileGames([identity]);
            Equal(row.GetProperty("reserved").GetBoolean(), restarted.IsReadOnly);
            running = false;
            restarted.ReconcileGames([]);
            Sequence(row.GetProperty("restored").GetBytesFromBase64(), files.Files[ActivePath]);
        });
    }

    private static void RejectData(Action read)
    {
        try { read(); }
        catch (InvalidDataException) { return; }
        throw new Exception("Invalid data was accepted.");
    }
}

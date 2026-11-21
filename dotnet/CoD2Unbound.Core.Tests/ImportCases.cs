using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace CoD2Unbound.Core.Tests;

internal static partial class Program
{
    private static readonly FixtureImportCatalog ImportCatalog = new(new()
    {
        ["sensitivity"] = new(Archived: true), ["com_maxfps"] = new(Archived: true),
        ["cg_fov"] = new(CheatProtected: true), ["r_fullbright"] = new(CheatProtected: true),
        ["name"] = new(Archived: true), ["cl_allowdownload"] = new(Archived: true),
        ["foreign_fixture_setting"] = new(Inert: true),
        ["protected_foreign_fixture"] = new(CheatProtected: true, Inert: true)
    }, ["+attack", "-attack", "+forward", "weapnext"]);
    private static readonly FixtureImportCatalog PersistenceCatalog = new(new() { ["motd"] = new(Archived: true) }, ["+attack"]);

    private static void ImportCases()
    {
        using var fixture = Fixtures("import");
        foreach (var (row, index) in fixture.RootElement.GetProperty("review").EnumerateArray().Select((row, i) => (row, i)))
        {
            var dialect = row.String("dialect") == "stock" ? ConfigDialect.Stock : ConfigDialect.Cod2x;
            var catalog = row.String("catalog") == "import" ? ImportCatalog : PersistenceCatalog;
            Check($"import review {index} ({dialect})", () =>
            {
                var files = row.GetProperty("files").EnumerateArray()
                    .Select(file => new ConfigImportFile(file.String("path")!, new(file.Bytes("bytes"), dialect))).ToImmutableArray();
                var review = new ConfigImportSafety(catalog).Review(new(new(ConfigImportSourceKind.Paste),
                    row.String("entry")!, files, []));
                var expected = row.GetProperty("candidates");
                Equal(expected.GetArrayLength(), review.Candidates.Length);
                for (int i = 0; i < review.Candidates.Length; i++)
                {
                    try { ImportCandidateEqual(expected[i], review.Candidates[i]); }
                    catch (InvalidDataException error) { throw new InvalidDataException($"Candidate {i}. {error.Message}", error); }
                }
            });
        }

        Check("signed profile uses paste-only dependency discovery", () =>
        {
            using var signedFixture = Fixtures("directory");
            var root = signedFixture.RootElement;
            var index = new Uri(root.String("indexURL")!);
            var signature = new Uri(root.String("signatureURL")!);
            var trust = new ConfigDirectoryConfiguration(index, signature, ImmutableArray.Create(root.Bytes("publicKey")));
            var data = root.GetProperty("cases")[0];
            var fetcher = new DirectoryFetcher();
            fetcher.Responses[index] = new(ImmutableArray.Create(data.Bytes("index")), index, null);
            fetcher.Responses[signature] = new(ImmutableArray.Create(data.Bytes("signature")), signature, null);
            var directory = new ConfigDirectory(trust, new DirectoryStorage(), fetcher);
            var snapshot = Load(directory).Snapshot!;
            var entry = snapshot.Index.Entries.Single();
            fetcher.Responses[entry.ConfigUrl] = new(ImmutableArray.Create(root.Bytes("profile")), entry.ConfigUrl, "text/plain");
            var bundle = Sync(directory.LoadProfileAsync(entry.Id, snapshot, ConfigDialect.Cod2x, ImportCatalog));
            Equal(entry.SourceUrl, bundle.Source.OriginalUrl);
            Equal(entry.ConfigUrl, bundle.Source.FetchedUrl);
            Equal(1, bundle.Files.Length);
            Equal("extra.cfg", bundle.Unresolved.Single().RequestedName);
            var review = new ConfigImportSafety(ImportCatalog).Review(bundle);
            Equal(true, review.Candidates.Any(candidate => candidate.Reasons.Contains(ConfigImportRisk.Credential) && !candidate.Importable));
            Equal(true, review.Candidates.Any(candidate => candidate.Command.Kind == ConfigCommandKind.Exec && !candidate.Importable));
        });

        Check("bounded local config input and dependencies", () =>
        {
            var root = new Uri("file:///fixture/selected/");
            var source = new FixtureImportFiles(root);
            source.Files["main.cfg"] = "exec sub/extra\nbind j +attack";
            source.Files["sub/extra.cfg"] = "seta sensitivity 2.1";
            var loader = new ConfigImportLoader(ImportCatalog, source);
            var file = Sync(loader.LoadAsync(new ConfigImportInput.File(new Uri(root, "main.cfg")), ConfigDialect.Stock));
            Equal(ConfigImportSourceKind.File, file.Source.Kind);
            Sequence(new[] { "main.cfg", "sub/extra.cfg" }, file.Files.Select(item => item.Path));
            var folder = Sync(loader.LoadAsync(new ConfigImportInput.Folder(root, "main.cfg"), ConfigDialect.Stock));
            Equal(ConfigImportSourceKind.Folder, folder.Source.Kind);
            Equal(2, folder.Files.Length);

            source.Unsafe.Add("escape.cfg");
            source.Files["main.cfg"] = "exec escape; exec ../outside; exec sub/extra";
            var bounded = Sync(loader.LoadAsync(new ConfigImportInput.File(new Uri(root, "main.cfg")), ConfigDialect.Stock));
            Equal(2, bounded.Files.Length);
            Equal(2, bounded.Unresolved.Length);
            Equal(true, bounded.Unresolved.All(item => item.Failure == ConfigImportFailure.OutsideRoot));
            source.Files["loop.cfg"] = "exec loop";
            Equal(1, Sync(loader.LoadAsync(new ConfigImportInput.File(new Uri(root, "loop.cfg")), ConfigDialect.Stock)).Files.Length);

            source.Files["main.cfg"] = "exec sub/extra";
            var countBound = new ConfigImportLoader(ImportCatalog, source, limits: new(MaximumFiles: 1));
            ImportRejects(ConfigImportFailure.Limits, () => Sync(countBound.LoadAsync(new ConfigImportInput.File(new Uri(root, "main.cfg")), ConfigDialect.Stock)));
            var sizeBound = new ConfigImportLoader(ImportCatalog, source, limits: new(MaximumTotalBytes: 20));
            ImportRejects(ConfigImportFailure.Limits, () => Sync(sizeBound.LoadAsync(new ConfigImportInput.File(new Uri(root, "main.cfg")), ConfigDialect.Stock)));
        });

        Check("paste and drop accept legacy text and decline binary, markup and oversized input", () =>
        {
            var loader = new ConfigImportLoader(ImportCatalog);
            foreach (var input in new ConfigImportInput[] { new ConfigImportInput.Paste(ImportBytes("seta sensitivity 2")), new ConfigImportInput.Drop(ImportBytes("seta sensitivity 2")) })
                Equal(1, Sync(loader.LoadAsync(input, ConfigDialect.Stock)).Files.Length);

            var tooLarge = ImmutableArray.Create(new byte[65_536]);
            ImportRejects(ConfigImportFailure.TooLarge, () => Sync(loader.LoadAsync(new ConfigImportInput.Paste(tooLarge), ConfigDialect.Stock)));
            foreach (var bytes in new ImmutableArray<byte>[]
            {
                [0, 1, 2], ImportBytes("<!doctype html><html>bad</html>"),
                [0xef, 0xbb, 0xbf, .. ImportBytes("<html>bad</html>")], [80, 75, 3, 4],
                ImportBytes("%PDF-1.7"), ImportBytes("MZ")
            })
                ImportRejects(ConfigImportFailure.NonText, () => Sync(loader.LoadAsync(new ConfigImportInput.Paste(bytes), ConfigDialect.Stock)));
        });

        Check("remote config roots, redirects and URL credentials", () =>
        {
            var fetcher = new DirectoryFetcher();
            var loader = new ConfigImportLoader(ImportCatalog, fetcher: fetcher);
            var entry = new Uri("https://example.invalid/config/main.cfg");
            var dependency = new Uri("https://example.invalid/config/sub/extra.cfg");
            var query = new Uri(entry.AbsoluteUri + "?download=1");
            var request = new ConfigFetchRequest(entry, 65_535, new(entry, "."));
            foreach (string candidate in new[] { "../other.cfg", "%252e%252e/evil.cfg", "%2e/evil.cfg",
                "https://example.invalid/config-more/evil.cfg", "http://example.invalid/config/main.cfg", "https://other.invalid/config/main.cfg" })
                Equal(false, request.Allows(new Uri(entry, candidate)));
            fetcher.Responses[entry] = new(ImportBytes("exec sub/extra"), entry, "text/plain");
            fetcher.Responses[query] = fetcher.Responses[entry] with { FinalUrl = query };
            fetcher.Responses[dependency] = new(ImportBytes("quit"), dependency, "text/plain");
            Equal(ConfigImportSourceKind.Https, Sync(loader.LoadAsync(new ConfigImportInput.Remote(entry), ConfigDialect.Stock)).Source.Kind);
            var queried = Sync(loader.LoadAsync(new ConfigImportInput.Remote(query), ConfigDialect.Stock));
            Equal(2, queried.Files.Length);
            Equal("", queried.Source.RootUrl!.Query);
            foreach (string blocked in new[] { entry.AbsoluteUri + "?token=secret", entry.AbsoluteUri + "?%73ig=secret",
                "http://example.invalid/config.cfg", "file:///private/config.cfg", "https://user:password@example.invalid/config.cfg" })
                ImportRejects(ConfigImportFailure.InvalidUrl, () => Sync(loader.LoadAsync(new ConfigImportInput.Remote(new(blocked)), ConfigDialect.Stock)));

            foreach (string loopback in new[] { "http://localhost:8080/main.cfg", "http://127.0.0.1:8080/main.cfg" })
            {
                var url = new Uri(loopback);
                fetcher.Responses[url] = new(ImportBytes("quit"), url, "text/plain");
                Equal(ConfigImportSourceKind.Localhost, Sync(loader.LoadAsync(new ConfigImportInput.Remote(url), ConfigDialect.Stock)).Source.Kind);
            }

            fetcher.Responses[entry] = new(ImportBytes("quit"), new Uri(entry, "../other/evil.cfg"), "text/plain");
            ImportRejects(ConfigImportFailure.OutsideRoot, () => Sync(loader.LoadAsync(new ConfigImportInput.Remote(entry), ConfigDialect.Stock)));
            fetcher.Responses[entry] = new(ImportBytes("quit"), entry, "text/html");
            ImportRejects(ConfigImportFailure.NonText, () => Sync(loader.LoadAsync(new ConfigImportInput.Remote(entry), ConfigDialect.Stock)));

            var github = new Uri("https://github.com/test/configs/blob/main/main.cfg");
            var raw = new Uri("https://raw.githubusercontent.com/test/configs/main/main.cfg");
            fetcher.Responses[raw] = new(ImportBytes("quit"), raw, "text/plain");
            Equal(ConfigImportSourceKind.Github, Sync(loader.LoadAsync(new ConfigImportInput.Remote(github), ConfigDialect.Stock)).Source.Kind);
            var gist = new Uri("https://gist.github.com/test/abc123");
            var gistRaw = new Uri("https://gist.githubusercontent.com/test/abc123/raw");
            var gistFinal = new Uri("https://gist.githubusercontent.com/test/abc123/raw/revision/main.cfg");
            fetcher.Responses[gistRaw] = new(ImportBytes("quit"), gistFinal, "text/plain");
            var fromGist = Sync(loader.LoadAsync(new ConfigImportInput.Remote(gist), ConfigDialect.Stock)).Source;
            Equal(ConfigImportSourceKind.Gist, fromGist.Kind);
            Equal(gistFinal, fromGist.FetchedUrl);
        });

        Check("cleaned metadata uses the last retained assignment", () =>
        {
            var config = ImportBytes("seta sensitivity 2\nseta sensitivity \"https://example.invalid/a?token=sample-secret\"\n");
            var result = Sync(ConfigClean.CleanAsync(config, ImportCatalog));
            Equal("2", result.Settings["sensitivity"]);
            Equal(true, result.LeftOut.Single().Reasons.Contains("credential"));
        });

        foreach (var row in fixture.RootElement.GetProperty("clean").EnumerateArray())
            Check($"clean config {row.String("id")}", () => CleanEqual(row));
    }

    private static void ImportCandidateEqual(JsonElement expected, ConfigImportCandidate actual)
    {
        ImportLocationEqual(expected.GetProperty("location"), actual.Location);
        Equal(expected.Int("classification"), (int)actual.Classification);
        Equal(expected.GetProperty("importable").GetBoolean(), actual.Importable);
        Equal(expected.GetProperty("default").GetBoolean(), actual.SelectedByDefault);
        Equal(expected.String("details"), actual.Details);
        ImportRisksEqual(expected, actual.Reasons);
        var effects = expected.GetProperty("effects");
        Equal(effects.GetArrayLength(), actual.Effects.Length);
        for (int i = 0; i < actual.Effects.Length; i++)
        {
            var effect = actual.Effects[i];
            ImportLocationEqual(effects[i].GetProperty("location"), effect.Location);
            Equal(effects[i].Int("classification"), (int)effect.Classification);
            Equal(effects[i].String("details"), effect.Details);
            ImportRisksEqual(effects[i], effect.Reasons);
        }

        var deps = expected.GetProperty("dependencies");
        Equal(deps.GetArrayLength(), actual.Dependencies.Length);
        for (int i = 0; i < actual.Dependencies.Length; i++)
        {
            var dep = actual.Dependencies[i];
            Equal(deps[i].String("kind"), dep.Kind == ConfigImportDependencyKind.Exec ? "exec" : "vstr");
            Equal(deps[i].String("name"), dep.Name);
            Equal(deps[i].GetProperty("resolved").GetBoolean(), dep.Resolved);
            var locations = deps[i].GetProperty("locations");
            Equal(locations.GetArrayLength(), dep.Locations.Length);
            for (int j = 0; j < dep.Locations.Length; j++)
                ImportLocationEqual(locations[j], dep.Locations[j]);
        }
    }

    private static void ImportLocationEqual(JsonElement expected, ConfigImportLocation actual)
    {
        Equal(expected.String("file"), actual.File);
        Equal(expected.Int("start"), actual.Span.Start);
        Equal(expected.Int("end"), actual.Span.End);
        Sequence(expected.Strings("context"), actual.Context);
    }

    private static void ImportRisksEqual(JsonElement expected, IEnumerable<ConfigImportRisk> reasons) =>
        Equal(string.Join(", ", expected.Strings("reasons")), string.Join(", ", reasons.Select(reason => reason.Key())));

    private static void CleanEqual(JsonElement row)
    {
        var result = Sync(ConfigClean.CleanAsync(ImmutableArray.Create(row.Bytes("input")), new ConfigImportCatalogAdapter(ConfigCatalog.LoadBundled())));
        Sequence(row.Bytes("bytes"), result.Bytes);
        Equal(row.Int("settingLines"), result.SettingLines);
        Equal(row.Int("binds"), result.Binds);
        Equal(row.Int("kept"), result.Kept);
        Equal(row.Int("comments"), result.Comments);
        var notes = row.GetProperty("leftOut");
        Equal(notes.GetArrayLength(), result.LeftOut.Length);
        for (int i = 0; i < result.LeftOut.Length; i++)
        {
            var item = result.LeftOut[i];
            Equal(notes[i].Int("start"), item.Span.Start);
            Equal(notes[i].Int("end"), item.Span.End);
            Sequence(notes[i].Bytes("text"), item.Text);
            Sequence(notes[i].Strings("reasons"), item.Reasons);
            Sequence(notes[i].Strings("tokens"), item.Tokens);
        }
        Sequence(row.GetProperty("settings").EnumerateObject().Select(item => (item.Name, item.Value.GetString())).Order(),
            result.Settings.Select(item => (item.Key, (string?)item.Value)).Order());
        Sequence(row.GetProperty("reasons").EnumerateObject().Select(item => (item.Name, item.Value.GetInt32())).Order(),
            result.Reasons.Select(item => (item.Key, item.Value)).Order());
    }

    private static ImmutableArray<byte> ImportBytes(string text) => ImmutableArray.Create(Encoding.UTF8.GetBytes(text));

    private static void ImportRejects(ConfigImportFailure failure, Action operation)
    {
        try { operation(); }
        catch (ConfigImportException error) { Equal(failure, error.Failure); return; }
        throw new InvalidDataException($"Expected import failure {failure}.");
    }

    private sealed class FixtureImportCatalog(Dictionary<string, ConfigImportSetting> settings, string[] bindable) : IConfigImportCatalog
    {
        public ConfigImportSetting? Setting(string name, ConfigDialect dialect) => settings.GetValueOrDefault(name);
        public ConfigImportCommandInfo? Command(string name, ConfigDialect dialect) => bindable.Contains(name) ? new(true) : null;
    }

    private sealed class FixtureImportFiles(Uri root) : IConfigImportFiles
    {
        public Dictionary<string, string> Files { get; } = [];
        public HashSet<string> Unsafe { get; } = [];

        public ValueTask<ConfigImportLocalFile> ResolveFileAsync(Uri file, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ConfigImportLocalFile(root, file.Segments[^1]));

        public ValueTask<Uri> ResolveFolderAsync(Uri folder, CancellationToken cancellationToken) => ValueTask.FromResult(root);

        public ValueTask<ImmutableArray<byte>> ReadAsync(Uri selectedRoot, string relativePath, int maximumBytes, CancellationToken cancellationToken)
        {
            Equal(root, selectedRoot);
            if (Unsafe.Contains(relativePath))
                throw new ConfigImportException(ConfigImportFailure.OutsideRoot);
            if (!Files.TryGetValue(relativePath, out string? body))
                throw new ConfigImportException(ConfigImportFailure.MissingEntry);
            return ValueTask.FromResult(ImportBytes(body));
        }
    }
}

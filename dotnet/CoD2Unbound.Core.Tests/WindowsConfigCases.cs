using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CoD2Unbound.Core.Tests;

internal static partial class Program
{
    private static void WindowsConfigCases()
    {
        Check("Retail profiles are found in the current user's VirtualStore", () =>
        {
            const string local = "C:/Users/Player/AppData/Local";
            const string players = local + "/VirtualStore/Program Files (x86)/Activision/Call of Duty 2/main/players";
            var result = WindowsProfiles.Find(new ProfileTree(players), ["C:/"], [], localAppData: local);
            Equal("Tessa", result.Profiles[0].Name);
            Equal(true, result.Profiles[0].IsActive);
            Equal(players + "/Tessa/config_mp.cfg", result.Profiles[0].ConfigPath);
            Equal(4, result.Profiles.Length);
        });
        Check("VirtualStore covers a custom retail install named by the launcher", () =>
        {
            const string install = "C:/Program Files (x86)/Custom Publisher/Call of Duty 2";
            const string local = "C:/Users/Player/AppData/Local";
            string players = local + "/VirtualStore/" + install[3..] + "/main/players";
            var result = WindowsProfiles.Find(new ProfileTree(players), ["C:/"], [install], localAppData: local);
            Equal(players + "/Tessa/config_mp.cfg", result.Profiles[0].ConfigPath);
        });
        Check("Copy names use the first available case-insensitive suffix", () =>
            Equal("Copy of Main 3", LoadoutName.CopyOf("Main", ["copy of main", "Copy of Main 2"])));
        Check("Studio can focus a healthy profile while another profile needs journal recovery", () =>
        {
            var files = new FixtureProfileFiles();
            var studio = new ConfigStudioSession(files, ConfigDialect.Stock, () => false, TimeProvider.System);
            studio.Create("Main", "Main");
            studio.Select("Main");
            studio.Create("Other", "Other");
            studio.Focus("Main");
            files.FailPath = ConfigPath;
            var edit = studio.State.Current!.Document.SetSetting("sensitivity", "3");
            ProfileRejects(ConfigProfileFailure.CannotPublish, () => studio.Mutate("Main",
                store => store.Update("Main", edit, studio.State.Current!.Version, null, "Changed Mouse speed")));
            files.Files[ConfigPath] = ImportBytes("seta sensitivity \"5\"\n");
            studio.Refresh();
            Equal(null, studio.State.Current);
            Equal("Main", studio.State.ReadProblem?.ProfileId);
            studio.Focus("Other");
            Equal(null, studio.State.ReadProblem);
            Equal("Other", studio.State.Current?.Profile.Id);
            studio.Focus("Main");
            studio.InspectRecovery("Main");
            studio.KeepDiskVersion();
            Equal("5", studio.State.Current?.Document.EffectiveSetting("sensitivity"));
            Equal(false, files.Files.ContainsKey(JournalPath));
        });
        Check("A failed guided change can be discarded without publishing its staged journal", () =>
        {
            var files = new FixtureProfileFiles();
            var studio = new ConfigStudioSession(files, ConfigDialect.Stock, () => false, TimeProvider.System);
            studio.Create("Main", "Main");
            studio.Focus("Main");
            var edit = studio.State.Current!.Document.SetSetting("sensitivity", "3");
            studio.SetDraft(edit);
            files.FailPath = ConfigPath;
            ProfileRejects(ConfigProfileFailure.CannotPublish, () => studio.SaveSettingDraft("Changed Mouse speed"));
            studio.DiscardSettingDraft();
            Equal(null, studio.State.Current?.Document.EffectiveSetting("sensitivity"));
            Equal(null, studio.State.Draft);
            Equal(false, files.Files.ContainsKey(JournalPath));
        });
        Check("A failed guided change retries as one History snapshot", () =>
        {
            var files = new FixtureProfileFiles();
            var studio = new ConfigStudioSession(files, ConfigDialect.Stock, () => false, TimeProvider.System);
            studio.Create("Main", "Main");
            studio.Focus("Main");
            var edit = studio.State.Current!.Document.SetSetting("sensitivity", "3");
            studio.SetDraft(edit);
            files.FailPath = ConfigPath;
            ProfileRejects(ConfigProfileFailure.CannotPublish, () => studio.SaveSettingDraft("Changed Mouse speed"));
            studio.SaveSettingDraft("Changed Mouse speed");
            Equal("3", studio.State.Current?.Document.EffectiveSetting("sensitivity"));
            Equal(1, studio.State.History.Count(entry => entry.Summary == "Changed Mouse speed"));
        });
        Check("Remote import validates link forms and excludes credential file dependencies", () =>
        {
            Equal(true, ConfigImportLoader.AcceptsLink("https://github.com/player/setup/blob/main/config.cfg"));
            Equal(true, ConfigImportLoader.AcceptsLink("http://localhost:8000/config.cfg"));
            Equal(false, ConfigImportLoader.AcceptsLink("https://guest:password@example.test/config.cfg"));
            Equal(false, ConfigImportLoader.AcceptsLink("http://example.test/config.cfg"));
            Equal(false, ConfigImportLoader.AcceptsLink("https://github.com/player/setup"));
            Equal(false, ConfigImportLoader.AcceptsLink("https://example.test/cod2key"));
        });
        Check("Import details preserve exact lines, hide secrets and preserve other ticks when changing mode", () =>
        {
            var session = NewSession("seta sensitivity 4\nseta name New\nseta password sample-secret\nexec cod2key\n");
            session.SetTarget(new ConfigImportTarget.Existing(ProfileRead("seta   sensitivity   \"2\"\r\n", ["sensitivity"])));
            Equal("seta   sensitivity   \"2\"", session.Rows[0].PreviousLine);
            Equal("seta sensitivity 4", session.Rows[0].SourceLine);
            Equal(false, session.Rows[0].CanSelect);
            Equal(null, session.Rows[2].SourceLine);
            Equal(false, session.Rows[3].CanSelect);
            Equal(false, session.RequiresConfirmation);
            session.SetMode(ConfigImportMode.Everything);
            Equal(true, session.RequiresConfirmation);
            Equal(1, session.SelectedRiskCount);
            var risk = session.Rows[1];
            var expected = session.SelectedIds.Remove(risk.Id);
            session.Select(risk.Id, false);
            Equal(ConfigImportMode.Review, session.Mode);
            Equal(true, expected.SetEquals(session.SelectedIds));
            Equal(false, session.RequiresConfirmation);
        });

        Check("Import Undo restores existing bytes and recycles an untouched new loadout", () =>
        {
            var files = new FixtureProfileFiles();
            var trash = new FixtureTrash(files);
            var studio = new ConfigStudioSession(files, ConfigDialect.Cod2x, () => false, TimeProvider.System);
            studio.Create("Main", "Main");
            studio.Select("Main");
            studio.Focus("Main");
            var original = studio.State.Current!;
            var into = NewSession("seta sensitivity 4\n");
            into.SetTarget(new ConfigImportTarget.Existing(original));
            var edited = Sync(into.ApplyAsync(new ConfigProfileStore(files, ConfigDialect.Cod2x)));
            studio.UndoImport(edited, created: false, trash);
            Equal(true, studio.ReadProfile("Main").Version.Matches(original.Version));
            Equal(1, studio.State.History.Count(entry => entry.Source == ConfigHistorySource.Import));

            var separate = NewSession("seta sensitivity 5\n");
            var added = Sync(separate.ApplyAsync(new ConfigProfileStore(files, ConfigDialect.Cod2x)));
            studio.Focus(added.ProfileId);
            studio.Select(added.ProfileId);
            ProfileRejects(ConfigProfileFailure.InUse, () => studio.UndoImport(added, created: true, trash));
            studio.Select("Main");
            studio.UndoImport(added, created: true, trash);
            Equal("Main", studio.State.Current?.Profile.Id);
            Equal(true, studio.State.Current!.Profile.IsActive);
            Equal(1, studio.State.Profiles.Length);
        });

        Check("Config HTTP transport follows only contained redirects and sends no cookies", () =>
            ConfigLoopback().GetAwaiter().GetResult());

        Check("Loader leaves credential file dependencies unread", () =>
        {
            var root = new Uri("file:///fixture/selected/");
            var files = new FixtureImportFiles(root);
            files.Files["main.cfg"] = "exec cod2key.cfg";
            files.Files["cod2key.cfg"] = "seta sensitivity 2";
            var loader = new ConfigImportLoader(ImportCatalog, files);
            var bundle = Sync(loader.LoadAsync(new ConfigImportInput.File(new Uri(root, "main.cfg")), ConfigDialect.Stock));
            Equal(1, bundle.Files.Length);
            Equal(ConfigImportFailure.OutsideRoot, bundle.Unresolved.Single().Failure);
        });
    }

    private static async Task ConfigLoopback()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        listener.Start();
        var origin = new Uri($"http://localhost:{((IPEndPoint)listener.LocalEndpoint).Port}/");
        var root = new Uri(origin, "cfg/");
        var allowed = new Uri(root, "start.cfg");
        var fetch = new HttpConfigTextFetcher();
        var replies = ReplyAsync();
        var response = await fetch.FetchAsync(new(allowed, 37, root), timeout.Token);
        Equal(new Uri(root, "good.cfg"), response.FinalUrl);
        Equal("seta sensitivity 4", Encoding.ASCII.GetString(response.Data.AsSpan()));
        ImportRejects(ConfigImportFailure.OutsideRoot, () => Sync(fetch.FetchAsync(
            new(new Uri(root, "out.cfg"), 37, root), timeout.Token)));
        ImportRejects(ConfigImportFailure.TooLarge, () => Sync(fetch.FetchAsync(
            new(new Uri(root, "large.cfg"), 8, root), timeout.Token)));
        await replies;

        async Task ReplyAsync()
        {
            await SendAsync("/cfg/start.cfg", "302 Found", "Location: good.cfg\r\nSet-Cookie: private=fixture\r\n", "");
            await SendAsync("/cfg/good.cfg", "200 OK", "Content-Type: text/plain\r\n", "seta sensitivity 4");
            await SendAsync("/cfg/out.cfg", "302 Found", "Location: /outside.cfg\r\n", "");
            await SendAsync("/cfg/large.cfg", "200 OK", "Content-Type: text/plain\r\n", "seta sensitivity 4");
        }

        async Task SendAsync(string path, string status, string headers, string body)
        {
            using var remote = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = remote.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true);
            Equal($"GET {path} HTTP/1.1", await reader.ReadLineAsync(timeout.Token));
            while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 } header)
            {
                Equal(false, header.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase));
                Equal(false, header.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase));
            }
            var response = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\n{headers}Connection: close\r\nContent-Length: {body.Length}\r\n\r\n{body}");
            await stream.WriteAsync(response, timeout.Token);
        }
    }
}

using System.Text;
using CoD2Unbound.Core;

namespace CoD2Unbound.Core.Tests;

internal static partial class Program
{
    private static void SetupWorkflowCases()
    {
        using var fixture = Fixtures("setup");
        int index = 0;
        foreach (var row in fixture.RootElement.GetProperty("keys").EnumerateArray())
            Check($"Swift CD key case {++index}", () =>
            {
                string input = row.GetProperty("input").GetString()!;
                Equal(row.GetProperty("normalized").GetString(), CdKey.Normalize(input));
                Equal(row.GetProperty("display").GetString(), CdKey.Display(input));
                Equal(row.GetProperty("valid").GetBoolean(), CdKey.IsValid(input));
                var (state, left) = CdKey.Check(input);
                Equal(row.GetProperty("state").GetString(), state.ToString());
                Equal(row.GetProperty("left").GetInt32(), left);
            });
        foreach (var row in fixture.RootElement.GetProperty("preferences").EnumerateArray())
            Check("Swift initial data-folder preference", () =>
                Equal(row.GetProperty("expected").GetString(), SetupSession.InitialDataPath(
                    row.GetProperty("found").GetString(), row.GetProperty("remembered").GetString(),
                    row.GetProperty("complete").GetBoolean())));

        Check("Swift Windows profile discovery, active ordering and backups", () =>
        {
            const string root = "volume";
            const string players = "volume/Games/Call of Duty 2/main/players";
            var files = new ProfileTree(players);
            var windows = fixture.RootElement.GetProperty("windows");
            var result = WindowsProfiles.Find(files, [root], [players]);
            Sequence(windows.GetProperty("profiles").EnumerateArray().Select(row => (
                row.GetProperty("name").GetString()!, row.GetProperty("path").GetString()!,
                row.GetProperty("active").GetBoolean(), row.GetProperty("backup").GetBoolean())),
                result.Profiles.Select(row => (
                    row.Name, row.ConfigPath[(players.Length + 1)..], row.IsActive, row.IsBackup)));
            Equal(windows.GetProperty("truncated").GetBoolean(),
                WindowsProfiles.Find(files, [root], [], maximumEntries: 0).Truncated);
        });

        Check("Setup validates again on confirmation and rolls back only unconfirmed choices", () =>
        {
            var files = new ArchiveChoices();
            var storage = new MemoryPlatform("home");
            var secrets = new TestSecrets();
            var setup = new SetupSession(files, storage, secrets, "old", false, true, SetupStep.Data);
            Equal(SetupDataState.Damaged, setup.ChooseData("new").State);
            files.Complete = true;
            Equal(SetupDataState.Found, setup.ChooseData("new").State);
            files.Complete = false;
            Equal(false, setup.ContinueAsync().AsTask().GetAwaiter().GetResult());
            Equal(SetupStep.Data, setup.Step);
            files.Complete = true;
            setup.ChooseData("new");
            Equal(true, setup.ContinueAsync().AsTask().GetAwaiter().GetResult());
            Equal(SetupStep.Key, setup.Step);
            Equal(SetupDataState.Found, setup.ChooseData("old").State);
            Equal(true, setup.Cancel());
            Equal("new", setup.DataPath);
            var saved = storage.ReadAsync(SetupSession.DataPathFile, default).AsTask().GetAwaiter().GetResult();
            Equal("new\n", Encoding.UTF8.GetString(saved!.Value.AsSpan()));
        });

        Check("Setup distinguishes explicitly picking a folder from accepting the same discovered folder", () =>
        {
            var setup = new SetupSession(new ArchiveChoices(), new MemoryPlatform("home"), new TestSecrets(),
                "old", false, step: SetupStep.Data);
            Equal(true, setup.ContinueAsync().AsTask().GetAwaiter().GetResult());
            Equal(false, setup.DataWasPicked);

            setup.Back();
            Equal(SetupDataState.Found, setup.ChooseData("old", picked: true).State);
            Equal(true, setup.ContinueAsync().AsTask().GetAwaiter().GetResult());
            Equal(true, setup.DataWasPicked);

            setup.Back();
            setup.ChooseData("old");
            Equal(true, setup.ContinueAsync().AsTask().GetAwaiter().GetResult());
            Equal(false, setup.DataWasPicked);
        });

        Check("Canceling the Windows wizard discards confirmed folder and key drafts", () =>
        {
            var storage = new MemoryPlatform("home");
            var secrets = new TestSecrets();
            var setup = new SetupSession(new ArchiveChoices { Complete = true }, storage, secrets,
                "old", false, canCancel: true, step: SetupStep.Data, hasReportService: true,
                hasUsageService: true, deferWrites: true);
            setup.ChooseData("new");
            Equal(true, setup.ContinueAsync().AsTask().GetAwaiter().GetResult());
            Equal(true, setup.ContinueAsync("000000000000000086D3").AsTask().GetAwaiter().GetResult());
            Equal("86D3", setup.PendingKeyTail);
            Equal(true, setup.ReviewPersonalization(new("^2Morrow", "windowed")));
            setup.ReviewPrivacy(true);
            setup.ReviewPrivacy(false);
            Equal(true, setup.CrashChoice);
            Equal(false, setup.UsageChoice);
            Equal(true, setup.Cancel());
            Equal<SetupPersonalization?>(null, setup.Personalization);
            Equal<bool?>(null, setup.CrashChoice);
            Equal<bool?>(null, setup.UsageChoice);
            Equal("old", setup.DataPath);
            Equal(false, setup.HasStoredKey);
            Equal<string?>(null, secrets.Saved);
            Equal(false, storage.ReadAsync(SetupSession.DataPathFile, default).AsTask().GetAwaiter().GetResult().HasValue);
        });

        Check("Finishing the Windows wizard validates again and writes reviewed choices", () =>
        {
            var files = new ArchiveChoices { Complete = true };
            var storage = new MemoryPlatform("home");
            var secrets = new TestSecrets();
            var setup = new SetupSession(files, storage, secrets, "old", false, step: SetupStep.Data, deferWrites: true);
            setup.ChooseData("new");
            setup.ContinueAsync().AsTask().GetAwaiter().GetResult();
            setup.SkipKey();
            setup.PersonalizationSaved();
            files.Complete = false;
            Equal(false, setup.ContinueAsync().AsTask().GetAwaiter().GetResult());
            Equal(SetupStep.Data, setup.Step);
            files.Complete = true;
            setup.ChooseData("new");
            setup.ContinueAsync().AsTask().GetAwaiter().GetResult();
            setup.SkipKey();
            setup.PersonalizationSaved();
            Equal(true, setup.ContinueAsync().AsTask().GetAwaiter().GetResult());
            Equal(SetupStep.Complete, setup.Step);
            Equal("new\n", Encoding.UTF8.GetString(storage.ReadAsync(SetupSession.DataPathFile, default)
                .AsTask().GetAwaiter().GetResult()!.Value.AsSpan()));
        });

        Check("The reviewed CD key and choices are applied only after archive validation", () =>
        {
            var storage = new MemoryPlatform("home");
            var secrets = new TestSecrets();
            var files = new ArchiveChoices { Complete = true };
            var setup = new SetupSession(files, storage, secrets, "new", false,
                canCancel: true, step: SetupStep.Data, deferWrites: true);
            Sync(setup.ContinueAsync());
            Sync(setup.ContinueAsync("000000000000000086D3"));
            setup.PersonalizationSaved();
            Equal<string?>(null, secrets.Saved);
            int applied = 0;
            files.Complete = false;
            Equal(false, Sync(setup.ContinueAsync(applyReview: Apply)));
            Equal(0, applied);
            Equal(true, setup.CanCancel);
            files.Complete = true;
            setup.ChooseData("new");
            Sync(setup.ContinueAsync());
            Sync(setup.ContinueAsync());
            setup.PersonalizationSaved();
            Equal(true, Sync(setup.ContinueAsync(applyReview: Apply)));
            Equal(1, applied);
            Equal("86D3", SetupSession.KeyTail(secrets.Saved));

            ValueTask Apply()
            {
                applied++;
                return ValueTask.CompletedTask;
            }
        });

        Check("A player card edit preserves other settings and does not repeat an unchanged write", () =>
        {
            var storage = new FixtureProfileFiles();
            bool running = false;
            var store = new ConfigProfileStore(storage, ConfigDialect.Stock, () => running);
            store.Create("Main", "Main", new("seta name Soldier\r\nseta sensitivity 3\r\n"u8, ConfigDialect.Stock));
            store.Select("Main", null);
            store.SavePlayerName("Main", "^3Sgt. ^7Morrow");
            store.SavePlayerName("Main", "^3Sgt. ^7Morrow");
            Equal("^3Sgt. ^7Morrow", store.Read("Main").Document.EffectiveSetting("name"));
            Equal("3", store.Read("Main").Document.EffectiveSetting("sensitivity"));
            Equal(2, store.History("Main").Length);
            running = true;
            ProfileRejects(ConfigProfileFailure.GameRunning, () => store.SavePlayerName("Main", "While playing"));
        });

        Check("Steam library discovery supports current and legacy quoted VDF paths", () =>
        {
            const string vdf = """
                "libraryfolders"
                {
                    "0"
                    {
                        "path"        "C:\\Program Files (x86)\\Steam"
                        "apps" { "2630" "3456" }
                    }
                    "1"           "D:\\Other Library"
                }
                """;
            Sequence(new[] { @"C:\Program Files (x86)\Steam", @"D:\Other Library" }, SetupDiscovery.SteamLibraryPaths(vdf));
            Equal("Call of Duty 2", SetupDiscovery.SteamInstallDirectory(""" "installdir" "Call of Duty 2" """));
            Equal<string?>(null, SetupDiscovery.SteamInstallDirectory(""" "installdir" "../Call of Duty 2" """));
        });

        Check("Setup discovery keeps unique complete copies with their source", () =>
        {
            var files = new ArchiveChoices { Complete = true };
            var known = SetupDiscovery.Complete(files, "OLD",
                [new("old", [], GameInstallSource.Disc), new("new", [], GameInstallSource.Steam),
                 new("NEW", [], GameInstallSource.Disc), new("partial", [], GameInstallSource.Steam)]);
            Sequence(new[] { ("old", GameInstallSource.Disc), ("new", GameInstallSource.Steam) },
                known.Select(copy => (copy.DataPath, copy.Source)));
            Equal(@"C:\Users\Player\AppData\Local\VirtualStore\Program Files (x86)\Activision\Call of Duty 2",
                SetupDiscovery.VirtualStoreCopy(@"C:\Users\Player\AppData\Local", @"C:\Program Files (x86)\Activision\Call of Duty 2"));
            Equal<string?>(null, SetupDiscovery.VirtualStoreCopy(@"C:\Local", @"\\server\share\Call of Duty 2"));
        });

        Check("Steam watch resurvey advances only on a complete archive set", () =>
        {
            var files = new ArchiveChoices();
            int attempts = 0;
            var changed = SetupDiscovery.WaitForSteamAsync(files, _ =>
            {
                files.Complete = ++attempts == 2;
                return ValueTask.FromResult<System.Collections.Immutable.ImmutableArray<GameInstallation>>(
                    [new("old", [], GameInstallSource.Disc), new("new", [], GameInstallSource.Steam)]);
            }, TimeSpan.Zero, default).GetAwaiter().GetResult();
            Equal(2, attempts);
            Equal("new", changed.DataPath);
        });

        Check("Player name validation and recovery use portable launcher rules", () =>
        {
            Equal<string?>(null, SetupPlayer.NameProblem("^2Ranger"));
            Equal(true, SetupPlayer.NameProblem("Bad;name") is not null);
            Equal(LaunchRecoveryKind.Folder, LaunchRecovery.Classify("old", false, false, "error").Kind);
            Equal(LaunchRecoveryKind.MissingGame, LaunchRecovery.Classify("old", true, false, "error").Kind);
            Equal("error", LaunchRecovery.Classify("old", true, true, "error").Message);
        });

        Check("Replacing a CD key preserves the stored key until confirmation", () =>
        {
            var storage = new MemoryPlatform("home");
            var secrets = new TestSecrets();
            var setup = new SetupSession(new ArchiveChoices { Complete = true }, storage, secrets,
                "old", true, true, SetupStep.Key);
            Equal(true, setup.KeyReady(""));
            setup.ReplaceKey();
            Equal(false, setup.KeyReady(""));
            Equal(false, setup.ContinueAsync("bad").AsTask().GetAwaiter().GetResult());
            Equal<string?>(null, secrets.Saved);
            Equal(true, setup.ContinueAsync("0000-0000-0000-0000-86d3").AsTask().GetAwaiter().GetResult());
            Equal("86D3", SetupSession.KeyTail(secrets.Saved));
            Equal(SetupStep.Personalize, setup.Step);
        });
    }

    private sealed class ArchiveChoices : IGameFiles
    {
        public bool Complete { get; set; }
        public bool IsRegularFile(string root, string path) => root == "old" || (root == "new" && Complete);
    }

    private sealed class TestSecrets : ISecretStore
    {
        public string? Saved { get; private set; }
        public ValueTask<string?> ReadAsync(string name, CancellationToken cancellationToken) => ValueTask.FromResult(Saved);
        public ValueTask WriteAsync(string name, string? value, CancellationToken cancellationToken)
        {
            Saved = value;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ProfileTree(string players) : IWindowsProfileFiles
    {
        private readonly string[] paths = [
            "Tessa/config_mp.cfg", "taylor/backup/config_mp.cfg", "taylor/config_mp.cfg",
            "Tessa/Raptr/config_mp-old.cfg.bak", "Tessa/ignored.cfg", "taylor/other/config_mp.cfg",
            "Tessa/config_sp.cfg", "active.txt"
        ];

        public bool IsDirectory(string root, string path) =>
            paths.Any(file => (players + "/" + file).StartsWith(path + "/", StringComparison.Ordinal));
        public bool IsRegularFile(string root, string path) => paths.Any(file => players + "/" + file == path);
        public IEnumerable<string> EnumerateNames(string root, string path) => paths
            .Select(file => players + "/" + file).Where(file => file.StartsWith(path + "/", StringComparison.Ordinal))
            .Select(file => file[(path.Length + 1)..].Split('/')[0]).Distinct(StringComparer.Ordinal);
        public byte[]? ReadPrefix(string root, string path, int maximumBytes) =>
            path == players + "/active.txt" ? "Tessa\n"u8.ToArray() : null;
    }
}

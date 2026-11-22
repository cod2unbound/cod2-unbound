using System.Collections.Immutable;

namespace CoD2Unbound.Core.Tests;

internal static partial class Program
{
    private const string MainProfile = "main/players/Main/";
    private const string JournalPath = MainProfile + ".studio-transaction.json";
    private const string ConfigPath = MainProfile + "config_mp.cfg";
    private const string MetadataPath = MainProfile + "studio.json";
    private const string ActivePath = "main/players/active.txt";

    private static void ProfileCases()
    {
        using var fixture = Fixtures("profiles");
        var row = fixture.RootElement;
        Check("loadout choices expose the requested startup resolution", () =>
        {
            var home = new MemoryPlatform("/game-home");
            home.WriteAsync(ActivePath, ImportBytes("Main"), default).GetAwaiter().GetResult();
            home.WriteAsync(ConfigPath, ImportBytes("seta r_mode 640x480\nseta r_mode \"1440x900\"\n"), default).GetAwaiter().GetResult();
            home.WriteAsync("main/players/Default/config_mp.cfg", ImportBytes("seta sensitivity 2\n"), default).GetAwaiter().GetResult();

            var choices = Sync(ConfigProfileStore.ReadChoicesAsync(home, ["Main", "Default"]));
            Equal("1440x900", choices.Single(choice => choice.IsActive).RequestedResolution);
            Equal(null, choices.Single(choice => choice.Id == "Default").RequestedResolution);
        });

        Check("Swift journal recovers a mixed config, metadata and companion", () =>
        {
            var storage = SwiftInterrupted();
            var store = new ConfigProfileStore(storage, ConfigDialect.Cod2x);
            var recovered = store.Read("Main");
            Equal(true, recovered.Profile.IsActive);
            Equal("Changed", recovered.Metadata.DisplayName);
            Equal(1200.0, recovered.Metadata.Dpi);
            Sequence(new[] { "sensitivity" }, recovered.Metadata.Locks);
            Sequence(new byte[] { 0, 255 }, recovered.Metadata.ChainDefinitions!.Value);
            Sequence(row.Bytes("afterConfig"), recovered.Version.Config);
            Sequence(row.Bytes("afterMetadata"), recovered.Version.Metadata!.Value);
            Sequence(row.Bytes("afterChain"), recovered.Version.Chain!.Value);
            Equal(false, storage.Files.ContainsKey(JournalPath));
            var snapshot = store.History("Main").Single();
            Equal(row.String("id"), snapshot.Id);
            Equal(new DateTimeOffset(2001, 1, 1, 0, 2, 3, TimeSpan.Zero).AddMilliseconds(250), snapshot.Date);
            var result = store.Restore("Main", snapshot.Id, recovered.Version, ConfigSnapshotRevision.Before);
            Sequence(row.Bytes("restoredConfig"), result.After.Config);
            Sequence(row.Bytes("restoredMetadata"), result.After.Metadata!.Value);
            Equal(null, result.After.Chain);
            Equal(2, store.History("Main").Length);
        });

        Check("external conflict preserves bytes and exact recovery receipt", () =>
        {
            var storage = SwiftInterrupted();
            var store = new ConfigProfileStore(storage, ConfigDialect.Cod2x);
            storage.Files[ConfigPath] = ImportBytes("seta sensitivity 99\n");
            ProfileRejects(ConfigProfileFailure.Conflict, () => store.Read("Main"));
            var receipt = store.InspectRecovery("Main");
            storage.Files[ConfigPath] = ImportBytes("seta sensitivity 100\n");
            ProfileRejects(ConfigProfileFailure.Conflict, () => store.KeepDiskVersion(receipt));
            Equal(true, storage.Files.ContainsKey(JournalPath));
            store.KeepDiskVersion(store.InspectRecovery("Main"));
            Equal("100", store.Read("Main").Document.EffectiveSetting("sensitivity"));
            Equal(false, storage.Files.ContainsKey(JournalPath));
            Equal(0, store.History("Main").Length);
            Sequence(row.Bytes("journal"), storage.Files[MainProfile + "recovery/" + row.String("id") + ".json"]);
        });

        Check("profile store commits reviewed imports and recovers interrupted updates", () =>
        {
            var storage = new FixtureProfileFiles();
            var store = new ConfigProfileStore(storage, ConfigDialect.Cod2x);
            var first = store.Create("Main", "Main", new(row.Bytes("beforeConfig"), ConfigDialect.Cod2x));
            store.Select("Main", null);
            var session = NewSession("seta sensitivity 9\n");
            session.SetTarget(new ConfigImportTarget.Existing(first));
            var imported = Sync(session.ApplyAsync(store));
            Equal(ConfigHistorySource.Import, imported.HistoryEntry.Source);
            Equal("9", store.Read("Main").Document.EffectiveSetting("sensitivity"));
            Equal("Main", store.ActiveSelection().ProfileId);
            ProfileRejects(ConfigProfileFailure.Conflict, () => store.Select("Main", null));

            var current = store.Read("Main");
            storage.FailPath = MetadataPath;
            var newDocument = current.Document.SetSetting("sensitivity", "7");
            ProfileRejects(ConfigProfileFailure.CannotPublish,
                () => store.Update("Main", newDocument, current.Version, current.Metadata with { DisplayName = "New" }, "Change"));
            Equal(true, storage.Files.ContainsKey(JournalPath));
            var recovered = new ConfigProfileStore(storage, ConfigDialect.Cod2x).Read("Main");
            Equal("7", recovered.Document.EffectiveSetting("sensitivity"));
            Equal("New", recovered.Metadata.DisplayName);
            Equal(false, storage.Files.ContainsKey(JournalPath));
            Equal(3, store.History("Main").Length);
            ProfileRejects(ConfigProfileFailure.Conflict, () => store.Update("Main", current.Document, current.Version, null, "Stale"));
        });

        Check("interrupted reviewed import returns the same receipt on retry", () =>
        {
            var storage = new FixtureProfileFiles();
            var store = new ConfigProfileStore(storage, ConfigDialect.Cod2x);
            var original = store.Create("Main", "Main", new("seta sensitivity 2\n"u8, ConfigDialect.Cod2x));
            var session = NewSession("seta sensitivity 4\n");
            session.SetTarget(new ConfigImportTarget.Existing(original));
            storage.FailPath = MetadataPath;
            ProfileRejects(ConfigProfileFailure.CannotPublish, () => Sync(session.ApplyAsync(store)));
            Equal(null, session.Receipt);
            Equal(true, storage.Files.ContainsKey(JournalPath));
            var retry = new ConfigProfileStore(storage, ConfigDialect.Cod2x);
            var receipt = Sync(session.ApplyAsync(retry));
            Equal(receipt, session.Receipt);
            Equal("4", retry.Read("Main").Document.EffectiveSetting("sensitivity"));
            Equal(1, retry.History("Main").Count(entry => entry.Source == ConfigHistorySource.Import));
        });

        Check("legacy profile restores and records game writes without rewriting config", () =>
        {
            var storage = new FixtureProfileFiles();
            storage.Files[ConfigPath] = ImportBytes("seta sensitivity 2\r\n");
            var store = new ConfigProfileStore(storage, ConfigDialect.Cod2x);
            var legacy = store.Read("Main");
            Equal(null, legacy.Version.Metadata);
            Equal("Main", legacy.Metadata.DisplayName);
            var newEntry = store.Update("Main", legacy.Document.SetSetting("sensitivity", "3"), legacy.Version, new("Renamed"), "Changed");
            store.Restore("Main", newEntry.Id, store.Read("Main").Version, ConfigSnapshotRevision.Before);
            Equal(false, storage.Files.ContainsKey(MetadataPath));
            var before = store.Read("Main").Version;
            storage.Files[ConfigPath] = ImportBytes("seta sensitivity 4\r\n");
            string sessionId = Guid.NewGuid().ToString();
            store.RecordGameSession("Main", before, sessionId: sessionId);
            store.RecordGameSession("Main", before, sessionId: sessionId);
            Equal(1, store.History("Main").Count(entry => entry.Source == ConfigHistorySource.GameSession));
            Sequence(ImportBytes("seta sensitivity 4\r\n"), storage.Files[ConfigPath]);
        });

        Check("companion copy can replay an interrupted exclusive publication", () =>
        {
            var storage = new FixtureProfileFiles();
            var store = new ConfigProfileStore(storage, ConfigDialect.Cod2x);
            var initial = store.Create("Main", "Main", new("seta sensitivity 1\n"u8, ConfigDialect.Cod2x));
            var chain = ImportBytes("set helper +attack\n");
            store.UpdateChain("Main", initial.Document, initial.Version, initial.Metadata, chain, "Chain");
            var main = store.Read("Main");
            string companion = "main/" + ConfigStudioLaunch.ChainBasename("Copy");
            storage.FailPath = companion;
            ProfileRejects(ConfigProfileFailure.CannotPublish, () => store.Duplicate("Main", "Copy", "Copy", main.Version));
            Equal(false, storage.DirectoryExists("main/players/Copy"));
            var copied = store.Duplicate("Main", "Copy", "Copy", main.Version);
            Sequence(chain, copied.Version.Chain!.Value);
            Equal(1, store.History("Copy").Length);
            Equal(false, storage.Files.Keys.Any(path => path.Contains(".studio-companion-", StringComparison.Ordinal)));
        });

        Check("profile layout separates active selection, journal, and companion", () =>
        {
            var storage = new FixtureProfileFiles();
            bool running = false;
            var store = new ConfigProfileStore(storage, ConfigDialect.Cod2x, gameIsRunning: () => running);
            var main = store.Create("Main", "Main", new(row.Bytes("beforeConfig"), ConfigDialect.Cod2x));
            var copied = store.Duplicate("Main", "Copied", "Copy", main.Version);
            Equal("Main", copied.Metadata.Origin.Source);
            Equal(ConfigOriginKind.Copied, copied.Metadata.Origin.Kind);
            Sequence(main.Version.Config, copied.Version.Config);
            ProfileRejects(ConfigProfileFailure.AlreadyExists, () => store.Create("main", "Other", main.Document));
            ProfileRejects(ConfigProfileFailure.InvalidIdentifier, () => store.Create("..", "Other", main.Document));
            Equal(2, store.List().Length);
            running = true;
            ProfileRejects(ConfigProfileFailure.GameRunning, () => store.Select("Copied", null));
            running = false;
            store.Select("Copied", null);
            Equal(true, store.Read("Copied").Profile.IsActive);
            Equal(false, store.Read("Main").Profile.IsActive);
            Equal("Configs are read-only while Call of Duty 2 is running.", LoadoutName.Problem("New", [], readOnly: true));
            Equal("Type a name for the loadout.", LoadoutName.Problem("   ", [], readOnly: false));
            Equal("You already have a loadout called “copy”.", LoadoutName.Problem(" copy ", ["Copy"], readOnly: false));
            Equal(null, LoadoutName.Problem("New", ["Copy"], readOnly: false));
        });

        FixtureProfileFiles SwiftInterrupted()
        {
            var storage = new FixtureProfileFiles();
            storage.Files[ConfigPath] = ImmutableArray.Create(row.Bytes("observedConfig"));
            storage.Files[MetadataPath] = ImmutableArray.Create(row.Bytes("beforeMetadata"));
            storage.Files[ActivePath] = ImportBytes("Main");
            storage.Files["main/" + ConfigStudioLaunch.ChainBasename("Main")] = ImmutableArray.Create(row.Bytes("afterChain"));
            storage.Files[JournalPath] = ImmutableArray.Create(row.Bytes("journal"));
            storage.Files[MainProfile + "history/" + row.String("id") + ".json"] = ImmutableArray.Create(row.Bytes("history"));
            return storage;
        }
    }

    private sealed class FixtureProfileFiles : IConfigProfileFiles
    {
        private readonly Lock gate = new();
        public Dictionary<string, ImmutableArray<byte>> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? FailPath { get; set; }

        public IDisposable Acquire(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            gate.Enter();
            return new ProfileLease(gate);
        }
        public ImmutableArray<byte>? Read(string path) => Files.TryGetValue(path, out var value) ? value : null;
        public void WriteAtomic(string path, ImmutableArray<byte> data)
        {
            if (path == FailPath)
            {
                FailPath = null;
                throw new ConfigProfileException(ConfigProfileFailure.CannotPublish);
            }
            Files[path] = data;
        }
        public void DeleteFile(string path) => Files.Remove(path);
        public bool DirectoryExists(string path) => Files.Keys.Any(key => key.StartsWith(path + '/', StringComparison.OrdinalIgnoreCase));
        public ImmutableArray<string> ListDirectories(string path) => Children(path).Where(name => name.Contains('/'))
            .Select(name => name.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray();
        public ImmutableArray<string> ListFiles(string path) => Children(path).Where(name => !name.Contains('/')).ToImmutableArray();

        public void PublishDirectory(string staging, string target)
        {
            if (DirectoryExists(target))
                throw new ConfigProfileException(ConfigProfileFailure.AlreadyExists);
            foreach (var name in Children(staging).ToArray())
                Files[target + '/' + name] = Files[staging + '/' + name];
            DeleteOwnedDirectory(staging);
        }

        public void DeleteOwnedDirectory(string staging)
        {
            foreach (var name in Children(staging).ToArray())
                Files.Remove(staging + '/' + name);
        }

        private IEnumerable<string> Children(string path) => Files.Keys
            .Where(key => key.StartsWith(path + '/', StringComparison.OrdinalIgnoreCase)).Select(key => key[(path.Length + 1)..]);

        private sealed class ProfileLease(Lock gate) : IDisposable
        {
            public void Dispose() => gate.Exit();
        }
    }
}

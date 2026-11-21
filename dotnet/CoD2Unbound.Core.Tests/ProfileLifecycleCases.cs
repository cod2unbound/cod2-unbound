using System.Collections.Immutable;

namespace CoD2Unbound.Core.Tests;

internal static partial class Program
{
    private static void ProfileLifecycleCases()
    {
        Check("legacy migration preserves bytes, retries once and keeps active selection on conflict", () =>
        {
            var files = new FixtureProfileFiles();
            var store = new ConfigProfileStore(files, ConfigDialect.Cod2x);
            var original = ImportBytes("// legacy\r\nseta sensitivity 3\r\n");
            files.Files["main/config_mp.cfg"] = original;
            var values = new ConfigPersonalValues([
                ("sensitivity", "5"), ("r_renderer", "dx9"), ("Sensitivity", "6"),
                ("password", "sample-secret"), ("cl_cdkey", "test-key")], 1200);
            var input = new ConfigMigrationInput(new ConfigMigrationSource.FlattenedLegacy(), original, values);
            files.FailPath = ActivePath;
            ProfileRejects(ConfigProfileFailure.CannotPublish, () => store.Migrate(input));
            Sequence(original, store.OriginalMigrationBackup()!.Value);
            Sequence(original, files.Files["main/config_mp.cfg"]);
            Equal("6", store.Read("Main").Document.EffectiveSetting("sensitivity"));
            Equal(null, store.Read("Main").Document.EffectiveSetting("r_renderer"));
            Equal(null, store.Read("Main").Document.EffectiveSetting("password"));
            Equal(1200, store.Read("Main").Metadata.Dpi);
            Equal(null, store.ActiveSelection().ProfileId);
            files.Files[ActivePath] = ImportBytes("changed-by-game");
            ProfileRejects(ConfigProfileFailure.Conflict, () => store.Migrate(input));
            Sequence(ImportBytes("changed-by-game"), files.Files[ActivePath]);
            files.Files.Remove(ActivePath);
            Equal(false, store.Migrate(input).AlreadyCompleted);
            Equal(true, store.Migrate(input).AlreadyCompleted);
            Equal("Main", store.ActiveSelection().ProfileId);
            var snapshot = store.History("Main").Single();
            Equal(ConfigHistorySource.Migration, snapshot.Source);
            Sequence(original, snapshot.Before.Config);
        });

        Check("connection cleanup checks origin and restores only its own active bytes", () =>
        {
            var files = new FixtureProfileFiles();
            var store = new ConfigProfileStore(files, ConfigDialect.Cod2x);
            var original = store.Create("Main", "Main", new("seta sensitivity 2\n"u8, ConfigDialect.Cod2x));
            store.UpdateChain("Main", original.Document, original.Version, original.Metadata,
                ImportBytes("set helper +attack\n"), "Chain");
            original = store.Read("Main");
            string id = "csconn" + Guid.NewGuid().ToString("N");
            store.Duplicate("Main", id, "Temporary", original.Version);
            var before = ImportBytes("\"Main\"\r\n");
            files.Files[ActivePath] = before;
            store.Select(id, before);
            var applied = store.ActiveSelection().OriginalBytes!.Value;
            Equal(false, store.RestoreConnectionSelection(before, ImportBytes("stale")));
            Equal(id, store.ActiveSelection().ProfileId);
            ProfileRejects(ConfigProfileFailure.Conflict, () => store.DiscardConnectionProfile(id, "WrongOwner", applied));
            Equal(true, store.RestoreConnectionSelection(before, applied));
            store.DiscardConnectionProfile(id, "Main", applied);
            Sequence(before, store.ActiveSelection().OriginalBytes!.Value);
            Equal(false, files.Files.ContainsKey("main/" + ConfigStudioLaunch.ChainBasename(id)));
            Equal(true, files.Files.ContainsKey("main/" + ConfigStudioLaunch.ChainBasename("Main")));
            Equal(1, store.List().Length);
        });

        Check("Trash preserves the companion, validates active ownership and rolls back a failed move", () =>
        {
            var files = new FixtureProfileFiles();
            var trash = new FixtureTrash(files);
            var store = new ConfigProfileStore(files, ConfigDialect.Cod2x);
            var original = store.Create("Main", "Main", new("seta sensitivity 2\n"u8, ConfigDialect.Cod2x));
            var copied = store.Duplicate("Main", "Copy", "Copy", original.Version);
            var chain = ImportBytes("set helper +attack\n");
            store.UpdateChain("Copy", copied.Document, copied.Version, copied.Metadata, chain, "Chain");
            copied = store.Read("Copy");
            store.Select("Main", null);
            ProfileRejects(ConfigProfileFailure.InUse, () => store.MoveToTrash("Main", original.Version, trash));
            trash.Fail = true;
            ProfileRejects(ConfigProfileFailure.CannotTrash, () => store.MoveToTrash("Copy", copied.Version, trash));
            Sequence(chain, store.Read("Copy").Version.Chain!.Value);
            trash.Fail = false;
            var handle = store.MoveToTrash("Copy", copied.Version, trash);
            Equal(1, store.List().Length);
            store.RestoreFromTrash(handle, "Copy", trash);
            Sequence(chain, store.Read("Copy").Version.Chain!.Value);
            Equal(2, store.History("Copy").Length);
        });
    }

    private sealed class FixtureTrash(FixtureProfileFiles files) : IConfigProfileTrash
    {
        private const string TrashPath = "fixture-trash";
        public bool Fail { get; set; }

        public ConfigProfileTrashHandle Move(string path)
        {
            if (Fail)
                throw new IOException("Fixture move failed.");
            files.PublishDirectory(path, TrashPath);
            return new(TrashPath);
        }

        public void Restore(ConfigProfileTrashHandle handle, string target) => files.PublishDirectory(handle.Token, target);
    }
}

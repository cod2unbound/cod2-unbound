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

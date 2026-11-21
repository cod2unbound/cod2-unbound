using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CoD2Unbound.Core.Tests;

internal static partial class Program
{
    private static readonly Guid ConnectionId = new("cd47c776-4f22-4456-a4f3-eb3b6b6097de");
    private const string WindowsData = @"D:\Game data";
    private const string WindowsHome = @"C:\User Home\";
    private const string BinaryPath = @"C:\Program Files\CoD2\cod2_win32.exe";
    private const string DedicatedBinary = @"C:\Program Files\CoD2\cod2_win32_ded.exe";
    private const string WorkingDirectory = @"C:\Program Files\CoD2";

    private static void LaunchCases()
    {
        using var file = Fixtures("launch");
        var root = file.RootElement;
        var culture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.CurrentCulture = culture;
        Check("advanced chain basename", () => Equal(root.String("chain"), ConfigStudioLaunch.ChainBasename("Daily.Stage")));

        foreach (var row in root.GetProperty("client").EnumerateArray())
        {
            Check(row.String("id")!, () =>
            {
                var request = ReadLaunch(row);
                var platform = new MemoryPlatform(request.HomePath);
                if (row.GetProperty("chainExists").GetBoolean())
                    platform.PutFile("main/" + request.Studio!.Chain!);
                GameLaunchPlan? plan = null;
                string? failure = null;
                try { plan = GameLaunchPlan.Client(request, platform); }
                catch (LaunchPlanException error) { failure = error.Message; }
                Equal(row.OptionalString("error"), failure);
                if (plan is null)
                    return;
                Sequence(row.Strings("arguments"), plan.Arguments);
                Equal(row.GetProperty("playsIntroFilms").GetBoolean(), plan.PlaysIntroFilms);
                Equal(row.OptionalString("handoff"), plan.Link?.Url);
            });
        }

        foreach (var row in root.GetProperty("limits").EnumerateArray())
        {
            Check(row.String("id")!, () =>
            {
                var edition = Edition(row);
                var arguments = row.Strings("arguments").Select(value => value!).ToArray();
                Equal(new EngineLimits(row.Int("maxCommands"), row.Int("maxBytes")), EngineCommandLine.Limits(edition));
                Equal(row.String("problem"), EngineCommandLine.Problem(arguments, edition, culture: culture));
                Equal(row.String("editor"), EngineCommandLine.Problem(arguments, edition, inEditor: true, culture: culture));
            });
        }

        foreach (var row in root.GetProperty("dedicated").EnumerateArray())
        {
            foreach (var edition in Enum.GetValues<GameEdition>())
            {
                Check($"dedicated {edition} {row.String("id")}", () =>
                {
                    bool listed = row.String("id") == "listed";
                    var swift = GameLaunchPlan.Dedicated(edition, "/Game data", "/Player home", row.Int("port"), listed, "127.0.0.1");
                    Sequence(row.Strings("arguments"), swift.Arguments);
                    var binary = new GameBinary(edition, EngineRole.Dedicated, DedicatedBinary);
                    var windows = WindowsLaunchPlan.Dedicated(binary, WorkingDirectory, WindowsData, WindowsHome, row.Int("port"), listed, "127.0.0.1");
                    var expected = row.Strings("arguments").Select(value => value switch
                    {
                        "\"/Game data\"" => "\"D:/Game data\"",
                        "\"/Player home\"" => "\"C:/User Home/\"",
                        _ => value
                    });
                    Sequence(expected, windows.Start.EngineArguments);
                    Equal(DedicatedBinary, windows.Start.Executable);
                    Equal(false, windows.PlaysIntroFilms);
                });
            }
        }

        foreach (var edition in Enum.GetValues<GameEdition>())
            foreach (var renderer in Enum.GetValues<WindowsRenderMode>())
            {
                Check($"Windows {edition} {renderer} join", () =>
                {
                    var row = root.GetProperty("client").EnumerateArray().Single(row => row.String("id") == $"join-{Key(edition)}");
                    var request = ReadLaunch(row) with { DataPath = WindowsData, HomePath = WindowsHome };
                    var platform = new MemoryPlatform(WindowsHome);
                    string chain = root.String("chain")!;
                    platform.PutFile("main/" + chain);
                    var binary = new GameBinary(edition, EngineRole.Client, BinaryPath);
                    var plan = WindowsLaunchPlan.Client(request, binary, WorkingDirectory, renderer, platform, ConnectionId);
                    string rendererValue = renderer == WindowsRenderMode.Direct3D9 ? "dx9" : "dx7";
                    string filename = $"join_{ConnectionId:N}.cfg";
                    var expected = row.Strings("arguments").Select(value => value switch
                    {
                        "\"/Game data\"" => "\"D:/Game data\"",
                        "\"/Player home\"" => "\"C:/User Home/\"",
                        _ => value
                    }).ToList();
                    expected.InsertRange(expected.IndexOf(chain) + 1, ["+set", "r_rendererPreference", $"\"{rendererValue}\""]);
                    expected.AddRange(["+exec", filename]);
                    Sequence(expected, plan.Start.EngineArguments);
                    Equal("main/" + filename, plan.Start.PrivateFiles.Single().RelativePath);
                    Equal(root.String("connectionConfig"), Encoding.ASCII.GetString(plan.Start.PrivateFiles.Single().Data.AsSpan()));
                    Equal($"\"{BinaryPath}\" " + string.Join(' ', expected), WindowsCommandLine.Format(plan.Start));
                    Equal(false, WindowsCommandLine.Format(plan.Start).Contains("fake+password", StringComparison.Ordinal));
                    Equal(false, plan.PlaysIntroFilms);
                });
            }

        Check("Windows raw WinMain quoting", () =>
        {
            var sample = new EngineProcessStart(BinaryPath, WorkingDirectory, WindowsHome,
                ["+set", "fs_basepath", "\"D:/Game data\"", "+set", "fs_homepath", "\"C:/User Home/\"", "+demo", "round.one"], []);
            Equal("\"C:\\Program Files\\CoD2\\cod2_win32.exe\" +set fs_basepath \"D:/Game data\" +set fs_homepath \"C:/User Home/\" +demo round.one",
                WindowsCommandLine.Format(sample));
        });

        Check("Windows actual startup separator bounds", () =>
        {
            var request = new ClientLaunchRequest(GameEdition.Cod2x, new(), WindowsData, WindowsHome)
            {
                Forwarded = [string.Join(',', Enumerable.Repeat("+vstr x", 13))]
            };
            var binary = new GameBinary(GameEdition.Cod2x, EngineRole.Client, BinaryPath);
            Equal(32, WindowsLaunchPlan.Client(request, binary, WorkingDirectory).Start.EngineArguments
                .Sum(arg => arg.Count(character => character == '+')));
            // The Mac guard only counts '+'. A raw CoD2x tail also splits on commas and newlines.
            request = request with { Forwarded = ["+vstr x," + string.Join(',', Enumerable.Repeat("vstr x", 13))] };
            string? failure = null;
            try { WindowsLaunchPlan.Client(request, binary, WorkingDirectory); }
            catch (LaunchPlanException error) { failure = error.Message; }
            Equal("This start needs 33 startup commands and CoD2x takes 32. Remove at least one line from Extra launch options in Settings › Advanced.", failure);
        });

        Check("Windows opened link without password", () =>
        {
            var request = new ClientLaunchRequest(GameEdition.Original, new(), WindowsData, WindowsHome)
            {
                Link = LaunchLink.Parse("cod2x://connect/localhost")
            };
            var binary = new GameBinary(GameEdition.Original, EngineRole.Client, BinaryPath);
            var plan = WindowsLaunchPlan.Client(request, binary, WorkingDirectory, connectionId: ConnectionId);
            Equal("connect localhost\n", Encoding.ASCII.GetString(plan.Start.PrivateFiles.Single().Data.AsSpan()));
        });

        Check("in-memory platform boundaries", () => PlatformBoundary().GetAwaiter().GetResult());
    }

    private static ClientLaunchRequest ReadLaunch(JsonElement row)
    {
        var settings = new GameSettings
        {
            Advanced = row.String("advanced")!, Fullscreen = row.String("fullscreen")!, Resolution = row.String("resolution")!,
            Sensitivity = row.GetProperty("sensitivity").GetDouble(), MatchSummariesEnabled = row.GetProperty("matchSummariesEnabled").GetBoolean(),
            IntroFilmsPlayed = row.GetProperty("introPlayed").GetBoolean()
        };
        return new(Edition(row), settings, row.String("dataPath")!, row.String("homePath")!)
        {
            Demo = row.OptionalString("demo"),
            Link = row.OptionalString("link") is { } url ? LaunchLink.Parse(url) : null,
            Forwarded = [.. row.Strings("forwarded").Select(value => value!)],
            Studio = row.TryGetProperty("studio", out var studio)
                ? new(studio.String("profileId")!, studio.OptionalString("chain"), studio.GetProperty("connectionIsolated").GetBoolean()) : null,
            Connection = row.TryGetProperty("connection", out var connection)
                ? new(connection.String("address")!, Dictionary(connection.GetProperty("values"))) : null,
            Loadout = row.TryGetProperty("loadout", out var loadout) ? Dictionary(loadout) : null
        };
    }

    private static GameEdition Edition(JsonElement row) => Enum.Parse<GameEdition>(row.String("engine")!, ignoreCase: true);
    private static ImmutableDictionary<string, string> Dictionary(JsonElement element) =>
        element.EnumerateObject().ToImmutableDictionary(pair => pair.Name, pair => pair.Value.GetString()!, StringComparer.Ordinal);

    private static async Task PlatformBoundary()
    {
        var platform = new MemoryPlatform(WindowsHome);
        ISettingsFolder settings = platform;
        ISecretStore secrets = platform;
        IGameDiscovery discovery = platform;
        IProcessSupervisor processes = platform;
        IExternalLinks links = platform;
        IFileAssociations associations = platform;
        var token = CancellationToken.None;
        await settings.WriteAsync("settings.json", [1, 2, 3], token);
        await secrets.WriteAsync("cd-key", "fixture secret", token);
        Sequence(new byte[] { 1, 2, 3 }, (await settings.ReadAsync("settings.json", token))!.Value);
        Equal("fixture secret", await secrets.ReadAsync("cd-key", token));
        await settings.DeleteAsync("settings.json", token);
        await secrets.WriteAsync("cd-key", null, token);
        Equal(null, await settings.ReadAsync("settings.json", token));
        Equal(null, await secrets.ReadAsync("cd-key", token));
        var binary = new GameBinary(GameEdition.Original, EngineRole.Client, BinaryPath);
        platform.Installations = [new(WindowsData, [binary])];
        Equal(binary, (await discovery.DiscoverAsync(token)).Single().Binaries.Single());
        var start = new EngineProcessStart(binary.Path, WorkingDirectory, WindowsHome, [], [new("main/private.cfg", [1])]);
        await using var process = await processes.StartAsync(start, token);
        Equal(true, platform.IsRegularFile(WindowsHome, "main/private.cfg"));
        Sequence(new[] { process.Id }, await processes.FindEngineProcessesAsync(token));
        await process.StopAsync(token);
        Equal(0, await process.Exited);
        Equal(true, (await processes.FindEngineProcessesAsync(token)).IsEmpty);
        Equal(false, platform.IsRegularFile(WindowsHome, "main/private.cfg"));
        var external = new Uri("https://example.invalid/docs");
        await links.OpenAsync(external, token);
        Equal(external, platform.OpenedLink);
        platform.Activations.Enqueue(new(LauncherActivationKind.ConfigFile, "example.cfg"));
        await foreach (var activation in associations.ReadActivationsAsync(token))
            Equal(new LauncherActivation(LauncherActivationKind.ConfigFile, "example.cfg"), activation);
    }
}

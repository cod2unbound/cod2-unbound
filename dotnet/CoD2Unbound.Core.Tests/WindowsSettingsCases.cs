namespace CoD2Unbound.Core.Tests;

internal static partial class Program
{
    private static void WindowsSettingsCases()
    {
        var native = new DisplayResolution(2560, 1440);
        var hd = new DisplayResolution(1920, 1080);
        var portrait = new DisplayResolution(1200, 1600);
        var modes = WindowsDisplays.Offered([new(native, 60), new(hd, 60), new(native, 120), new(portrait, 60)]);
        Check("Windows only offers reported sizes and keeps the highest refresh", () =>
            Sequence(new[] { new WindowsDisplayMode(portrait, 60), new(hd, 60), new(native, 120) }, modes));
        Check("Windows picks the largest area that fits the request", () =>
            Equal(native, WindowsDisplays.ForLaunch("3840x2160", modes)!.Value));
        Check("Windows keeps a reported mode and makes no size up", () =>
        {
            Equal(hd, WindowsDisplays.ForLaunch("1920x1080", modes)!.Value);
            Equal<DisplayResolution?>(null, WindowsDisplays.ForLaunch("3840x2160", []));
            Equal<DisplayResolution?>(null, WindowsDisplays.ForLaunch("640x480", modes));
        });
        Check("Windows launches the selected monitor and honors a loadout override", () =>
        {
            WindowsDisplay main = new("panel", "Internal", "Intel", 1, native, native, modes);
            WindowsDisplay external = new("desk", "External", "AMD", 2, hd, hd, [new(hd, 60)]);
            var settings = new GameSettings { WindowsMonitor = "desk" };
            var values = System.Collections.Immutable.ImmutableDictionary.Create<string, string>(StringComparer.OrdinalIgnoreCase)
                .Add("r_mode", "3840x2160").Add("r_monitor", "2");
            Equal(external, WindowsDisplays.ForLoadout([main, external], "panel", "2"));
            Equal(main, WindowsDisplays.ForLoadout([main, external], "desk", "3"));
            Equal(external, WindowsDisplays.ForLoadout([main, external], "desk", "0"));
            var request = new ClientLaunchRequest(GameEdition.Cod2x, settings, "C:/data", "C:/home")
            {
                Studio = new("Main"), Loadout = values
            };
            var plan = WindowsLaunchPlan.Client(request, new(GameEdition.Cod2x, EngineRole.Client, "C:/game.exe"), "C:/home", display: external);
            Equal(true, WindowsCommandLine.Format(plan.Start).Contains("+set r_monitor \"2\" +set r_mode \"1920x1080\""));
        });
        Check("Windows restores inherited display values after launcher restart", () =>
        {
            var files = new FixtureProfileFiles();
            var store = new ConfigProfileStore(files, ConfigDialect.Cod2x);
            store.Create("Main", "Main", new("seta r_mode 3840x2160\nseta sensitivity 2\n"u8, ConfigDialect.Cod2x));
            files.Files[ActivePath] = [.. "Main\n"u8];
            var clock = new FixedClock();
            var studio = new ConfigStudioSession(files, ConfigDialect.Cod2x, () => false, clock);
            Equal("Main", studio.PrepareLaunch()!.ProfileId);
            studio.ProtectLaunchDisplay("Main", hd, 2);
            var identity = new GameProcessIdentity(42, clock.GetUtcNow());
            studio.CommitLaunch(identity);
            files.Files[ConfigPath] = [.. "seta r_mode 1920x1080\nseta r_monitor 2\nseta sensitivity 4\n"u8];
            var reopened = new ConfigStudioSession(files, ConfigDialect.Cod2x, () => false, clock);
            reopened.ReconcileGames([identity]);
            reopened.ReconcileGames([]);
            reopened.ReconcileGames([]);
            var result = store.Read("Main").Document;
            Equal("3840x2160", result.EffectiveSetting("r_mode"));
            Equal(null, result.EffectiveSetting("r_monitor"));
            Equal("4", result.EffectiveSetting("sensitivity"));
            Equal(1, store.History("Main").Count(entry => entry.Source == ConfigHistorySource.GameSession));
        });
        Check("Windows stores the monitor identity and last settings tab", () =>
        {
            var settings = GameSettingsJson.Decode("{}"u8.ToArray());
            Equal(0, settings.LastSettingsPane);
            Equal(0, GameSettingsJson.Decode("""{"lastSettingsPane":999}"""u8.ToArray()).LastSettingsPane);
            settings = settings with { LastSettingsPane = 6, WindowsMonitor = "monitor-b" };
            var roundTrip = GameSettingsJson.Decode(GameSettingsJson.Encode(settings));
            Equal(6, roundTrip.LastSettingsPane);
            Equal("monitor-b", roundTrip.WindowsMonitor);
        });
        Check("Windows support report redacts hardware fields and encodes GitHub inputs", () =>
        {
            var privacy = new ProblemReportPrivacy(@"D:\Profiles\Alice", @"D:\Profiles\Alice\AppData\Local",
                ["Alice", "RIG22", "^1Morrow", "000000000000000086D3"]);
            var report = ProblemReport.Create("0.5.0", "412", "CoD2x 1.4.6.8 · Original 1.3", "Windows 10 22H2 (19045) · x64",
                "Direct3D 9", "2560 × 1440 borderless", "Intel & AMD RIG22", @"Alice ^1Morrow D:\Profiles\Alice\Documents\secret", privacy);
            Equal(true, report.Details.Contains("CoD2 Unbound 0.5.0 (412)"));
            Equal(true, report.Details.Contains("Intel & AMD"));
            Equal(false, report.Details.Contains("RIG22", StringComparison.OrdinalIgnoreCase));
            Equal(false, report.Details.Contains("morrow", StringComparison.OrdinalIgnoreCase));
            Equal(false, report.Details.Contains(@"D:\Profiles", StringComparison.OrdinalIgnoreCase));
            Equal(true, report.IssueUrl.Contains("template=windows_bug_report.yml&version=0.5.0%20%28412%29"));
            Equal(true, report.IssueUrl.Contains("Intel%20%26%20AMD"));
        });
        Check("Report rejects a key-shaped string and copies only the safe selected log", () =>
        {
            var privacy = new ProblemReportPrivacy(@"C:\Users\Alice", @"C:\Users\Alice\AppData\Local",
                ["Alice", "WIN-RIG", "^2Morrow"]);
            var date = DateTimeOffset.UnixEpoch;
            ProblemLog game = new(@"log contains ABCD-EFGH-IJKL-MNOP-FFFF", date);
            ProblemLog host = new("Opened \"C:\\Users\\Alice\\AppData\\Local\\CoD2\\main\" by ^2mOrrOw on WIN-RIG", date);
            var result = ProblemReportLogs.Build(game, host, privacy);
            Equal(true, result.GameRefused);
            Equal(false, result.ServerRefused);
            Equal("Opened \"%LOCALAPPDATA%\\…\" by [name omitted] on [name omitted]", result.Text);
            Equal<DateTimeOffset?>(date, result.ServerCopied);
        });
        Check("Report rejects known and checksum-valid keys and labels two clean logs", () =>
        {
            var privacy = new ProblemReportPrivacy("", "", ["000000000000000086D3"]);
            var date = DateTimeOffset.UnixEpoch;
            var game = new ProblemLog("game output", date);
            var host = new ProblemLog("server output", date);
            Equal("Game log\n\ngame output\n\nServer log\n\nserver output", ProblemReportLogs.Build(game, host, privacy).Text);
            Equal(true, ProblemReportLogs.Build(new("000000000000000086D3", date), null, privacy).GameRefused);
            Equal(true, ProblemReportLogs.Build(null, new("0000-0000-0000-0000-86D3", date), privacy).ServerRefused);
        });
    }
}

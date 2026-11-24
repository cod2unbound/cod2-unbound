using CoD2Unbound.Core;
using Microsoft.UI.Xaml;

namespace CoD2Unbound.Windows.Views;

/// Fixture builds only: Home states the picture pass cannot reach without starting the game or publishing
/// a release (COD2_UNBOUND_TEST_HOME_VIEW = running, crash or update, with a fixture home). Public builds
/// compile the empty half.
internal static class HomeFixture
{
#if UPDATE_FIXTURES
    private static readonly string? View = Platform.WindowsLauncherSession.HasFixtureHome
        ? Environment.GetEnvironmentVariable("COD2_UNBOUND_TEST_HOME_VIEW") : null;
    private static readonly DateTimeOffset Started = DateTimeOffset.UtcNow.AddMinutes(-12).AddSeconds(-5);

    internal static GameSessionState Observe(GameSessionState real, string? lastServer) => View switch
    {
        "running" when !real.Running => new(Started, [new(4242, Started)], null,
            Activity: new(new(4242, Started), lastServer, null)),
        "crash" when !real.Running && real.LastExit is null => real with { LastExit = new GameExit(true, CrashReport) },
        _ => real
    };

    internal static string? UpdateVersion => View == "update" ? "0.5.1" : null;

    private static int arranges;
    private static bool hooked;

    /// Startup steps of Home for the picture pass (startup-timing.log), with WinUI's own resource-reference
    /// and binding failures, so a native failure that never reaches managed code still leaves a trail.
    internal static void Trace(string step)
    {
        if (!hooked && Application.Current?.DebugSettings is { } debug)
        {
            hooked = true;
            debug.XamlResourceReferenceFailed += (_, args) => StartupTrace.Mark("home: resource reference failed: " + args.Message);
            debug.BindingFailed += (_, args) => StartupTrace.Mark("home: binding failed: " + args.Message);
        }
        StartupTrace.Mark("home: " + step);
    }

    /// Marks every 50th layout pass of Home, which shows a layout loop in the trace.
    internal static void CountArrange()
    {
        if (++arranges % 50 == 0)
            StartupTrace.Mark($"home: {arranges} arrange passes");
    }

    // The capture script writes this report into the fixture home, so Show Crash Report has a file to show.
    private static string? CrashReport => Platform.WindowsLauncherSession.ResolveHome() is { } home
        ? Path.Join(home, "crash-reports", "crash-fixture.txt") : null;
#else
    internal static GameSessionState Observe(GameSessionState real, string? lastServer) => real;

    internal static string? UpdateVersion => null;

    internal static void Trace(string step) { }

    internal static void CountArrange() { }
#endif
}

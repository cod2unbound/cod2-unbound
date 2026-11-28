using System.Diagnostics;

namespace CoD2Unbound.Windows;

internal static class StartupTrace
{
#if UPDATE_FIXTURES
    internal static bool Enabled => Environment.GetEnvironmentVariable("COD2_UNBOUND_TEST_UPDATE_VISUAL") == "1";
#endif

    [Conditional("UPDATE_FIXTURES")]
    internal static void Mark(string step)
    {
#if UPDATE_FIXTURES
        if (!Enabled ||
            Environment.GetEnvironmentVariable("COD2_UNBOUND_TEST_HOME") is not { } home)
            return;
        try
        {
            using var directory = new Platform.WindowsFiles.DirectoryHandles(home);
            string line = $"{DateTimeOffset.UtcNow:O} {Environment.ProcessId} {step}\n";
            if (step == "managed entry")
            {
                using var process = Process.GetCurrentProcess();
                line = $"{process.StartTime.ToUniversalTime():O} {Environment.ProcessId} process start\n" + line;
            }
            File.AppendAllText(Path.Combine(home, "startup-timing.log"), line);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
#endif
    }
}

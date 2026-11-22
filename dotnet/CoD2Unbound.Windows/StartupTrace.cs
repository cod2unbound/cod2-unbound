using System.Diagnostics;

namespace CoD2Unbound.Windows;

internal static class StartupTrace
{
    [Conditional("UPDATE_FIXTURES")]
    internal static void Mark(string step)
    {
#if UPDATE_FIXTURES
        if (Environment.GetEnvironmentVariable("COD2_UNBOUND_TEST_UPDATE_VISUAL") != "1" ||
            Environment.GetEnvironmentVariable("COD2_UNBOUND_TEST_HOME") is not { } home)
            return;
        try
        {
            using var directory = new Platform.WindowsFiles.DirectoryHandles(home);
            File.AppendAllText(Path.Combine(home, "startup-timing.log"), $"{DateTimeOffset.UtcNow:O} {Environment.ProcessId} {step}\n");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
#endif
    }
}

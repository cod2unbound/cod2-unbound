namespace CoD2Unbound.Windows.Platform;

internal static class WindowsVersion
{
    internal static bool IsWindows11OrLater =>
        !ForceWindows10 && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    internal static bool ForceWindows10
    {
        get
        {
#if UPDATE_FIXTURES
            return Environment.GetEnvironmentVariable("COD2_UNBOUND_TEST_FORCE_WINDOWS10") == "1";
#else
            return false;
#endif
        }
    }
}

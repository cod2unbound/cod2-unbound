using System.Text.Json;

namespace CoD2Unbound.Core.Tests;

internal static partial class Program
{
    private static int passed;
    private static int failed;

    private static int Main()
    {
        ConfigCases();
        WireCases();
        ActivationCases();
        LaunchCases();
        SettingsCases();
        WindowsSettingsCases();
        DirectoryCases();
        ImportCases();
        ImportSessionCases();
        ProfileCases();
        ProfileLifecycleCases();
        ShareCases();
        HostingCases();
        HostingPackageCases();
        SetupCases();
        SetupWorkflowCases();
        WindowsConfigCases();
        SteamCases();
        LifecycleCases();
        LifecycleFlowCases();
        StudioCases();
        CheckerCases();
        ReadyCases();
        LibraryCases();
        ChainCases();
        WindowsBindsCases();
        CheckupCases();
        ConfigChecksCases();
        ExtraCases();
        WorkflowCases();
        WindowsUpdateCases();
        UsageCases();
        ShellCases();
        NewsCases();
        Console.WriteLine($"{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    private static JsonDocument Fixtures(string file)
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", file + ".json"));
        var fixture = JsonDocument.Parse(stream);
        if (fixture.RootElement.GetProperty("swiftRevision").GetString()?.Length != 40)
            throw new InvalidDataException("Missing Swift source revision.");
        return fixture;
    }

    private static void Check(string name, Action test)
    {
        try
        {
            test();
            passed++;
        }
        catch (Exception error)
        {
            failed++;
            Console.Error.WriteLine($"FAIL {name}: {error}");
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidDataException($"Expected {expected}, got {actual}");
    }

    private static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidDataException("Sequence differs from the Swift expectation.");
    }
}

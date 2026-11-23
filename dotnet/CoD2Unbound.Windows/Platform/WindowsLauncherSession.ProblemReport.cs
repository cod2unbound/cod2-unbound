using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using CoD2Unbound.Core;
using Microsoft.Win32;

namespace CoD2Unbound.Windows.Platform;

internal sealed record ProblemLogFile(string Folder, string Name, DateTimeOffset Modified, string Kind)
{
    internal async Task<ProblemLog> ReadAsync()
    {
        using var input = WindowsFiles.Open(Folder, Name);
        if (input.Length > GameSessionLog.FileLimit)
            throw new IOException("The log is too large to copy.");
        byte[] bytes = new byte[(int)input.Length];
        await input.ReadExactlyAsync(bytes);
        return new(Encoding.UTF8.GetString(bytes), Modified);
    }
}

internal sealed partial class WindowsLauncherSession
{
    internal const string IncludedVersions = "CoD2x 1.4.6.8 · Original 1.3";

    internal string Build => typeof(WindowsLauncherSession).Assembly.GetName().Version is { Revision: > 0 } number
        ? number.Revision.ToString(CultureInfo.InvariantCulture) : "";

    internal async Task<ProblemReportPrivacy> ReadReportPrivacyAsync(bool includeLogSecrets = false)
    {
        List<string> privateValues = [.. WindowsReportFacts.PrivateNames(), await ReadRawPlayerNameAsync() ?? ""];
        if (includeLogSecrets)
        {
            privateValues.Add(await credentials.ReadAsync(SetupSession.KeySecret, default) ?? "");
            // Logs can come from a different loadout than the one currently active.
            foreach (var profile in await ReadLoadoutChoicesAsync())
                if (await gameFolder.ReadAsync($"main/players/{profile.Id}/config_mp.cfg", default) is { } bytes &&
                    new ConfigDocument(bytes.AsSpan(), PlayerDialect).EffectiveSetting("name") is { } name)
                    privateValues.Add(name);
        }
        return new(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), privateValues);
    }

    internal async Task<ProblemReport> ReadProblemReportAsync()
    {
        var privacy = await ReadReportPrivacyAsync();
        var profile = await ReadLoadoutAsync();
        var settings = Settings.Working;
        return await Task.Run(() =>
        {
            var displays = WindowsDisplayInventory.ReadForGame();
            var display = WindowsDisplays.ForLoadout(displays, settings.WindowsMonitor, profile?.RequestedMonitor);
            string requested = profile?.RequestedResolution ?? settings.Resolution;
            string resolution = WindowsDisplays.Adjustment(requested, display)?.ToString() ?? requested;
            string[] adapters = [.. WindowsGraphicsFacts.AdapterNames().Concat(displays.Select(item => item.Adapter))
                .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase)];
            return ProblemReport.Create(Version, Build, IncludedVersions, ReportWindowsVersion(), WindowsDisplays.GraphicsName(Renderer),
                $"{LauncherText.Resolution(resolution)} {WindowsDisplays.ModeName(settings.Fullscreen).ToLowerInvariant()}",
                adapters.Length == 0 ? "Unavailable" : string.Join(" · ", adapters),
                string.IsNullOrWhiteSpace(display?.Adapter) ? "Unavailable" : display.Adapter, privacy);
        });
    }

    private static string ReportWindowsVersion()
    {
        var version = Environment.OSVersion.Version;
        string edition = version.Build >= 22000 ? "Windows 11" : "Windows 10";
        string? release = null;
        try
        {
            release = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                "DisplayVersion", null) as string;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        if (release is { Length: 4 } && char.IsAsciiDigit(release[0]) && char.IsAsciiDigit(release[1]) &&
            release[2] is 'H' or 'h' && char.IsAsciiDigit(release[3]))
            edition += " " + release;
        string architecture = RuntimeInformation.OSArchitecture == Architecture.X64 ? "x64" :
            $"{RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()} · {RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()} launcher";
        return FormattableString.Invariant($"{edition} ({version.Build}) · {architecture}");
    }

    internal async Task<(ProblemLogFile? Game, ProblemLogFile? Server)> FindProblemLogsAsync()
    {
        var servers = await Hosting.ReadAsync();
        return await Task.Run(() =>
        {
            var crash = LatestProblemLog(HomePath, WindowsCrashReport.FilePrefix + "*.txt", "crash report", WindowsCrashReport.IsReportName);
            var engine = LatestProblemLog(LogsPath, "engine-*.log", "game log");
            var game = crash is not null && (engine is null || crash.Modified >= engine.Modified) ? crash : engine;
            var server = servers.Select(server => LatestProblemLog(Hosting.LogFolder(server.Id), "engine-*.log", "server log"))
                .Where(file => file is not null).OrderByDescending(file => file!.Modified).FirstOrDefault();
            return (game, server);
        });
    }

    private static ProblemLogFile? LatestProblemLog(string directory, string pattern, string kind, Func<string, bool>? filter = null)
    {
        if (!Directory.Exists(directory))
            return null;
        using var root = new WindowsFiles.DirectoryHandles(directory);
        var latest = new DirectoryInfo(directory).EnumerateFiles(pattern)
            .Where(file => (file.Attributes & FileAttributes.ReparsePoint) == 0 && (filter is null || filter(file.Name)))
            .OrderByDescending(file => file.LastWriteTimeUtc).FirstOrDefault();
        return latest is null ? null : new(directory, latest.Name, latest.LastWriteTimeUtc, kind);
    }
}

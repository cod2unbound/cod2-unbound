using Microsoft.Win32;

namespace CoD2Unbound.Windows.Platform;

internal sealed partial class WindowsGameDiscovery
{
    internal static IReadOnlyList<string> ProfileInstallPaths(CancellationToken cancellationToken)
    {
        var paths = new List<string>();
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            Add(paths, RegistryPath(RegistryHive.LocalMachine, view, RetailKey, "InstallPath"));
            Add(paths, RegistryPath(RegistryHive.CurrentUser, view, RetailKey, "InstallPath"));
            AddSteam(paths, RegistryPath(RegistryHive.CurrentUser, view, SteamKey, "SteamPath"));
            AddSteam(paths, RegistryPath(RegistryHive.LocalMachine, view, SteamKey, "InstallPath"));
        }
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}

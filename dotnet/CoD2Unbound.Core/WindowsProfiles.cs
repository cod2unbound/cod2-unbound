using System.Collections.Immutable;
using System.Text;

namespace CoD2Unbound.Core;

public interface IWindowsProfileFiles
{
    // Implement each operation relative to the trusted root using contained, non-reparse handles.
    // Enumeration is streamed and reports access failures as IO or UnauthorizedAccess exceptions.
    bool IsDirectory(string root, string path);
    bool IsRegularFile(string root, string path);
    IEnumerable<string> EnumerateNames(string root, string path);
    byte[]? ReadPrefix(string root, string path, int maximumBytes);
}

public sealed record WindowsProfileCandidate(string Name, string ConfigPath, string PlayersPath, bool IsActive, bool IsBackup);
public sealed record WindowsProfileDiscovery(ImmutableArray<WindowsProfileCandidate> Profiles, bool Truncated, int UnreadableDirectories);

public static class WindowsProfiles
{
    private static readonly string[] InstallPaths =
    [
        "Program Files (x86)/Activision/Call of Duty 2", "Program Files/Activision/Call of Duty 2",
        "Activision/Call of Duty 2", "Games/Call of Duty 2", "SteamLibrary/steamapps/common/Call of Duty 2",
        "Program Files (x86)/Steam/steamapps/common/Call of Duty 2", "Program Files/Steam/steamapps/common/Call of Duty 2"
    ];

    public static WindowsProfileDiscovery Find(IWindowsProfileFiles files, IReadOnlyList<string> volumes,
        IReadOnlyList<string> selectedFolders, int maximumEntries = 256, int maximumVolumes = 32, string? localAppData = null,
        CancellationToken cancellationToken = default)
    {
        int limit = Math.Max(0, maximumVolumes);
        var scanner = new Scanner(files, Math.Max(0, maximumEntries), cancellationToken)
        {
            Truncated = volumes.Count > limit || selectedFolders.Count > limit
        };
        if (localAppData is not null && Normalize(localAppData) is { } local && ValidRoot(local))
        {
            var candidatePaths = InstallPaths.Concat(selectedFolders.Take(limit).Select(Normalize)
                .Where(path => path.Length > 3 && path[1..3] == ":/").Select(path => path[3..]));
            foreach (string install in candidatePaths.Distinct(StringComparer.OrdinalIgnoreCase).Where(IsProgramFilesInstall))
                scanner.Players(local, local + "/VirtualStore/" + install + "/main/players");
        }

        foreach (string volume in volumes.Take(limit).Select(Normalize).Where(ValidRoot))
            foreach (string installPath in InstallPaths)
                scanner.Players(volume, volume + "/" + installPath + "/main/players");

        foreach (string folder in selectedFolders.Take(limit).Select(Normalize).Where(ValidRoot))
        {
            scanner.Players(folder, folder + "/main/players");
            scanner.Players(folder, folder + "/players");
            scanner.Players(folder, folder);
            int slash = folder.LastIndexOf('/');
            if (slash >= 0 && IsLeaf(folder[(slash + 1)..]))
                scanner.Profile(folder, folder, folder[..slash], folder[(slash + 1)..], false);
        }
        return scanner.Result();
    }

    private static string Normalize(string root) => root.Replace('\\', '/').TrimEnd('/');
    private static bool IsProgramFilesInstall(string path) =>
        (path.StartsWith("Program Files/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("Program Files (x86)/", StringComparison.OrdinalIgnoreCase)) &&
        path.Split('/').All(IsLeaf);

    private static bool ValidRoot(string root) => root.Length != 0 && !root.Contains('\0');
    private static bool IsLeaf(string name) => name.Length > 0 && name is not ("." or "..") &&
        !name.AsSpan().ContainsAny("/\\:\0");

    private sealed class Scanner(IWindowsProfileFiles files, int limit, CancellationToken cancellationToken)
    {
        private readonly Dictionary<string, WindowsProfileCandidate> profiles = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> playersVisited = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> profilesVisited = new(StringComparer.OrdinalIgnoreCase);
        private int inspected;
        private int unreadable;
        public bool Truncated { get; set; }

        public WindowsProfileDiscovery Result() => new([.. profiles.Values.OrderByDescending(entry => entry.IsActive)
            .ThenBy(entry => entry.IsBackup).ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.ConfigPath, StringComparer.Ordinal)], Truncated, unreadable);

        public void Players(string root, string path)
        {
            if (!Try(() => files.IsDirectory(root, path)) || !playersVisited.Add(path))
                return;
            string? active = null;
            string selection = path + "/active.txt";
            if (Try(() => files.IsRegularFile(root, selection) ? files.ReadPrefix(root, selection, 257) : null) is { Length: <= 256 } bytes)
            {
                string text = Encoding.Latin1.GetString(bytes).Trim();
                if (IsLeaf(text) && !text.Contains("..", StringComparison.Ordinal))
                    active = text;
            }
            foreach (string name in Entries(root, path))
                Profile(root, path + "/" + name, path, name, name.Equals(active, StringComparison.OrdinalIgnoreCase));
        }

        public void Profile(string root, string path, string players, string name, bool active)
        {
            if (!Try(() => files.IsDirectory(root, path)) || !profilesVisited.Add(path))
                return;
            ReadConfigs(path, false);
            foreach (string backup in new[] { "backup", "backups", "Raptr" })
                ReadConfigs(path + "/" + backup, true);

            void ReadConfigs(string directory, bool forceBackup)
            {
                foreach (string leaf in Entries(root, directory))
                {
                    string filename = leaf.ToLowerInvariant();
                    string full = directory + "/" + leaf;
                    if (filename.StartsWith("config_mp", StringComparison.Ordinal) &&
                        (filename.EndsWith(".cfg", StringComparison.Ordinal) || filename.EndsWith(".cfg.bak", StringComparison.Ordinal)) &&
                        Try(() => files.IsRegularFile(root, full)))
                        profiles[full] = new(name, full, players, active, forceBackup || filename != "config_mp.cfg");
                }
            }
        }

        private T? Try<T>(Func<T> read)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return read(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                unreadable++;
                return default;
            }
        }

        private IEnumerable<string> Entries(string root, string path)
        {
            var names = new List<string>();
            if (!Try(() => files.IsDirectory(root, path)))
                return names;
            try
            {
                foreach (string name in files.EnumerateNames(root, path))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (inspected == limit)
                    {
                        Truncated = true;
                        break;
                    }
                    inspected++;
                    if (IsLeaf(name))
                        names.Add(name);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                unreadable++;
            }
            return names.Order(StringComparer.OrdinalIgnoreCase);
        }
    }
}

using System.Collections.Immutable;
using CoD2Unbound.Core;

namespace CoD2Unbound.Windows.ViewModels;

internal sealed partial class ConfigsViewModel
{
    internal Task ExportCheckedCopyAsync(string path, ImmutableArray<byte> bytes)
    {
        string relative = Path.GetRelativePath(session.HomePath, path);
        if (!Path.IsPathRooted(relative) && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException("Choose an export location outside the game's writable home.");

        return ConfigCheckCopy.ExportAsync(path, bytes);
    }
}

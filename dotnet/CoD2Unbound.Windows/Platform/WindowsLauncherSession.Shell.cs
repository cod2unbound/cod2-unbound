using System.Collections.Immutable;
using CoD2Unbound.Core;

namespace CoD2Unbound.Windows.Platform;

internal sealed partial class WindowsLauncherSession
{
    internal async Task RememberUpdateNotificationAsync(string version)
    {
        try
        {
            await UpdateLibraryAsync(library => library with { NotifiedUpdates = library.NotifiedUpdates.Add(version) });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A storage failure should not let a notice repeat in this process.
            Library = Library with { NotifiedUpdates = Library.NotifiedUpdates.Add(version) };
        }
    }

    internal Task ClearRecentServersAsync() => UpdateLibraryAsync(library =>
        library with { Recent = [], Played = ImmutableDictionary<string, DateTimeOffset>.Empty });
}

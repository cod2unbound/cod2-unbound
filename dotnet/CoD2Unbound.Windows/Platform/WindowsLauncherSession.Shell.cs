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
}

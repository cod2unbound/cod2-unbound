using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;
using CoD2Unbound.Windows.Views;

namespace CoD2Unbound.Windows;

public sealed partial class MainWindow
{
    private WindowsMediaShare? mediaShare;

    internal WindowsMediaShare LibraryShare =>
        mediaShare ??= new(WinRT.Interop.WindowNative.GetWindowHandle(this));

    internal Task OpenLibraryCheckupAsync(MatchSummary match) => RunAsync(async () =>
    {
        await NavigateAsync(LauncherPlace.Configs);
        if (ContentFrame.Content is ConfigsPage configs)
            configs.OpenCheckupForMatch(match.Id);
    });
}

using System.Collections.Immutable;
using System.Globalization;
using CoD2Unbound.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

internal sealed class ReleaseNotesDialog : ContentDialog
{
    internal ReleaseNotesDialog(MainWindow shell, string version, string current, ReleaseOffer? offer,
        ImmutableArray<ReleaseNoteItem> notes, Uri link, UpdateBlock? hold, bool canInstall)
    {
        // A C# subclass does not pick up the implicit ContentDialog style; use the shared sheet.
        Style = StyleFor("LauncherSheet");
        XamlRoot = shell.DialogRoot;
        RequestedTheme = shell.DialogTheme;
        Resources["ContentDialogMaxWidth"] = 620d;
        Width = 620;
        Title = $"What’s new in {version}";
        this.WithSymbol("mark");
        PrimaryButtonText = offer is null ? "Done" : "Install and Relaunch";
        PrimaryButtonStyle = StyleFor("LauncherAccentButton");
        IsPrimaryButtonEnabled = offer is null || canInstall;
        CloseButtonText = offer is null ? "" : "Later";
        DefaultButton = offer is null ? ContentDialogButton.Primary : ContentDialogButton.Close;

        string introduction = offer is not null
            ? $"You have {current}. This update is {Math.Round(offer.Archive.Size / 1048576d).ToString(CultureInfo.CurrentCulture)} MB."
            : version == current ? "The changes in the version you have." : $"The changes in CoD2 Unbound {version}.";
        var rows = new StackPanel { Spacing = 12 };
        foreach (var note in notes.Take(offer is null ? 6 : 12))
        {
            var words = new StackPanel { Spacing = 2 };
            words.Children.Add(new TextBlock { Text = note.Title, TextWrapping = TextWrapping.Wrap,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            if (note.Detail.Length != 0)
                words.Children.Add(Body(note.Detail));
            if (offer is null)
                rows.Children.Add(words);
            else
            {
                var row = Columns(12, new GridLength(72), new GridLength(1, GridUnitType.Star));
                Put(row, 0, new TextBlock { Text = note.Tag, Style = StyleFor("MonoCaption") });
                Put(row, 1, words);
                rows.Children.Add(row);
            }
        }
        if (rows.Children.Count == 0)
            rows.Children.Add(Caption("Release notes aren’t available here yet."));
        var panel = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                Body(introduction),
                new ScrollViewer { MaxHeight = 320, Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
            }
        };
        if (offer is not null && hold is { } blocker)
            panel.Children.Add(Caption(blocker == UpdateBlock.GameRunning
                ? "Close Call of Duty 2 to install. The update replaces the launcher, so it can’t run while you play."
                : "Stop your hosted servers to install the update."));
        panel.Children.Add(new HyperlinkButton { Content = offer is null ? "Release notes" : "Full release notes", NavigateUri = link });
        Content = panel;
    }
}

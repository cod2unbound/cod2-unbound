using CoD2Unbound.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using static CoD2Unbound.Windows.Views.PagePresentation;
using Size = global::Windows.Foundation.Size;

namespace CoD2Unbound.Windows.Views;

/// What's New (DispatchesCard, NewsLine and HomeNewsSheet on the Mac) and the update pill (UpdatePill).
public sealed partial class HomePage
{
    private (int Rows, int Summary) newsFit = (3, 3);

    internal void RenderNews()
    {
        NewsChecked.Text = model.NewsFoot;
        FitNews(force: true);
    }

    /// As many lines as the card has room for: three, else two, else this version's alone, with shorter
    /// text before fewer lines. The last choice always fits.
    private void FitNews(bool force = false)
    {
        if (stacked)
            return;
        double width = NewsCard.ActualWidth - 40;
        if (width <= 0)
        {
            if (force)
                RenderNewsRows(newsFit.Rows, newsFit.Summary);
            return;
        }
        NewsChecked.Measure(new Size(width, double.PositiveInfinity));
        double room = cardHeight - 40 - 23 - 14 - 8 - NewsChecked.DesiredSize.Height;
        (int Rows, int Summary)[] choices = [(3, 3), (2, 3), (2, 2), (1, 3), (1, 0)];
        var chosen = choices[^1];
        foreach (var choice in choices)
        {
            var probe = new StackPanel { Spacing = 14 };
            foreach (var item in model.News.Take(choice.Rows))
                probe.Children.Add(NewsLine(item, 2, choice.Summary));
            probe.Measure(new Size(width, double.PositiveInfinity));
            if (probe.DesiredSize.Height <= room)
            {
                chosen = choice;
                break;
            }
        }
        if (!force && chosen == newsFit && NewsRows.Children.Count > 0)
            return;
        newsFit = chosen;
        RenderNewsRows(chosen.Rows, chosen.Summary);
    }

    private void RenderNewsRows(int rows, int summary)
    {
        NewsRows.Children.Clear();
        foreach (var item in model.News.Take(rows))
        {
            var line = NewsLine(item, 2, summary);
            if (item.Destination == NewsDestination.None)
            {
                AutomationProperties.SetName(line, Spoken(item));
                NewsRows.Children.Add(line);
                continue;
            }
            var button = new Button
            {
                Content = line, Style = StyleFor("NewsLineButton"), HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            button.Resources["SubtleButtonBackgroundPointerOver"] = Ink("hover");
            button.Resources["SubtleButtonBackgroundPressed"] = Ink("press");
            AutomationProperties.SetName(button, Spoken(item));
            AutomationProperties.SetHelpText(button, OpenTitle(item));
            var dispatch = item;
            button.Click += async (_, _) => await shell.RunAsync(() => OpenNewsItemAsync(dispatch));
            NewsRows.Children.Add(button);
        }
    }

    /// One line of What's New: the date above the title, then the summary. The card limits the lines;
    /// the sheet shows all of it.
    private StackPanel NewsLine(NewsDispatch item, int? titleLines, int? summaryLines)
    {
        var line = new StackPanel { Spacing = 4 };
        line.Children.Add(new TextBlock { Text = item.Stamp, Style = StyleFor("AddressText"), FontSize = 12, TextWrapping = TextWrapping.NoWrap });
        line.Children.Add(new TextBlock
        {
            Text = item.Title, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Style = StyleFor("RecentName"),
            TextWrapping = TextWrapping.Wrap, MaxLines = titleLines ?? 0, TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (item.Summary.Length > 0 && summaryLines != 0)
            line.Children.Add(new TextBlock
            {
                Text = item.Summary, FontSize = 13, LineHeight = 18.2, Style = StyleFor("NewsSummary"),
                TextWrapping = TextWrapping.Wrap, MaxLines = summaryLines ?? 0, TextTrimming = TextTrimming.CharacterEllipsis
            });
        return line;
    }

    private static string Spoken(NewsDispatch item) =>
        $"{(item.Stamp == "NOW" ? "This version" : item.Stamp)}: {item.Title}. {item.Summary}".TrimEnd(' ', '.') + ".";

    private static string OpenTitle(NewsDispatch item) => item.Destination switch
    {
        NewsDestination.Configs => "Open Configs",
        NewsDestination.Servers => "Open Servers",
        NewsDestination.Library => "Open Library",
        NewsDestination.Updates => "Open Updates",
        _ => "Read More"
    };

    /// What's New in full: every release and news line, newest first, then what comes next.
    private async void OnShowNews(object sender, RoutedEventArgs args) => await shell.RunAsync(async () =>
    {
        var rows = new StackPanel { Padding = new Thickness(16, 0, 16, 0) };
        NewsDispatch? selected = null;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, RequestedTheme = shell.DialogTheme, Title = "What’s New",
            PrimaryButtonText = "Release Notes", CloseButtonText = "Done", DefaultButton = ContentDialogButton.Close,
            CloseButtonStyle = StyleFor("LauncherAccentButton"), PrimaryButtonStyle = StyleFor("LauncherButton")
        };
        dialog.Resources["ContentDialogMaxWidth"] = 620d;
        int index = 0;
        foreach (var item in model.AllNews)
        {
            var line = Columns(12, new GridLength(1, GridUnitType.Star), GridLength.Auto);
            line.Padding = new Thickness(0, 12, 0, 12);
            if (index++ > 0)
            {
                line.BorderBrush = Ink("SeparatorBrush");
                line.BorderThickness = new Thickness(0, 1, 0, 0);
            }
            var text = NewsLine(item, null, null);
            AutomationProperties.SetName(text, Spoken(item));
            Put(line, 0, text);
            if (item.Destination != NewsDestination.None)
            {
                var open = new Button { Content = OpenTitle(item), VerticalAlignment = VerticalAlignment.Center };
                AutomationProperties.SetName(open, $"{OpenTitle(item)}: {item.Title}");
                var dispatch = item;
                open.Click += (_, _) => { selected = dispatch; dialog.Hide(); };
                Put(line, 1, open);
            }
            rows.Children.Add(line);
        }
        if (rows.Children.Count == 0)
            rows.Children.Add(new TextBlock { Text = "Nothing yet.", Style = StyleFor("SecondaryText"), Margin = new Thickness(0, 12, 0, 12) });
        var well = new Border
        {
            Background = Ink("well"), BorderBrush = Ink("wellStroke"), BorderThickness = new Thickness(1),
            CornerRadius = (CornerRadius)Application.Current.Resources["CrateRadius"],
            Child = new ScrollViewer { MaxHeight = 380, Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
        };
        dialog.Content = new StackPanel
        {
            Spacing = 14,
            Children = { new TextBlock { Text = model.NewsFoot, Style = StyleFor("SecondaryText") }, well }
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
            await OpenAsync(WindowsReleaseAssets.ReleasesPage.AbsoluteUri);
        else if (selected is not null)
            await OpenNewsItemAsync(selected);
    });

    private Task OpenNewsItemAsync(NewsDispatch item) => item.Destination switch
    {
        NewsDestination.Configs => shell.NavigateAsync(LauncherPlace.Configs),
        NewsDestination.Servers => shell.NavigateAsync(LauncherPlace.Servers),
        NewsDestination.Library => shell.NavigateAsync(LauncherPlace.Library),
        NewsDestination.Updates => shell.NavigateAsync(LauncherPlace.UpdateSettings),
        NewsDestination.Browser when item.BrowserLink is { } link => OpenAsync(link.AbsoluteUri),
        _ => Task.CompletedTask
    };

    // MARK: Update pill

    /// "CoD2 Unbound 0.5.1 is ready to install · What's New · Install Update" while a release is ready or
    /// installing; a failed install with its Details. Checking and up to date live in Settings › Updates.
    internal void RefreshUpdate(LauncherUpdateState state)
    {
        bool failed = state.Offer is not null && state.Issue is not (UpdateIssue.None or UpdateIssue.CheckUnavailable or UpdateIssue.ProtectedLocation);
        bool installing = !failed && state.Phase is UpdatePhase.Downloading or UpdatePhase.Verifying or UpdatePhase.Installing;
        bool ready = state.Offer is not null && state.Issue == UpdateIssue.None && state.Phase is UpdatePhase.Offered or UpdatePhase.Ready;
        bool visible = !session.SetupUpdating && (ready || installing || failed);
        UpdatePill.Visibility = IsVisible(visible);
        if (!visible)
        {
            shell.SetHomeCaptionPassthrough(null);
            return;
        }
        string version = state.AvailableVersion ?? "";
        UpdateProgress.IsActive = installing;
        UpdateProgress.Visibility = IsVisible(installing);
        UpdateIcon.Visibility = IsVisible(!installing);
        UpdateIcon.Glyph = failed ? "" : (string)Application.Current.Resources["FluentArrowDownload"];
        UpdateIcon.FontFamily = failed ? new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets") :
            (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["FluentRegular"];
        UpdateIcon.Foreground = Ink(failed ? "NegativeBrush" : "HeroAccentBrush");
        UpdateText.Text = installing ? "Installing CoD2 Unbound " + version + "…" :
            failed ? state.Issue == UpdateIssue.SetupFailed
                ? session.InstallNeedsRepair ? "The update didn’t finish. Run Setup again to repair CoD2 Unbound." :
                    $"The update didn’t finish. {session.Version} is still installed and works."
                : "The update couldn’t be installed."
            : version.Length == 0 ? "A new version of CoD2 Unbound is available" : $"CoD2 Unbound {version} is ready to install";
        ToolTipService.SetToolTip(UpdateText, failed ? UpdateText.Text : null);
        UpdateNotes.Visibility = IsVisible(ready);
        UpdateButton.Visibility = IsVisible(ready);
        UpdateDetails.Visibility = IsVisible(failed);
        UpdatePill.Padding = new Thickness(16, 0, ready ? 4 : 16, 0);
        AutomationProperties.SetName(UpdatePill, "Update: " + UpdateText.Text);
        ReportPillBounds();
    }

    private async void OnUpdateAction(object sender, RoutedEventArgs args) => await shell.RunAsync(shell.InstallOfferedUpdateAsync);

    private async void OnUpdateDetails(object sender, RoutedEventArgs args) =>
        await shell.RunAsync(() => shell.NavigateAsync(LauncherPlace.UpdateSettings));

    private async void OnUpdateNotes(object sender, RoutedEventArgs args) =>
        await shell.RunAsync(() => shell.ShowReleaseNotesAsync(session.Updates.State.AvailableVersion ?? session.Version));

    /// The pill reaches into the title bar row; its rectangle takes pointer input there.
    private void OnUpdatePillMoved(object sender, SizeChangedEventArgs args) => ReportPillBounds();

    private void ReportPillBounds()
    {
        if (UpdatePill.Visibility != Visibility.Visible || XamlRoot is null || !IsLoaded)
        {
            shell.SetHomeCaptionPassthrough(null);
            return;
        }
        var corner = UpdatePill.TransformToVisual(null).TransformPoint(new global::Windows.Foundation.Point(0, 0));
        double scale = XamlRoot.RasterizationScale;
        shell.SetHomeCaptionPassthrough(new global::Windows.Graphics.RectInt32(
            (int)Math.Floor(corner.X * scale), (int)Math.Floor(corner.Y * scale),
            (int)Math.Ceiling(UpdatePill.ActualWidth * scale), (int)Math.Ceiling(UpdatePill.ActualHeight * scale)));
    }
}

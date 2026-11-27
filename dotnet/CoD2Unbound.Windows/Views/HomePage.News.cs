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
        var list = HomeList("NewsItem");
        AutomationProperties.SetName(list, "What’s New");
        list.ItemClick += async (_, args) =>
        {
            if (args.ClickedItem is FrameworkElement { Tag: NewsDispatch dispatch } && dispatch.Destination != NewsDestination.None)
                await shell.RunAsync(() => OpenNewsItemAsync(dispatch));
        };
        foreach (var item in model.News.Take(rows))
        {
            var line = NewsLine(item, 2, summary);
            line.Tag = item;
            var entry = new ListViewItem { Content = line, Tag = item, Style = LocalStyle("NewsItem") };
            AutomationProperties.SetName(entry, Spoken(item));
            AutomationProperties.SetHelpText(entry, item.Destination == NewsDestination.None ? "" : OpenTitle(item));
            list.Items.Add(entry);
        }
        NewsRows.Children.Add(list);
    }

    /// One line of What's New: the date above the title, then the summary. The card limits the lines;
    /// the sheet shows all of it.
    private StackPanel NewsLine(NewsDispatch item, int? titleLines, int? summaryLines)
    {
        var line = new StackPanel { Spacing = 4 };
        line.Children.Add(new TextBlock { Text = item.Stamp, Style = StyleFor("AddressText"), FontSize = 12, TextWrapping = TextWrapping.NoWrap });
        line.Children.Add(new TextBlock
        {
            Text = item.Title, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Style = LocalStyle("RecentName"),
            TextWrapping = TextWrapping.Wrap, MaxLines = titleLines ?? 0, TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (item.Summary.Length > 0 && summaryLines != 0)
            line.Children.Add(new TextBlock
            {
                Text = item.Summary, FontSize = 13, LineHeight = 18.2, Style = LocalStyle("NewsSummary"),
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
        // The rows are a stock ListView (list semantics and arrow keys); each row's Open button acts, so the
        // items themselves do not click and show no hover fill.
        var rows = new ListView
        {
            SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = false, Padding = new Thickness(16, 0, 16, 0)
        };
        rows.Resources["ListViewItemBackgroundPointerOver"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        rows.Resources["ListViewItemBackgroundPressed"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        AutomationProperties.SetName(rows, "What’s New");
        NewsDispatch? selected = null;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, RequestedTheme = shell.DialogTheme, Title = "What’s New",
            PrimaryButtonText = "Release Notes", CloseButtonText = "Done", DefaultButton = ContentDialogButton.Close
        }.WithSymbol("mark");
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
            Put(line, 0, NewsLine(item, null, null));
            if (item.Destination != NewsDestination.None)
            {
                var open = new Button { Content = OpenTitle(item), VerticalAlignment = VerticalAlignment.Center };
                AutomationProperties.SetName(open, $"{OpenTitle(item)}: {item.Title}");
                var dispatch = item;
                open.Click += (_, _) => { selected = dispatch; dialog.Hide(); };
                Put(line, 1, open);
            }
            var entry = new ListViewItem
            {
                Content = line, Padding = new Thickness(0), MinHeight = 0, HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            AutomationProperties.SetName(entry, Spoken(item));
            rows.Items.Add(entry);
        }
        if (rows.Items.Count == 0)
            rows.Items.Add(new ListViewItem
            {
                Content = new TextBlock { Text = "Nothing yet.", Style = StyleFor("SecondaryText"), Margin = new Thickness(0, 12, 0, 12) }
            });
        var well = new Border
        {
            Background = Ink("well"), BorderBrush = Ink("wellStroke"), BorderThickness = new Thickness(1),
            Child = rows
        };
        rows.MaxHeight = 380;
        dialog.Content = new StackPanel
        {
            Spacing = 14,
            Children = { new TextBlock { Text = model.NewsFoot, Style = StyleFor("SecondaryText") }, well }
        };
        var result = await shell.ShowShellSheetAsync(dialog);
        if (result == ContentDialogResult.Primary)
            await shell.ShowReleaseNotesAsync(session.Updates.State.AvailableVersion ?? session.Version);
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
        string version = state.AvailableVersion ?? "";
        if (HomeFixture.UpdateVersion is { } shown)
            (ready, installing, failed, version) = (true, false, false, shown);
        bool visible = !session.SetupUpdating && (ready || installing || failed);
        UpdatePill.Visibility = IsVisible(visible);
        if (!visible)
            return;
        UpdateProgress.IsActive = installing;
        UpdateProgress.Visibility = IsVisible(installing);
        UpdateIcon.Visibility = IsVisible(!installing);
        UpdateIcon.Glyph = (string)Application.Current.Resources[failed ? "FluentWarning" : "FluentArrowDownload"];
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
        AutomationProperties.SetName(UpdatePill, "Update: " + UpdateText.Text);
    }

    private async void OnUpdateAction(object sender, RoutedEventArgs args) => await shell.RunAsync(shell.InstallOfferedUpdateAsync);

    private async void OnUpdateDetails(object sender, RoutedEventArgs args) =>
        await shell.RunAsync(() => shell.NavigateAsync(LauncherPlace.UpdateSettings));

    private async void OnUpdateNotes(object sender, RoutedEventArgs args) =>
        await shell.RunAsync(() => shell.ShowReleaseNotesAsync(session.Updates.State.AvailableVersion ?? session.Version));
}

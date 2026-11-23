using CoD2Unbound.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class HomePage
{
    internal void RenderNews()
    {
        RenderNewsRows(NewsRows, model.News, OpenFromCard);
        NewsChecked.Text = model.NewsFoot;

        async void OpenFromCard(NewsDispatch item) => await shell.RunAsync(() => OpenNewsItemAsync(item));
    }

    private async void OnShowNews(object sender, RoutedEventArgs args) => await shell.RunAsync(async () =>
    {
        var rows = new StackPanel { Spacing = 4 };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, RequestedTheme = shell.DialogTheme, Width = 620, Title = "What’s New",
            PrimaryButtonText = "Done", DefaultButton = ContentDialogButton.Primary, PrimaryButtonStyle = StyleFor("LauncherAccentButton")
        };
        dialog.Resources["ContentDialogMaxWidth"] = 620d;
        NewsDispatch? selected = null;
        RenderNewsRows(rows, model.AllNews, item =>
        {
            selected = item;
            dialog.Hide();
        });
        var releaseNotes = new HyperlinkButton { Content = "Release notes", NavigateUri = WindowsReleaseAssets.ReleasesPage };
        dialog.Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new ScrollViewer { MaxHeight = 340, Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                Caption(model.NewsFoot), releaseNotes
            }
        };
        await dialog.ShowAsync();
        if (selected is not null)
            await OpenNewsItemAsync(selected);
    });

    private static void RenderNewsRows(StackPanel rows, IEnumerable<NewsDispatch> items, Action<NewsDispatch> open)
    {
        rows.Children.Clear();
        foreach (var dispatch in items)
        {
            var item = new StackPanel { Spacing = 4 };
            item.Children.Add(new TextBlock { Text = dispatch.Stamp, Style = StyleFor("MonoCaption") });
            AddFact(item, dispatch.Title, dispatch.Summary);
            if (dispatch.Destination == NewsDestination.None)
            {
                rows.Children.Add(item);
                continue;
            }
            string action = dispatch.Destination switch
            {
                NewsDestination.Configs => "Open Configs",
                NewsDestination.Servers => "Open Servers",
                NewsDestination.Library => "Open Library",
                NewsDestination.Updates => "Open Updates",
                _ => "Read more in the browser"
            };
            string name = $"{action}: {dispatch.Title}";
            var line = Columns(8, new GridLength(1, GridUnitType.Star), GridLength.Auto);
            Put(line, 0, item);
            Put(line, 1, Fluent("ChevronRight", 14));
            var button = new Button
            {
                Content = line, Style = StyleFor("SubtleAction"), HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 6, 8, 6)
            };
            AutomationProperties.SetName(button, name);
            ToolTipService.SetToolTip(button, name);
            button.Click += (_, _) => open(dispatch);
            rows.Children.Add(button);
        }
        if (rows.Children.Count == 0)
            rows.Children.Add(Caption("Nothing yet."));
    }

    private Task OpenNewsItemAsync(NewsDispatch item) => item.Destination switch
    {
        NewsDestination.Configs => shell.NavigateAsync(LauncherPlace.Configs),
        NewsDestination.Servers => shell.NavigateAsync(LauncherPlace.Servers),
        NewsDestination.Library => shell.NavigateAsync(LauncherPlace.Library),
        NewsDestination.Updates => shell.NavigateAsync(LauncherPlace.UpdateSettings),
        NewsDestination.Browser when item.BrowserLink is { } link => OpenAsync(link.AbsoluteUri),
        _ => Task.CompletedTask
    };

    private async void OnUpdateNotes(object sender, RoutedEventArgs args) =>
        await shell.RunAsync(() => shell.ShowReleaseNotesAsync(session.Updates.State.AvailableVersion ?? session.Version));
}

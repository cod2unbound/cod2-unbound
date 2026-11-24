using CoD2Unbound.Windows.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class SettingsPage
{
    private void About()
    {
        var panel = new StackPanel { Spacing = 12, MaxWidth = 900 };
        Fields.Children.Add(panel);
        var mark = new Image { Width = 72, Height = 72, VerticalAlignment = VerticalAlignment.Top,
            Source = new BitmapImage(new Uri("ms-appx:///Assets/mark-48.png")) };
        var title = new TextBlock { Text = "COD2 UNBOUND", Style = StyleFor("StencilText"), FontSize = 40,
            TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level1);
        var copy = new Button { Content = IconLabel("Copy", "Copy version info", 16) };
        ToolTipService.SetToolTip(copy, "Copy the version, game versions, Windows, graphics and display, as Report a problem shows them.");
        ProblemReportDialog.AddCopyFeedback(copy, "Copy version info", async () => (await session.ReadProblemReportAsync()).Details);
        var version = Body("Version " + session.Version + (session.Build.Length == 0 ? "" : $" ({session.Build})"));
        version.VerticalAlignment = VerticalAlignment.Center;
        var versionGroup = new StackPanel { Spacing = 5, Children = { title, Buttons(version, copy),
            Caption("Call of Duty 2 multiplayer, on every platform. Free and open source. You bring your own copy of the game.") } };
        var header = Columns(18, GridLength.Auto, new GridLength(1, GridUnitType.Star));
        Put(header, 0, mark);
        Put(header, 1, versionGroup);
        panel.Children.Add(header);

        var star = new GridLength(1, GridUnitType.Star);
        var cards = Columns(4, star, star, star);
        Put(cards, 0, AboutFact("Game versions", WindowsLauncherSession.IncludedVersions, "Both included"));
        Put(cards, 1, AboutFact("Requires", "Windows 10 22H2 or Windows 11", "x64"));
        Put(cards, 2, AboutFact("Graphics", "Direct3D 9", "DirectX 7 stays as a choice in Settings"));
        panel.Children.Add(cards);

        var release = Action("Release Notes", () => OpenAsync("https://github.com/cod2unbound/cod2-unbound/releases"));
        var website = Action("Website", () => OpenAsync("https://github.com/cod2unbound/cod2-unbound"));
        var report = Action("Report a Problem…", ReportProblemFromShellAsync);
        var acknowledgements = Action("Acknowledgements", ShowAcknowledgementsAsync);
        var shortcuts = Action("Show", () => KeyboardShortcutsDialog.ShowAsync(shell));
        var update = Action("Check for Updates…", () =>
        {
            Panes.SelectedItem = Panes.Items[4];
            return shell.RunUpdateAsync(install: false);
        }, exclusive: false);
        var actions = new StackPanel { Spacing = 8, Children =
        {
            Buttons(release, website, report),
            Buttons(acknowledgements, update),
            SettingRow("Help", null, Action("Open Help", () => OpenAsync(MainWindow.HelpUrl))),
            SettingRow("Keyboard shortcuts", null, shortcuts)
        } };
        panel.Children.Add(actions);

        var built = new TextBlock { Text = "Built on", Style = StyleFor("BodyStrongTextBlockStyle") };
        AutomationProperties.SetHeadingLevel(built, AutomationHeadingLevel.Level2);
        panel.Children.Add(built);
        var components = new StackPanel();
        components.Children.Add(SettingRow("opencod2", WindowsAcknowledgements.Notices[0].Role, Caption("No licence declared"), grouped: true));
        components.Children.Add(SettingRow("CoD2x", "The multiplayer community patch. Its behavior is the reference for the CoD2x edition.", grouped: true));
        components.Children.Add(SettingRow("SDL", "Windows, input and sound for the game.", Caption("zlib"), grouped: true));
        components.Children.Add(SettingRow("curl", "Downloads for the game over Windows TLS.", Caption("curl"), grouped: true));
        components.Children.Add(SettingRow("Windows App SDK", "The launcher’s window and controls.", Caption("MIT"), grouped: true));
        components.Children.Add(SettingRow("Typefaces", "Big Shoulders Stencil and Courier Prime.", Caption("OFL-1.1"), grouped: true));
        panel.Children.Add(new Border { Style = StyleFor("FieldCard"), Padding = new Thickness(0), Child = components });
        panel.Children.Add(Caption("Call of Duty is a registered trademark of Activision Publishing, Inc. CoD2 Unbound is a community project and is not affiliated with or endorsed by Activision. No game content is included."));
    }

    private static Border AboutFact(string label, string value, string footnote) =>
        new()
        {
            Style = StyleFor("FieldCard"),
            Child = new StackPanel { Spacing = 3, Children = { Caption(label),
                new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Style = StyleFor("BodyStrongTextBlockStyle") },
                Caption(footnote) } }
        };

    private async Task ShowAcknowledgementsAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, RequestedTheme = shell.DialogTheme, Title = "Acknowledgements",
            SecondaryButtonText = "Copy all", CloseButtonText = "Done", DefaultButton = ContentDialogButton.Close
        }.WithSymbol("SystemPeople", "CoD2 Unbound stands on other people’s work. Select one to read its licence.");
        dialog.Resources["ContentDialogMaxWidth"] = 600d;
        dialog.Resources["ContentDialogMinWidth"] = 600d;
        var content = new StackPanel { Spacing = 10 };
        var list = new StackPanel { Spacing = 2 };
        var body = new ContentControl { Content = list, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        foreach (var notice in WindowsAcknowledgements.Notices)
        {
            var entry = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = SettingRow(notice.Name, $"{notice.Author} · {notice.Licence}\n{notice.Role}",
                    Fluent("ChevronRight", 16), grouped: true)
            };
            AutomationProperties.SetName(entry, $"{notice.Name}, {notice.Author}, {notice.Licence}. {notice.Role}");
            entry.Click += (_, _) => ShowLicence(notice, entry);
            list.Children.Add(entry);
        }
        content.Children.Add(Body("CoD2 Unbound stands on other people’s work. Select one to read its licence."));
        content.Children.Add(body);
        dialog.SecondaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            await shell.ShowProblemAsync(() =>
            {
                ProblemReportDialog.CopyText(WindowsAcknowledgements.ReadAll());
                dialog.SecondaryButtonText = "Copied";
                return Task.CompletedTask;
            });
            await Task.Delay(1800);
            dialog.SecondaryButtonText = "Copy all";
        };
        dialog.Content = content;
        await dialog.ShowAsync();

        void ShowLicence(WindowsNotice notice, Button entry)
        {
            var back = new Button { Content = IconLabel("ChevronLeft", "All acknowledgements", 16) };
            AutomationProperties.SetName(back, "All acknowledgements");
            back.Click += (_, _) =>
            {
                FocusOnLoad(entry);
                body.Content = list;
            };
            var text = new TextBlock
            {
                Text = WindowsAcknowledgements.Read(notice), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Style = StyleFor("Mono")
            };
            var label = new TextBlock { Text = notice.Name, Style = StyleFor("SectionTitle") };
            AutomationProperties.SetHeadingLevel(label, AutomationHeadingLevel.Level2);
            FocusOnLoad(back);
            body.Content = new StackPanel { Spacing = 8, Children =
            {
                back, label, Caption($"{notice.Author} · {notice.Licence}"), text
            } };
        }

        static void FocusOnLoad(Control control)
        {
            control.Loaded += OnLoaded;

            void OnLoaded(object sender, RoutedEventArgs args)
            {
                control.Loaded -= OnLoaded;
                control.Focus(FocusState.Programmatic);
            }
        }
    }
}

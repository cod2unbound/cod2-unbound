using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;
using CoD2Unbound.Windows.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows;

public sealed partial class MainWindow
{
    private void ShowHelpMenu()
    {
        var help = new MenuFlyout();
        var openHelp = new MenuFlyoutItem { Text = "Open Help" };
        openHelp.Click += async (_, _) => await ShowProblemAsync(() => OpenAsync(HelpUrl));
        var report = new MenuFlyoutItem { Text = "Report a Problem…" };
        report.Click += OnReportProblem;
        help.Items.Add(openHelp);
        help.Items.Add(report);
        help.ShowAt(Root, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
        {
            Position = new global::Windows.Foundation.Point(Root.ActualWidth / 2, 72)
        });
    }

    private async void OnReportProblem(object sender, RoutedEventArgs args) => await RunAsync(ShowProblemReportAsync);

    internal async Task ShowProblemReportAsync()
    {
        ReportCopied.IsOpen = ReportWarning.IsOpen = false;
        var report = await session.ReadProblemReportAsync();
        (ProblemLogFile? Game, ProblemLogFile? Server) logs = default;
        try { logs = await session.FindProblemLogsAsync(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }

        var dialog = new ProblemReportDialog(report, logs.Game is not null, logs.Server is not null)
        {
            XamlRoot = DialogRoot, RequestedTheme = DialogTheme
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        if (dialog.IncludeGame || dialog.IncludeServer)
        {
            try
            {
                var privacy = await session.ReadReportPrivacyAsync(includeLogSecrets: true);
                var game = dialog.IncludeGame ? await logs.Game!.ReadAsync() : null;
                var server = dialog.IncludeServer ? await logs.Server!.ReadAsync() : null;
                var result = await Task.Run(() => ProblemReportLogs.Build(game, server, privacy));
                if (result.Text.Length > 0)
                {
                    ProblemReportDialog.CopyText(result.Text);
                    string[] copied = new[]
                    {
                        Copied(logs.Game, result.GameCopied),
                        Copied(logs.Server, result.ServerCopied)
                    }.OfType<string>().ToArray();
                    ReportCopied.Message = $"Copied {string.Join(" and ", copied)}. Paste {(copied.Length == 1 ? "it" : "them")} into the report on GitHub.";
                    ReportCopied.IsOpen = true;
                }
                if (result.GameRefused || result.ServerRefused)
                {
                    string refused = result is { GameRefused: true, ServerRefused: true } ? "The game and server logs weren't copied because they may contain your CD key." :
                        $"The {(result.GameRefused ? "game" : "server")} log wasn't copied because it may contain your CD key.";
                    ShowReportWarning(refused, result.GameRefused ? logs.Game : logs.Server);
                }
                else if (!ReportCopied.IsOpen)
                    ShowReportWarning("The selected logs didn't contain anything that could be copied.");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                ShowReportWarning("The selected logs couldn't be copied.", dialog.IncludeGame ? logs.Game : logs.Server);
            }
        }
        await OpenAsync(report.IssueUrl);
    }

    private static string? Copied(ProblemLogFile? log, DateTimeOffset? time)
    {
        if (log is null || time is not { } value)
            return null;
        var local = value.ToLocalTime();
        var today = DateTimeOffset.Now.Date;
        string day = local.Date == today ? "today" : local.Date == today.AddDays(-1) ? "yesterday" : local.ToString("d MMMM");
        return $"the {log.Kind} from {day} at {local:HH:mm}";
    }

    private void ShowReportWarning(string message, ProblemLogFile? file = null)
    {
        ReportWarning.Message = message;
        ReportWarning.ActionButton = null;
        if (file is not null)
        {
            var show = new Button { Content = "Show in File Explorer" };
            show.Click += async (_, _) => await RunAsync(() => OpenAsync(file.Folder));
            ReportWarning.ActionButton = show;
        }
        ReportWarning.IsOpen = true;
    }
}

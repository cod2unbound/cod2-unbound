using CoD2Unbound.Core;
using CoD2Unbound.Windows.Views;
using Microsoft.UI.Xaml.Controls;

namespace CoD2Unbound.Windows;

public sealed partial class MainWindow
{
    private readonly ServerAdmission serverAdmission = new();

    internal async Task<(ServerAddress Target, GameEdition? Edition)?> PromptServerAsync(string? address = null, ServerLink? link = null,
        bool passwordRequired = false)
    {
        var dialog = new ConnectDialog(address, link, passwordRequired) { XamlRoot = DialogRoot, RequestedTheme = DialogTheme };
        return await dialog.ShowAsync() == ContentDialogResult.Primary && dialog.Target is { } target
            ? (target, dialog.RequestedEdition) : null;
    }

    internal async Task<string?> JoinServerAsync(ServerAddress target, UsageJoin source, GameEdition? requested = null,
        GameServer? known = null, bool lookupUnknown = false)
    {
        if (session.ClientIsRunning)
            return "Call of Duty 2 is already running.";

        known ??= session.Library.Cache.FirstOrDefault(server => server.Address == target.Address);
        if (source == UsageJoin.TypedAddress || ((lookupUnknown || requested is not null) && known is null))
        {
            known = await session.ServerQuery.ServerAsync(target.Address) ?? known;
            serverAdmission.LookUpFinished(target.Address, cancelled: false);
        }
        var decision = requested is { } edition
            ? new ServerAdmissionResult(ServerAdmissionAction.Launch, edition,
                session.HasClient(edition) ? null : $"Reinstall CoD2 Unbound to get {ConnectDialog.EditionName(edition)}.")
            : await CheckServerEditionAsync(target, known);
        if (decision.Problem is { } problem)
        {
            session.ShowError(UsageProblem.ServerVersion);
            return problem;
        }
        if (known is { PasswordRequired: true } && target.Password is null)
        {
            if (await PromptServerAsync(target.Address, passwordRequired: true) is not { } unlocked)
                return null;
            target = unlocked.Target;
        }
        if (decision.Action == ServerAdmissionAction.ConfirmVersion)
        {
            string version = ConnectDialog.EditionName(decision.Edition);
            var dialog = new ContentDialog
            {
                XamlRoot = DialogRoot, RequestedTheme = DialogTheme, Title = "Switch game version?",
                Content = $"This server runs {version}.", PrimaryButtonText = "Launch", CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return null;
        }

        return (await session.JoinAsync(target, decision.Edition, source)) switch
        {
            GameStartState.Started => "Launching Call of Duty 2…",
            GameStartState.NeedsSetup or GameStartState.MissingData => "Choose a complete game data folder before launching.",
            _ => "Call of Duty 2 is already running."
        };
    }

    private async Task<ServerAdmissionResult> CheckServerEditionAsync(ServerAddress target, GameServer? known)
    {
        var link = LaunchLink.Direct(target.Address, target.Password ?? "");
        var edition = session.Settings.Working.Engine;
        var installed = Enum.GetValues<GameEdition>().Where(session.HasClient).ToHashSet();
        var result = serverAdmission.Evaluate(link, known, edition, installed, alwaysUseServerVersion: false);
        if (result.Action != ServerAdmissionAction.LookUp)
            return result;
        bool queried = false;
        try
        {
            known = await session.ServerQuery.ServerAsync(target.Address);
            queried = true;
        }
        finally { serverAdmission.LookUpFinished(target.Address, cancelled: !queried); }
        return serverAdmission.Evaluate(link, known, edition, installed, alwaysUseServerVersion: false);
    }
}

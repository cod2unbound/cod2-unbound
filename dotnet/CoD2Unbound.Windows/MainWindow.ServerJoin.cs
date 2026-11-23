using System.ComponentModel;
using CoD2Unbound.Core;
using CoD2Unbound.Windows.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows;

public sealed partial class MainWindow
{
    private readonly ServerAdmission serverAdmission = new();

    internal async Task<string?> JoinServerAsync(ServerAddress target, UsageJoin source, GameEdition? requested = null,
        GameServer? known = null, bool lookupUnknown = false, ServerReadySession? readyCheck = null,
        string? loadoutId = null, bool alreadyLookedUp = false)
    {
        ClearProblem();
        if (!alreadyLookedUp)
        {
            known ??= session.Library.Cache.FirstOrDefault(server => ServerBrowser.TryAddress(server.Address) == target.Address);
            if (known is not null && known.Fields.IsEmpty)
                known = null;
            if (known is null && (lookupUnknown || requested is not null || session.Settings.Working.Engine == GameEdition.Original))
            {
                known = await session.ServerQuery.ServerAsync(target.Address);
                serverAdmission.LookUpFinished(target.Address, cancelled: false);
            }
        }

        await session.Game.ReconcileAsync();
        var normal = session.Settings.Working.Engine;
        var installed = Enum.GetValues<GameEdition>().Where(session.HasClient).ToHashSet();
        GameProcessIdentity? running = session.Game.State.ActiveGame;
        GameEdition? runningEdition = running is { } game ? session.RunningEdition(game) : null;
        var link = LaunchLink.Direct(target.Address, target.Password ?? "");
        serverAdmission.EndGame();
        if (alreadyLookedUp)
            serverAdmission.LookUpFinished(target.Address, cancelled: false);
        var decision = requested is { } edition
            ? new ServerAdmissionResult(ServerAdmissionAction.Launch, edition)
            : serverAdmission.Evaluate(link, known, runningEdition ?? normal, installed, session.AlwaysUseServerVersion);
        if (decision.Problem is { } problem)
        {
            session.ShowError(UsageProblem.ServerVersion);
            return problem;
        }
        if (running is not null && runningEdition is { } activeEdition && activeEdition != decision.Edition)
        {
            ShowServerJoinWarning($"This server needs {ConnectDialog.EditionName(decision.Edition)}. Close the game before switching versions.");
            return null;
        }

        if (running is not null && runningEdition is { } runningMode && session.HasTemporaryServerLoadout(runningMode))
        {
            ShowServerJoinWarning("Close the game to remove the temporary server settings before joining another server.");
            return null;
        }

        (bool Remember, bool HadSaved)? preference = null;
        if (known?.PasswordRequired != false && (target.Password is null || source == UsageJoin.Link))
        {
            string? saved = null;
            try { saved = await session.ServerPasswords.ReadAsync(target.Address); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception) { }
            if (known is not null || saved is not null)
            {
                string name = known is null ? target.Address : QuakeColors.Plain(known.Name);
                if (await PromptPasswordAsync(name, saved, target.Password) is not { } answer)
                    return null;
                target = target with { Password = answer.Password };
                link = LaunchLink.Direct(target.Address, answer.Password);
                preference = (answer.Remember, saved is not null);
            }
        }

        if (decision.Action == ServerAdmissionAction.ConfirmVersion &&
            !await ConfirmServerVersionAsync(known, target.Address, normal, decision.Edition, readyCheck, loadoutId))
            return null;

        if (preference is { } choice && (choice.Remember || choice.HadSaved))
        {
            try { await session.ServerPasswords.RememberAsync(target.Address, choice.Remember ? target.Password : null); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
            {
                ShowServerJoinWarning(choice.Remember
                    ? "The password couldn’t be saved in Windows Credential Manager, so it was used for this game only."
                    : "The password couldn’t be removed from Windows Credential Manager.");
            }
        }

        if (!Problem.IsOpen && known is not null && ServerFacts.From(known).Occupancy == ServerOccupancy.Full)
            ShowServerJoinWarning("This server is full. The game will still try to connect.");

        if (running is { } activeGame)
            return await DeliverServerJoinAsync(activeGame, link, decision.Edition, source, known);

        ServerLaunchLoadout? loadout;
        try
        {
            var mode = decision.Edition == GameEdition.Cod2x ? ConfigCatalogMode.Cod2x : ConfigCatalogMode.Stock;
            loadout = loadoutId is not null ? new(loadoutId) : readyCheck?.Prepare(link, known, mode);
        }
        catch (InvalidOperationException error)
        {
            ShowServerJoinWarning(error.Message);
            return null;
        }
        var start = await RunGameStartAsync(decision.Edition,
            () => session.JoinAsync(target, decision.Edition, source, known, loadout));
        return start switch
        {
            GameStartState.Started => "Launching Call of Duty 2…",
            null => null,
            _ => "Call of Duty 2 is already running."
        };
    }

    private async Task<string?> DeliverServerJoinAsync(GameProcessIdentity game, LaunchLink link, GameEdition edition,
        UsageJoin source, GameServer? known)
    {
        var delivery = await session.DeliverJoinAsync(game, link, edition, source, known);
        if (delivery == GameLinkDelivery.Delivered)
            return "Sent the server link to Call of Duty 2.";
        ShowServerJoinWarning(delivery == GameLinkDelivery.Pending
            ? "The game hasn't finished starting. Open the server link again after the menu appears."
            : "Could not deliver the server link to the game. Try again after the menu appears.");
        return null;
    }

    private async Task<(string Password, bool Remember)?> PromptPasswordAsync(string name, string? saved, string? supplied)
    {
        var dialog = new ServerPasswordDialog(name, saved, supplied) { XamlRoot = DialogRoot, RequestedTheme = DialogTheme };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? (dialog.Password, dialog.Remember) : null;
    }

    private async Task<bool> ConfirmServerVersionAsync(GameServer? server, string address, GameEdition normal, GameEdition needed,
        ServerReadySession? readyCheck, string? loadoutId)
    {
        string version = ConnectDialog.EditionName(needed);
        string usual = normal == GameEdition.Cod2x ? "CoD2x" : ConnectDialog.EditionName(normal);
        string name = server is null ? address : QuakeColors.Plain(server.Name);
        string? loadout = loadoutId is null ? readyCheck?.ChosenProfile?.DisplayName ?? (await session.ReadLoadoutAsync())?.DisplayName :
            (await session.ReadLoadoutChoicesAsync()).FirstOrDefault(profile => profile.Id == loadoutId)?.DisplayName;
        string explanation = $"You normally play {usual}. CoD2 Unbound will start {version} for this match and go back to {usual} afterwards.";
        if (loadout is not null)
            explanation += $" Your {loadout} loadout works on both.";
        var remember = new CheckBox { Content = "Don’t ask again; always use the version the server runs" };
        ToolTipService.SetToolTip(remember, "Settings › Advanced › Reset warnings brings this question back");
        var dialog = new ContentDialog
        {
            XamlRoot = DialogRoot, RequestedTheme = DialogTheme, Title = $"“{name}” runs {version}",
            Content = new StackPanel { Spacing = 10, Children = { Body(explanation), remember } },
            PrimaryButtonText = $"Launch with {version}", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return false;
        if (remember.IsChecked == true)
            await session.RememberServerVersionChoiceAsync();
        return true;
    }

    private void ShowServerJoinWarning(string message) => ShowShellNotice(message, InfoBarSeverity.Warning);
}

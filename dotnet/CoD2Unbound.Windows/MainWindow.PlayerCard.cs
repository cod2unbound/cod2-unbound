using CoD2Unbound.Windows.Views;
using Microsoft.UI.Xaml.Controls;

namespace CoD2Unbound.Windows;

public sealed partial class MainWindow
{
    private PlayerCard? playerCard;

    private async void OnPlayerCardOpened(object sender, object args)
    {
        var flyout = (Flyout)sender;
        playerCard ??= new PlayerCard(this, session, flyout);
        if (!ReferenceEquals(flyout.Content, playerCard))
            flyout.Content = playerCard;
        await ShowProblemAsync(playerCard.RefreshAsync);
    }

    private async void OnPlayerCardClosed(object sender, object args)
    {
        if (playerCard is not null)
            await ShowProblemAsync(playerCard.FlushNameAsync);
    }

    internal async Task<bool> EditCdKeyAsync()
    {
        if (session.ClientIsRunning)
            return false;
        bool saved = await CdKeyEditor.ShowAsync(this, session.Setup.HasStoredKey, session.ReplaceKeyAsync);
        if (saved)
        {
            await RefreshPlayerAsync();
            if (ContentFrame.Content is HomePage home)
                home.RenderGameControls(session.Game.State);
        }
        return saved;
    }
}

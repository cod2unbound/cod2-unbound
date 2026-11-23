using CoD2Unbound.Core;
using CoD2Unbound.Windows.Platform;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

internal sealed class PlayerCard : StackPanel
{
    private readonly MainWindow shell;
    private readonly WindowsLauncherSession session;
    private readonly PlayerNameEditor editor;
    private readonly TextBlock tagName = new() { TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock tagGame = Caption("");
    private readonly TextBlock footnote = Caption("");
    private readonly TextBlock keyStatus = Caption("");
    private readonly Button changeKey = new();
    private readonly ComboBox loadout = new() { Header = "Loadout for your next game", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly InfoBar running = new()
    {
        Message = "The game is running. Change your name after you close it.",
        IsClosable = false, Severity = InfoBarSeverity.Informational
    };
    private readonly DispatcherQueueTimer timer;
    private readonly SemaphoreSlim save = new(1);
    private (string Id, string Name)? pending;
    private string? profileId;
    private bool loading;

    internal PlayerCard(MainWindow shell, WindowsLauncherSession session, Flyout flyout)
    {
        this.shell = shell;
        this.session = session;
        Width = 350;
        Spacing = 12;
        AutomationProperties.SetName(this, "Player card");
        AutomationProperties.SetName(loadout, "Loadout for your next game");
        var dogTag = new Border
        {
            Style = StyleFor("FieldCard"), CornerRadius = new CornerRadius(12),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 12,
                Children =
                {
                    new TextBlock { Text = "●", VerticalAlignment = VerticalAlignment.Center },
                    new StackPanel { Children = { tagName, tagGame } }
                }
            }
        };
        editor = new PlayerNameEditor(shell);
        timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(300);
        timer.IsRepeating = false;
        timer.Tick += async (_, _) => await shell.ShowProblemAsync(FlushNameAsync);
        editor.Edited += () =>
        {
            RenderTag();
            if (profileId is null || loading)
                return;
            timer.Stop();
            pending = editor.Valid ? (profileId, editor.NameText.Trim()) : null;
            if (pending is not null)
                timer.Start();
        };
        changeKey.Click += async (_, _) => await shell.RunAsync(async () =>
        {
            await FlushNameAsync();
            flyout.Hide();
            await shell.EditCdKeyAsync();
        });
        loadout.SelectionChanged += async (_, _) =>
        {
            if (loading || loadout.SelectedItem is not ConfigProfile profile || profile.Id == profileId)
                return;
            await shell.RunAsync(async () =>
            {
                await FlushNameAsync();
                await session.SelectLoadoutAsync(profile.Id);
                await RefreshAsync();
                await shell.RefreshPlayerAsync();
            });
        };
        Children.Add(dogTag);
        Children.Add(editor);
        Children.Add(running);
        Children.Add(SettingRow("CD key", null, changeKey));
        Children.Add(keyStatus);
        Children.Add(loadout);
        Children.Add(footnote);
        UpdateRunning(true);
    }

    internal async Task RefreshAsync()
    {
        loading = true;
        try
        {
            await FlushNameAsync();
            if (!session.ClientIsRunning && await session.ReadLoadoutAsync() is null)
                await session.Game.ReconcileAsync();
            var choices = await session.ReadLoadoutChoicesAsync();
            var active = choices.FirstOrDefault(choice => choice.IsActive);
            profileId = active?.Id;
            var rawName = await session.ReadRawPlayerNameAsync();
            editor.NameText = rawName == SetupPlayer.Unknown ? "" : rawName ?? "";
            loadout.ItemsSource = choices;
            loadout.DisplayMemberPath = nameof(ConfigProfile.DisplayName);
            loadout.SelectedItem = active;
            footnote.Text = active is null ? "Choose a loadout to save your name." :
                $"Saves to your {active.DisplayName} loadout as you type.";
            bool stored = session.Setup.HasStoredKey;
            keyStatus.Text = stored ? "Verified · Stored only on this PC." : "Not entered · Needed to join most servers.";
            changeKey.Content = stored ? "Replace…" : "Enter key…";
            RenderTag();
            UpdateRunning(session.ClientIsRunning);
        }
        finally { loading = false; }
    }

    internal void UpdateRunning(bool gameRunning)
    {
        running.IsOpen = gameRunning;
        editor.IsEnabled = !gameRunning && profileId is not null;
        changeKey.IsEnabled = !gameRunning;
        loadout.IsEnabled = !gameRunning && loadout.Items.Count != 0;
    }

    internal async Task FlushNameAsync()
    {
        timer.Stop();
        await save.WaitAsync();
        try
        {
            if (pending is not { } draft)
                return;
            pending = null;
            if (!session.ClientIsRunning)
            {
                await session.SavePlayerNameAsync(draft.Id, draft.Name);
                await shell.RefreshPlayerAsync();
            }
        }
        finally { save.Release(); }
    }

    private void RenderTag()
    {
        string name = QuakeColors.Plain(editor.NameText);
        tagName.Text = (string.IsNullOrWhiteSpace(name) ? SetupPlayer.Unknown : name).ToUpperInvariant();
        tagGame.Text = session.Settings.Working.Engine == GameEdition.Cod2x ? "COD2X · 1.4" : "COD2 · 1.3";
    }
}

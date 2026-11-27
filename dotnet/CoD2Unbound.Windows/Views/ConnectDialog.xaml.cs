using CoD2Unbound.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

public sealed partial class ConnectDialog : ContentDialog
{
    private readonly ServerQueryClient query;
    private readonly ServerLink? openedLink;
    private readonly bool highContrast;
    private string? queriedAddress;
    private CancellationTokenSource? lookup;
    private Task lookupTask = Task.CompletedTask;
    private bool open;
    private bool accepting;

    internal ConnectDialog(ServerQueryClient query, LauncherLibrary library, bool highContrast, ServerLink? link = null)
    {
        this.query = query;
        openedLink = link;
        this.highContrast = highContrast;
        DefaultStyleKey = typeof(ContentDialog);
        InitializeComponent();
        this.WithSymbol("SystemLink", "For a server that isn’t in the list, or one a friend sent you.");
        if (link is not null)
        {
            AddressBox.Text = link.Target.Address;
            AddressBox.IsReadOnly = true;
            PasswordInput.Password = link.Target.Password ?? "";
            DefaultButton = ContentDialogButton.Close;
        }
        else
            BuildRecent(library);
        Opened += (_, _) =>
        {
            open = true;
            AddressBox.Focus(FocusState.Programmatic);
            UpdateAddress();
        };
        Closed += (_, _) =>
        {
            open = false;
            lookup?.Cancel();
        };
        PrimaryButtonClick += CompleteLookup;
    }

    internal ServerAddress? Target { get; private set; }
    internal GameEdition? RequestedEdition { get; private set; }
    internal GameServer? Observation { get; private set; }
    internal bool AddFavorite => Favorite.IsOn;

    internal static string EditionName(GameEdition edition) => edition == GameEdition.Cod2x ? "CoD2x 1.4.6.8" : "Original 1.3";

    private void BuildRecent(LauncherLibrary library)
    {
        if (library.Recent.IsEmpty)
            return;
        var heading = Caption("Recent addresses");
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        RecentAddresses.Children.Add(heading);
        foreach (string address in library.Recent.Take(4))
        {
            var server = library.Cache.FirstOrDefault(item => item.Address == address);
            string name = server is null ? "Not seen in the list" : QuakeColors.Plain(server.Name);
            string date = library.Played.TryGetValue(address, out var played) ? $"{played.ToLocalTime():dd MMM}" : "";
            var content = Columns(8, new GridLength(150), new GridLength(1, GridUnitType.Star), GridLength.Auto);
            Put(content, 0, new TextBlock { Text = address, Style = StyleFor("AddressText"), TextTrimming = TextTrimming.CharacterEllipsis });
            Put(content, 1, new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis });
            Put(content, 2, Caption(date));
            var button = new Button
            {
                Style = StyleFor("SubtleAction"), HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = content
            };
            AutomationProperties.SetName(button, $"Use {address}, {name}{(date.Length > 0 ? ", " + date : "")}");
            button.Click += (_, _) =>
            {
                AddressBox.Text = address;
                AddressBox.Focus(FocusState.Programmatic);
            };
            RecentAddresses.Children.Add(button);
        }
    }

    private void OnAddressChanged(object sender, TextChangedEventArgs args)
    {
        if (open)
            UpdateAddress();
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs args)
    {
        if (open)
            UpdateEntry();
    }

    private ServerConnectEntry UpdateEntry()
    {
        var entry = ServerConnectEntry.Parse(AddressBox.Text, PasswordInput.Password);
        Target = entry.Target;
        RequestedEdition = openedLink?.Edition ?? entry.Edition;
        Validation.Text = entry.PasswordProblem ?? "";
        Validation.Visibility = IsVisible(entry.PasswordProblem is not null);
        IsPrimaryButtonEnabled = Target is not null;
        return entry;
    }

    private void UpdateAddress()
    {
        var entry = UpdateEntry();
        if (queriedAddress == entry.Address && entry.Address is not null)
            return;
        lookup?.Cancel();
        queriedAddress = entry.Address;
        Observation = null;
        if (entry.Address is { } address)
            lookupTask = AskAsync(address);
        else
            ShowAnswer(entry.InvalidAddress
                ? "Use a host name or IP address, with an optional port from 1 to 65535. For example, 203.0.113.24:28960."
                : "", "NegativeBrush");
    }

    private async Task AskAsync(string address)
    {
        using var request = new CancellationTokenSource();
        lookup = request;
        ShowAnswer($"Asking {address}…", "CaptionBrush", asking: true);
        try
        {
            GameServer? result;
            try { result = await ServerConnectEntry.AskAsync(query, address, request.Token); }
            catch (Exception error) when (error is IOException or System.Net.Sockets.SocketException) { result = null; }
            if (request.IsCancellationRequested)
                return;
            Observation = result;
            if (result is null)
            {
                ShowAnswer($"Nothing answered at {address}. Check the address and port. You can still launch; the game will try to connect.",
                    "AddressBrush");
                return;
            }

            string locked = result.PasswordRequired ? " · needs a password" : "";
            ShowAnswer($"{QuakeColors.Plain(result.Name)} answered · {MapCatalog.DisplayName(result.Map)} · " +
                $"{result.PlayerCount}/{result.MaxPlayers} · {result.Ping} ms{locked}", "PositiveBrush");
            if (result.PasswordRequired && !accepting)
                PasswordInput.Focus(FocusState.Programmatic);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(lookup, request))
                lookup = null;
        }
    }

    private async void CompleteLookup(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        accepting = true;
        var deferral = args.GetDeferral();
        try
        {
            while (!lookupTask.IsCompleted)
                await lookupTask;
            args.Cancel = Target is null;
        }
        finally
        {
            accepting = false;
            deferral.Complete();
        }
    }

    private void ShowAnswer(string text, string color, bool asking = false)
    {
        LookupLine.Visibility = IsVisible(text.Length != 0);
        Asking.Visibility = IsVisible(asking);
        Asking.IsActive = asking;
        Answered.Visibility = IsVisible(color == "PositiveBrush" && text.Length > 0);
        Answer.Foreground = ThemeBrush(color, ActualTheme, highContrast);
        Answer.Text = text;
    }
}

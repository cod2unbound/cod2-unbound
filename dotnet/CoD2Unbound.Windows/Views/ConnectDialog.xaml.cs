using CoD2Unbound.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CoD2Unbound.Windows.Views;

public sealed partial class ConnectDialog : ContentDialog
{
    private readonly string? linkedAddress;
    private readonly ServerLink? openedLink;
    private readonly bool passwordRequired;

    internal ConnectDialog(string? address = null, ServerLink? link = null, bool passwordRequired = false)
    {
        this.passwordRequired = passwordRequired;
        openedLink = link;
        linkedAddress = link?.Target.Address ?? (passwordRequired ? address : null);
        InitializeComponent();
        AddressBox.Text = address ?? "";
        if (link is not null)
        {
            AddressBox.IsReadOnly = true;
            PasswordInput.Password = link.Target.Password ?? "";
            DefaultButton = ContentDialogButton.Close;
        }
        if (passwordRequired)
        {
            AddressBox.IsReadOnly = true;
            Description.Text = "This server requires a password.";
            PasswordInput.Header = "Password";
        }
        UpdateTarget();
        Opened += (_, _) =>
        {
            UpdateTarget();
            (address is null ? (Control)AddressBox : PasswordInput).Focus(FocusState.Programmatic);
        };
    }

    internal ServerAddress? Target { get; private set; }
    internal GameEdition? RequestedEdition { get; private set; }

    internal static string EditionName(GameEdition edition) => edition == GameEdition.Cod2x ? "CoD2x 1.4.6.8" : "Original 1.3";

    private void OnEntryChanged(object sender, RoutedEventArgs args) => UpdateTarget();

    private void UpdateTarget()
    {
        if (AddressBox is null || PasswordInput is null || Description is null)
            return;
        string input = AddressBox.Text.Trim();
        var link = openedLink ?? (linkedAddress is null ? ServerLink.Parse(input) : null);
        bool hasScheme = input.StartsWith("cod2://", StringComparison.OrdinalIgnoreCase) ||
            input.StartsWith("cod2x://", StringComparison.OrdinalIgnoreCase);
        var address = linkedAddress is not null ? new ServerAddress(linkedAddress, null) :
            hasScheme ? link?.Target : ServerAddress.Parse(input);
        RequestedEdition = link?.Edition;
        string password = PasswordInput.Password;
        if (password.Length == 0)
            password = address?.Password ?? "";
        string? passwordProblem = ServerAddress.PasswordProblem(password);
        Description.Text = passwordProblem ?? (passwordRequired ? "This server requires a password." :
            RequestedEdition is { } edition ? $"This link requests {EditionName(edition)}." :
            address is null && input.Length > 0 ? "Enter a server address or a CoD2 Unbound server link." :
            "For a server that isn’t in the list, or one a friend sent you.");
        Target = address is null || (passwordRequired && password.Length == 0) || passwordProblem is not null
            ? null : address with { Password = password.Length == 0 ? null : password };
        IsPrimaryButtonEnabled = Target is not null;
    }
}

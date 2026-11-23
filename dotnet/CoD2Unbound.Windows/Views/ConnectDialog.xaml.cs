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
        var link = openedLink ?? (linkedAddress is null ? ServerLink.Parse(AddressBox.Text.Trim()) : null);
        var address = linkedAddress is null ? link?.Target ?? ServerAddress.Parse(AddressBox.Text) : new ServerAddress(linkedAddress, null);
        RequestedEdition = link?.Edition;
        if (!passwordRequired)
            Description.Text = RequestedEdition is { } edition ? $"This link requests {EditionName(edition)}." :
                "For a server that isn’t in the list, or one a friend sent you.";
        string password = PasswordInput.Password;
        Target = address is null || (passwordRequired && password.Length == 0) || ServerAddress.PasswordProblem(password) is not null ? null :
            password.Length == 0 ? address : address with { Password = password };
        IsPrimaryButtonEnabled = Target is not null;
    }
}

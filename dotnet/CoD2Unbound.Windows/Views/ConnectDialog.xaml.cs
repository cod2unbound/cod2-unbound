using CoD2Unbound.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CoD2Unbound.Windows.Views;

public sealed partial class ConnectDialog : ContentDialog
{
    private readonly string? linkedAddress;

    internal ConnectDialog(string? address = null, ServerLink? link = null)
    {
        linkedAddress = link?.Target.Address;
        InitializeComponent();
        AddressBox.Text = address ?? "";
        if (link is not null)
        {
            AddressBox.IsReadOnly = true;
            Description.Text = $"This link requests {EditionName(link.Edition)}.";
            PasswordInput.Password = link.Target.Password ?? "";
            DefaultButton = ContentDialogButton.Close;
        }
        UpdateTarget();
        Opened += (_, _) =>
        {
            UpdateTarget();
            (address is null ? (Control)AddressBox : PasswordInput).Focus(FocusState.Programmatic);
        };
    }

    internal ServerAddress? Target { get; private set; }

    internal static string EditionName(GameEdition edition) => edition == GameEdition.Cod2x ? "CoD2x 1.4.6.8" : "Original 1.3";

    private void OnEntryChanged(object sender, RoutedEventArgs args) => UpdateTarget();

    private void UpdateTarget()
    {
        if (AddressBox is null || PasswordInput is null)
            return;
        var address = linkedAddress is null ? ServerAddress.Parse(AddressBox.Text) : new ServerAddress(linkedAddress, null);
        string password = PasswordInput.Password;
        Target = address is null || ServerAddress.PasswordProblem(password) is not null ? null :
            password.Length == 0 ? address : address with { Password = password };
        IsPrimaryButtonEnabled = Target is not null;
    }
}

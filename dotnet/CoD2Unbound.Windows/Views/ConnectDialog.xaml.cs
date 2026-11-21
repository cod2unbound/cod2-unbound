using CoD2Unbound.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CoD2Unbound.Windows.Views;

public sealed partial class ConnectDialog : ContentDialog
{
    internal ConnectDialog(string? address = null)
    {
        InitializeComponent();
        AddressBox.Text = address ?? "";
        Opened += (_, _) => (address is null ? (Control)AddressBox : PasswordInput).Focus(FocusState.Programmatic);
    }

    internal ServerAddress? Target { get; private set; }

    private void OnEntryChanged(object sender, RoutedEventArgs args)
    {
        if (AddressBox is null || PasswordInput is null)
            return;
        var address = ServerAddress.Parse(AddressBox.Text);
        string password = PasswordInput.Password;
        Target = address is null || ServerAddress.PasswordProblem(password) is not null ? null :
            password.Length == 0 ? address : address with { Password = password };
        IsPrimaryButtonEnabled = Target is not null;
    }
}

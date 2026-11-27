using CoD2Unbound.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

internal sealed class ServerPasswordDialog : ContentDialog
{
    private readonly PasswordBox password;
    private readonly CheckBox remember;
    private readonly TextBlock note;
    private readonly TextBlock validation;
    private readonly bool hadSavedPassword;

    internal ServerPasswordDialog(string serverName, string? saved, string? supplied)
    {
        this.WithWidth("ServerPasswordSheetWidth");
        hadSavedPassword = saved is not null;
        DefaultStyleKey = typeof(ContentDialog);
        Title = $"“{serverName}” needs a password";
        this.WithSymbol("FluentLock", "The people who run the server hand it out.");
        PrimaryButtonText = "Launch";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;
        password = new PasswordBox
        {
            Style = StyleFor("CapsulePasswordBox"), PasswordRevealMode = PasswordRevealMode.Hidden, MaxLength = 127,
            Password = supplied ?? saved ?? "", PlaceholderText = "Password"
        };
        AutomationProperties.SetName(password, $"Password for {serverName}");
        remember = new CheckBox { Content = "Remember for this server, on this PC only", IsChecked = true };
        note = Caption("");
        validation = new TextBlock { Style = StyleFor("NegativeText"), TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetLiveSetting(validation, AutomationLiveSetting.Polite);
        Content = new StackPanel
        {
            Spacing = 8,
            Children = { password, validation, remember, note }
        };
        password.PasswordChanged += (_, _) => Update();
        remember.Checked += (_, _) => Update();
        remember.Unchecked += (_, _) => Update();
        Opened += (_, _) => password.Focus(FocusState.Programmatic);
        Update();
    }

    internal string Password => password.Password;
    internal bool Remember => remember.IsChecked == true;

    private void Update()
    {
        string? problem = ServerAddress.PasswordProblem(Password);
        IsPrimaryButtonEnabled = Password.Length != 0 && problem is null;
        validation.Visibility = IsVisible(problem is not null);
        validation.Text = problem ?? "";
        note.Text = (hadSavedPassword, Remember) switch
        {
            (true, true) => "Saved on this PC. Untick to forget it when you launch.",
            (true, false) => "The saved password is forgotten when you launch.",
            (false, true) => "Kept in Windows Credential Manager on this PC; it never syncs.",
            _ => ""
        };
        note.Visibility = IsVisible(note.Text.Length != 0);
    }
}

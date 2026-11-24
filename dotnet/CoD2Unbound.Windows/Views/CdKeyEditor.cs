using CoD2Unbound.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

internal sealed class CdKeyEditor : StackPanel
{
    private readonly PasswordBox input = new()
    {
        Header = "CD key", PlaceholderText = "XXXX XXXX XXXX XXXX XXXX", PasswordRevealMode = PasswordRevealMode.Hidden
    };
    private readonly InfoBar feedback = new() { IsClosable = false };

    internal string Value { get => input.Password; set => input.Password = value; }
    internal bool Verified => CdKey.IsValid(Value);
    internal event Action? Edited;

    internal CdKeyEditor(MainWindow shell)
    {
        Spacing = 10;
        input.Style = StyleFor("CapsulePasswordBox");
        AutomationProperties.SetName(input, "CD key");
        var paste = new Button { Content = "Paste", VerticalAlignment = VerticalAlignment.Bottom };
        paste.Click += async (_, _) => await shell.ShowProblemAsync(async () =>
        {
            var clipboard = Clipboard.GetContent();
            if (clipboard.Contains(StandardDataFormats.Text))
            {
                Value = (await clipboard.GetTextAsync()).Trim();
                input.Focus(FocusState.Programmatic);
            }
        });
        var row = Columns(8, new(1, GridUnitType.Star), GridLength.Auto);
        Put(row, 0, input);
        Put(row, 1, paste);
        Children.Add(row);
        Children.Add(feedback);
        input.PasswordChanged += (_, _) =>
        {
            Render();
            Edited?.Invoke();
        };
        Render();
    }

    internal void FocusInput() => input.Focus(FocusState.Programmatic);

    private void Render()
    {
        var (state, left) = CdKey.Check(Value);
        feedback.IsOpen = true;
        (feedback.Severity, feedback.Message) = state switch
        {
            CdKeyState.Verified => (InfoBarSeverity.Success, "Checksum verified."),
            CdKeyState.Mismatch => (InfoBarSeverity.Error,
                "The checksum doesn’t match. Check for a mistyped character: 0 and O, 1 and I are the usual ones."),
            CdKeyState.Typing => (InfoBarSeverity.Informational,
                $"{left} {(left == 1 ? "character" : "characters")} to go. Spaces and dashes are fine."),
            _ => (InfoBarSeverity.Informational,
                "Spaces and dashes are fine. Find it on the manual, the disc sleeve, or in Steam under Manage › CD keys.")
        };
    }

    internal static async Task<bool> ShowAsync(MainWindow shell, bool replacing, Func<string, Task<bool>> save)
    {
        var editor = new CdKeyEditor(shell);
        var dialog = new ContentDialog
        {
            XamlRoot = shell.DialogRoot, RequestedTheme = shell.DialogTheme, Title = "Replace your CD key",
            Content = new StackPanel
            {
                Spacing = 12,
                Children = { Caption(replacing ? "The key you type replaces the one stored on this PC." :
                    "Enter the 20-character key that came with your game."), editor }
            },
            PrimaryButtonText = replacing ? "Replace" : "Save Key",
            IsPrimaryButtonEnabled = false,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        editor.Edited += () => dialog.IsPrimaryButtonEnabled = editor.Verified;
        dialog.Opened += (_, _) => editor.FocusInput();
        try { return await dialog.ShowAsync() == ContentDialogResult.Primary && await save(editor.Value); }
        finally { editor.Value = ""; }
    }
}

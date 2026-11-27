using CoD2Unbound.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

/// <summary>A visible CD key field that groups characters in fours as the player types. Setup supplies its
/// own feedback, and the player card and Settings dialog use the editor's feedback line.</summary>
internal sealed class CdKeyEditor : StackPanel
{
    private readonly MainWindow shell;
    private readonly bool large;
    private readonly TextBox input = new() { PlaceholderText = "XXXX XXXX XXXX XXXX XXXX" };
    private readonly InfoBar? feedback;
    private bool formatting;

    internal string Value
    {
        get => input.Text;
        set => input.Text = CdKey.Display(value);
    }
    internal bool Verified => CdKey.IsValid(Value);
    internal CdKeyState State => CdKey.Check(Value).State;
    internal event Action? Edited;
    internal event Action? Submitted;

    internal CdKeyEditor(MainWindow shell, bool large = false)
    {
        this.shell = shell;
        this.large = large;
        Spacing = 10;
        AutomationProperties.SetName(input, "CD key");
        AutomationProperties.SetHelpText(input, "Twenty characters");
        var paste = new Button { Content = "Paste", VerticalAlignment = large ? VerticalAlignment.Center : VerticalAlignment.Bottom };
        AutomationProperties.SetName(paste, "Paste the CD key");
        paste.Click += async (_, _) => await shell.ShowProblemAsync(async () =>
        {
            var clipboard = Clipboard.GetContent();
            if (clipboard.Contains(StandardDataFormats.Text))
            {
                Value = (await clipboard.GetTextAsync()).Trim();
                input.Focus(FocusState.Programmatic);
                input.Select(input.Text.Length, 0);
            }
        });
        var row = Columns(8, new(1, GridUnitType.Star), GridLength.Auto);
        if (large)
        {
            input.Style = StyleFor("SetupKeyBox");
            input.ActualThemeChanged += (_, _) => Stroke();
            Put(row, 0, input);
        }
        else
        {
            input.Header = "CD key";
            Put(row, 0, input);
            feedback = new InfoBar { IsClosable = false };
        }
        Put(row, 1, paste);
        Children.Add(row);
        if (feedback is not null)
            Children.Add(feedback);
        input.KeyDown += (_, args) =>
        {
            if (args.Key == global::Windows.System.VirtualKey.Enter && Submitted is not null)
            {
                args.Handled = true;
                Submitted();
            }
        };
        input.TextChanged += (_, _) =>
        {
            if (formatting)
                return;
            Format();
            Render();
            Edited?.Invoke();
        };
        Render();
    }

    internal void FocusInput() => input.Focus(FocusState.Programmatic);

    /// <summary>Groups the key in fours while keeping the caret after the same character.</summary>
    private void Format()
    {
        string text = input.Text;
        string display = CdKey.Display(text);
        if (display == text)
            return;
        int caret = Math.Min(input.SelectionStart, text.Length);
        int before = text[..caret].Count(char.IsAsciiLetterOrDigit);
        int position = 0;
        for (int seen = 0; position < display.Length && seen < before; position++)
            if (char.IsAsciiLetterOrDigit(display[position]))
                seen++;
        formatting = true;
        try
        {
            input.Text = display;
            input.Select(Math.Min(position, display.Length), 0);
        }
        finally { formatting = false; }
    }

    private void Stroke()
    {
        if (!large)
            return;
        var state = State;
        if (shell.IsHighContrast || state is not (CdKeyState.Mismatch or CdKeyState.Verified))
            input.ClearValue(Control.BorderBrushProperty);
        else
            input.BorderBrush = ThemeBrush(state == CdKeyState.Mismatch ? "negative" : "positive", ActualTheme, false);
        AutomationProperties.SetItemStatus(input, state switch
        {
            CdKeyState.Mismatch => "The checksum doesn’t match",
            CdKeyState.Verified => "Checksum verified",
            _ => ""
        });
    }

    private void Render()
    {
        Stroke();
        if (feedback is null)
            return;
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
                "Spaces and dashes are fine. Find it on the manual, the disc sleeve, or in Steam under Manage › CD Keys.")
        };
    }

    internal static async Task<bool> ShowAsync(MainWindow shell, bool replacing, Func<string, Task<bool>> save)
    {
        var editor = new CdKeyEditor(shell);
        var dialog = new ContentDialog
        {
            XamlRoot = shell.DialogRoot, RequestedTheme = shell.DialogTheme,
            Title = replacing ? "Replace your CD key" : "Add your CD key",
            Content = new StackPanel
            {
                Spacing = 12,
                Children = { Caption(replacing ? "The key you have now keeps working until you press Replace." :
                    "The 20-character key that came with your game. Joining a server needs it."), editor }
            },
            PrimaryButtonText = replacing ? "Replace Key" : "Save Key",
            IsPrimaryButtonEnabled = false,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        }.WithSymbol("SystemPermissions");
        editor.Edited += () => dialog.IsPrimaryButtonEnabled = editor.Verified;
        dialog.Opened += (_, _) => editor.FocusInput();
        try { return await shell.ShowShellSheetAsync(dialog) == ContentDialogResult.Primary && await save(editor.Value); }
        finally { editor.Value = ""; }
    }
}

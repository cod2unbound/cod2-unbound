using CoD2Unbound.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

/// <summary>The in-game name with its coloured preview and the ten colour codes. Setup lays it out as the
/// Mac's Make it yours (the field, “Shows as” and Add Colours… on one line, the codes as chips); the
/// player card keeps its stacked layout.</summary>
internal sealed class PlayerNameEditor : UserControl
{
    private static readonly string[] Colors = ["Black", "Red", "Green", "Yellow", "Blue", "Cyan", "Pink", "White", "Allies team colour", "Axis team colour"];
    private static readonly string?[] ColorResources =
    [
        "TertiaryBrush", "GameRedBrush", "GameGreenBrush", "GameYellowBrush", "GameBlueBrush",
        "GameCyanBrush", "GameVioletBrush", "BodyBrush", "GameBlueBrush", "GameRedBrush"
    ];
    private static readonly string[] SwatchResources =
    [
        "GameBlackBrush", "GameRedBrush", "GameGreenBrush", "GameYellowBrush", "GameBlueBrush",
        "GameCyanBrush", "GameVioletBrush", "BodyBrush", "GameBlueBrush", "GameRedBrush"
    ];
    private readonly MainWindow shell;
    private readonly bool setup;
    private readonly TextBox input = new() { PlaceholderText = SetupPlayer.Unknown };
    private readonly TextBlock preview = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock problem = Caption("");
    private int cursor;
    private int length;
    private bool resetting;

    internal string NameText
    {
        get => input.Text;
        set
        {
            resetting = true;
            input.Text = value;
            resetting = false;
            Refresh();
        }
    }

    internal bool Valid => SetupPlayer.NameProblem(input.Text.Trim()) is null;
    internal event Action? Edited;
    internal event Action? Submitted;

    internal void FocusInput() => input.Focus(FocusState.Programmatic);
    internal void OpenColours() => openColours?.Invoke();
    private Action? openColours;

    internal PlayerNameEditor(MainWindow shell, bool setup = false)
    {
        this.shell = shell;
        this.setup = setup;
        input.Style = StyleFor("CapsuleTextBox");
        var layout = new StackPanel { Spacing = 8 };
        Content = layout;
        AutomationProperties.SetName(input, "Name other players see");
        AutomationProperties.SetName(preview, "Shows as");
        AutomationProperties.SetLiveSetting(problem, AutomationLiveSetting.Polite);
        var palette = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
        var swatches = setup
            ? (Panel)new VariableSizedWrapGrid { Orientation = Orientation.Horizontal }
            : new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        for (int index = 0; index < Colors.Length; index++)
        {
            string code = $"^{index}";
            string? brush = ColorResources[index];
            string swatchBrush = SwatchResources[index];
            var digit = new TextBlock { Text = setup ? code : index.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            var chip = setup
                ? new Border { Width = 10, Height = 10, CornerRadius = new(3), BorderThickness = new(1), VerticalAlignment = VerticalAlignment.Center }
                : new Border { Width = 9, Height = 9, CornerRadius = new(2), VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAccessibilityView(chip, AccessibilityView.Raw);
            var button = new Button
            {
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = setup ? 5 : 3, Children = { chip, digit } }
            };
            if (setup)
            {
                button.Style = StyleFor("SetupColourChip");
                button.Margin = new Thickness(0, 0, 6, 6);
            }
            else
                button.Padding = new Thickness(5, 4, 5, 4);
            AutomationProperties.SetName(button, $"Add {Colors[index]} ({code})");
            button.Loaded += (_, _) =>
            {
                if (shell.IsHighContrast)
                    return;
                chip.Background = ThemeBrush(setup ? swatchBrush : brush ?? swatchBrush, ActualTheme, false);
                if (setup)
                    chip.BorderBrush = ThemeBrush("wellStroke", ActualTheme, false);
            };
            button.Click += (_, _) =>
            {
                int start = Math.Min(cursor, input.Text.Length);
                int count = Math.Min(length, input.Text.Length - start);
                input.Focus(FocusState.Programmatic);
                input.Select(start, count);
                input.SelectedText = code;
                input.Select(Math.Min(start + code.Length, input.Text.Length), 0);
            };
            swatches.Children.Add(button);
        }
        palette.Children.Add(swatches);
        var paletteNote = Caption("Colour codes ^0 to ^9 colour the letters after them.");
        if (setup)
        {
            paletteNote.Style = StyleFor("SetupDetail");
            paletteNote.Margin = new Thickness(6, -6, 0, 0);
        }
        palette.Children.Add(paletteNote);
        // Setup draws the board's small capsule button; the player card keeps its link.
        Microsoft.UI.Xaml.Controls.Primitives.ButtonBase toggle = setup
            ? new Button { Content = "Add Colours…" }
            : new HyperlinkButton { Content = "Add Colours…", Padding = new Thickness(0) };
        toggle.Click += (_, _) => Toggle(palette.Visibility == Visibility.Collapsed);
        openColours = () => Toggle(true);

        void Toggle(bool open)
        {
            palette.Visibility = IsVisible(open);
            toggle.Content = open ? "Hide Colours" : "Add Colours…";
        }

        if (setup)
        {
            // Make it yours: the label, then the field, Shows as and the colours link on one line.
            var label = new TextBlock
            {
                Text = "Name other players see", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Style = StyleFor("SetupDetail"), Margin = new Thickness(6, 0, 0, 0)
            };
            AutomationProperties.SetAccessibilityView(label, AccessibilityView.Raw);
            preview.TextWrapping = TextWrapping.NoWrap;
            preview.TextTrimming = TextTrimming.CharacterEllipsis;
            preview.FontSize = 12;
            preview.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            preview.VerticalAlignment = VerticalAlignment.Center;
            var showsAs = new TextBlock { Text = "Shows as", Style = StyleFor("SetupDetail"), VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAccessibilityView(showsAs, AccessibilityView.Raw);
            var line = Columns(12, new(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto);
            Put(line, 0, input);
            Put(line, 1, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center,
                Children = { showsAs, preview }, MaxWidth = 200 });
            toggle.VerticalAlignment = VerticalAlignment.Center;
            Put(line, 2, toggle);
            problem.Foreground = ThemeBrush("negative", ActualTheme, shell.IsHighContrast);
            problem.Margin = new Thickness(6, 0, 0, 0);
            layout.Children.Add(label);
            layout.Children.Add(line);
            layout.Children.Add(problem);
            layout.Children.Add(palette);
            input.KeyDown += (_, args) =>
            {
                if (args.Key == global::Windows.System.VirtualKey.Enter && Submitted is not null)
                {
                    args.Handled = true;
                    Submitted();
                }
            };
        }
        else
        {
            input.Header = "Name other players see";
            var line = Columns(8, GridLength.Auto, new(1, GridUnitType.Star), GridLength.Auto);
            Put(line, 0, Caption("Shows as"));
            Put(line, 1, preview);
            Put(line, 2, toggle);
            layout.Children.Add(input);
            layout.Children.Add(line);
            layout.Children.Add(palette);
            layout.Children.Add(problem);
        }
        input.SelectionChanged += (_, _) =>
        {
            cursor = input.SelectionStart;
            length = input.SelectionLength;
        };
        input.TextChanged += (_, _) =>
        {
            Refresh();
            if (!resetting)
                Edited?.Invoke();
        };
        Loaded += (_, _) => Refresh();
        ActualThemeChanged += (_, _) => Refresh();
        Refresh();
    }

    private void Refresh()
    {
        preview.Inlines.Clear();
        string name = string.IsNullOrWhiteSpace(input.Text) ? SetupPlayer.Unknown : input.Text;
        foreach (var piece in QuakeColors.Runs(name))
        {
            var run = new Microsoft.UI.Xaml.Documents.Run { Text = piece.Text };
            if (!shell.IsHighContrast && ColorResources[piece.Code] is { } brush)
                run.Foreground = ThemeBrush(brush, ActualTheme, false);
            preview.Inlines.Add(run);
        }
        problem.Text = SetupPlayer.NameProblem(input.Text.Trim()) ?? "";
        problem.Visibility = IsVisible(problem.Text.Length > 0);
        if (setup && !shell.IsHighContrast)
            problem.Foreground = ThemeBrush("negative", ActualTheme, false);
    }
}

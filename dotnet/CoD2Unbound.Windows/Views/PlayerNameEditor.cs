using CoD2Unbound.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

internal sealed class PlayerNameEditor : UserControl
{
    private static readonly string[] Colors = ["Black", "Red", "Green", "Yellow", "Blue", "Cyan", "Pink", "White", "Allies team colour", "Axis team colour"];
    private static readonly string?[] ColorResources =
    [
        "TertiaryBrush", "GameRedBrush", "GameGreenBrush", "GameYellowBrush", "GameBlueBrush",
        "GameCyanBrush", "GameVioletBrush", "BodyBrush", "GameBlueBrush", "GameRedBrush"
    ];
    private readonly MainWindow shell;
    private readonly TextBox input = new() { Header = "Name other players see", PlaceholderText = SetupPlayer.Unknown };
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

    internal PlayerNameEditor(MainWindow shell)
    {
        this.shell = shell;
        var layout = new StackPanel { Spacing = 8 };
        Content = layout;
        AutomationProperties.SetName(input, "Name other players see");
        AutomationProperties.SetName(preview, "Shows as");
        AutomationProperties.SetLiveSetting(problem, AutomationLiveSetting.Polite);
        var palette = new StackPanel { Spacing = 6, Visibility = Visibility.Collapsed };
        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        for (int index = 0; index < Colors.Length; index++)
        {
            string code = $"^{index}";
            string? brush = ColorResources[index];
            var digit = new TextBlock { Text = index.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            var chip = new Border { Width = 9, Height = 9, CornerRadius = new(2), VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAccessibilityView(chip, AccessibilityView.Raw);
            var button = new Button
            {
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Children = { chip, digit } },
                Padding = new Thickness(5, 4, 5, 4)
            };
            AutomationProperties.SetName(button, $"Add {Colors[index]} ({code})");
            button.Loaded += (_, _) =>
            {
                if (brush is not null && !shell.IsHighContrast)
                    chip.Background = ThemeBrush(brush, ActualTheme, false);
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
        palette.Children.Add(Caption("Colour codes ^0 to ^9 colour the letters after them."));
        var toggle = new HyperlinkButton { Content = "Add colours…", Padding = new Thickness(0) };
        toggle.Click += (_, _) =>
        {
            bool open = palette.Visibility == Visibility.Collapsed;
            palette.Visibility = IsVisible(open);
            toggle.Content = open ? "Hide colours" : "Add colours…";
        };

        var line = Columns(8, GridLength.Auto, new(1, GridUnitType.Star), GridLength.Auto);
        Put(line, 0, Caption("Shows as"));
        Put(line, 1, preview);
        Put(line, 2, toggle);
        layout.Children.Add(input);
        layout.Children.Add(line);
        layout.Children.Add(palette);
        layout.Children.Add(problem);
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
    }
}

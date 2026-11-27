using System.Numerics;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

using static CoD2Unbound.Windows.Views.PagePresentation;

namespace CoD2Unbound.Windows.Views;

internal enum SetupStatus { Ready, NeedsChange, Off }
internal enum SetupBannerTone { Neutral, Error }

/// <summary>The Mac's Setup pieces (SetupView.swift): glass panel, well group, symbol-tile rows, notes,
/// status labels, banners and the step capsules.</summary>
public sealed partial class SetupPage
{
    private static readonly Dictionary<string, string> Icons = new()
    {
        ["folder"] = "Folder", ["lock"] = "LockClosed", ["eye"] = "Eye",
        ["share"] = "Share", ["person"] = "Person", ["check"] = "CheckCircle",
        ["download"] = "ArrowDownload", ["shield"] = "ShieldCheckmark",
        ["key"] = "ShieldLock", ["warning"] = "Warning", ["info"] = "Info",
        ["off"] = "Subtract", ["sync"] = "ArrowSync", ["people"] = "People"
    };

    internal const double PanelWidth = 660, NarrowPanelWidth = 600;
    private double panelWidth = PanelWidth;

    private Brush Token(string key) => ThemeBrush(key, ActualTheme, shell.IsHighContrast);

    internal static FontIcon Symbol(string name, double size)
    {
        var icon = Fluent(Icons[name], size);
        AutomationProperties.SetAccessibilityView(icon, AccessibilityView.Raw);
        return icon;
    }

    /// <summary>Gives a stock RadioButtons list's items one width (the list's, split across its columns) and the
    /// tallest item's height. The control arranges each item at its own desired size, so stretch alignment does
    /// not fill a cell; each item is measured at the shared width and takes the tallest as its MinHeight, so
    /// text scaling still grows them together. Arrow keys, roving focus and selection stay the control's.</summary>
    private static void ShareCells(RadioButtons list, int columns, double gap, double floor = 0) =>
        list.SizeChanged += (_, args) =>
        {
            var items = list.Items.OfType<RadioButton>().ToList();
            if (items.Count == 0 || args.NewSize.Width <= 0)
                return;
            int across = Math.Max(1, Math.Min(columns, items.Count));
            double width = Math.Max(0, Math.Floor((args.NewSize.Width - gap * (across - 1)) / across));
            double tallest = floor;
            foreach (var item in items)
            {
                item.Width = width;
                item.ClearValue(FrameworkElement.MinHeightProperty);
                item.Measure(new global::Windows.Foundation.Size(width, double.PositiveInfinity));
                tallest = Math.Max(tallest, Math.Ceiling(item.DesiredSize.Height));
            }
            foreach (var item in items)
                if (item.MinHeight != tallest)
                    item.MinHeight = tallest;
        };

    /// <summary>The card under each heading: glass at radius 22, padding as the Mac's panel() call.</summary>
    private Grid GlassPanel(double padding, double spacing, params UIElement[] children)
    {
        var stack = new StackPanel { Spacing = spacing, Padding = new Thickness(padding) };
        foreach (var child in children)
            stack.Children.Add(child);
        var glass = new Border { Style = StyleFor("SetupPanel") };
        var panel = new Grid { Width = panelWidth, HorizontalAlignment = HorizontalAlignment.Center, Children = { glass, stack } };
        glass.Shadow = new ThemeShadow();
        glass.Translation = new Vector3(0, 0, 24);
        return panel;
    }

    /// <summary>A well group; rows are separated by a hairline inset past the symbol tile.</summary>
    private StackPanel GroupRows(double inset, params UIElement[] rows)
    {
        var stack = new StackPanel();
        foreach (var row in rows)
        {
            if (stack.Children.Count > 0)
                stack.Children.Add(new Border { Height = 1, Margin = new Thickness(inset, 0, 0, 0), Background = Token("separator") });
            stack.Children.Add(row);
        }
        return stack;
    }

    private Border Group(double inset, params UIElement[] rows) =>
        new() { Style = StyleFor("SetupGroup"), Child = GroupRows(inset, rows) };

    private Border Group(params UIElement[] rows) => Group(58, rows);

    private Border Tile(string symbol, bool negative = false)
    {
        var icon = Symbol(symbol, 15);
        icon.Foreground = Token(negative ? "negative" : "accent");
        return new Border
        {
            Style = StyleFor("RowSymbol"), VerticalAlignment = VerticalAlignment.Center, Child = icon
        };
    }

    /// <summary>A group row: tile, title and detail, then the trailing pieces. 60 tall with a button.</summary>
    /// <summary>A row as the Mac's SetupView draws it: 16 in, 12 between tile and text, 10 above and below,
    /// at least 56 tall (60 with a button so it sits clear of the group's corner), the tile centred.</summary>
    private Grid Row(string symbol, string title, string detail, FrameworkElement? trailing = null, bool mono = false,
        bool negative = false, FrameworkElement? status = null)
    {
        FrameworkElement[] ends = [.. new[] { status, trailing }.OfType<FrameworkElement>()];
        var row = Columns(12, GridLength.Auto, new(1, GridUnitType.Star), GridLength.Auto);
        row.Padding = new Thickness(16, 10, 16, 10);
        row.MinHeight = ends.Any(item => item is Button) ? 60 : 56;
        Put(row, 0, Tile(symbol, negative));
        var heading = new TextBlock { Text = title, Style = StyleFor(mono ? "SetupMonoTitle" : "SetupRowTitle") };
        if (mono)
            ToolTipService.SetToolTip(heading, title);
        var lines = new StackPanel
        {
            Spacing = 3, VerticalAlignment = VerticalAlignment.Center,
            Children = { heading, new TextBlock { Text = detail, Style = StyleFor("SetupDetail") } }
        };
        AutomationProperties.SetName(lines, $"{title}. {detail}");
        Put(row, 1, lines);
        if (ends.Length > 0)
        {
            var end = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            foreach (var item in ends)
            {
                item.VerticalAlignment = VerticalAlignment.Center;
                end.Children.Add(item);
            }
            Put(row, 2, end);
        }
        return row;
    }

    private Grid Note(string text, string? symbol = null)
    {
        var words = new TextBlock { Text = text, Style = StyleFor("SetupDetail") };
        var line = symbol is null ? new Grid() : Columns(6, GridLength.Auto, new(1, GridUnitType.Star));
        if (symbol is not null)
        {
            var icon = Symbol(symbol, 11);
            icon.Foreground = Token("secondary");
            icon.VerticalAlignment = VerticalAlignment.Top;
            line.Children.Add(icon);
            Grid.SetColumn(words, 1);
            GlyphAlign.SetIsEnabled(line, true);
        }
        line.Children.Add(words);
        AutomationProperties.SetName(line, text);
        return line;
    }

    private StackPanel Status(SetupStatus kind, string text)
    {
        var brush = Token(kind switch { SetupStatus.Ready => "positive", SetupStatus.NeedsChange => "caution", _ => "secondary" });
        var icon = Symbol(kind switch { SetupStatus.Ready => "check", SetupStatus.NeedsChange => "warning", _ => "off" }, 13);
        icon.Foreground = brush;
        var label = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                icon,
                new TextBlock { Text = text, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = brush }
            }
        };
        GlyphAlign.SetIsEnabled(label, true);
        AutomationProperties.SetName(label, text);
        return label;
    }

    /// <summary>The Mac's Banner: the well at radius 14, the error tone's stroke the negative colour at 55%.
    /// Archive names in the message are set in the mono face, as on the board.</summary>
    private Border Banner(SetupBannerTone tone, string symbol, string message, FrameworkElement? trailing = null)
    {
        var icon = Symbol(symbol, 14);
        icon.Foreground = Token(tone == SetupBannerTone.Error ? "negative" : "secondary");
        icon.VerticalAlignment = VerticalAlignment.Top;
        var words = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 18.2, Foreground = Token("text") };
        foreach (var part in Regex.Split(message, @"(\S+\.iwd)"))
        {
            if (part.Length == 0)
                continue;
            var run = new Run { Text = part };
            if (part.EndsWith(".iwd", StringComparison.Ordinal))
                run.FontFamily = (FontFamily)Application.Current.Resources["CourierPrime"];
            words.Inlines.Add(run);
        }
        var layout = Columns(10, GridLength.Auto, new(1, GridUnitType.Star), GridLength.Auto);
        Put(layout, 0, icon);
        Put(layout, 1, words);
        GlyphAlign.SetIsEnabled(layout, true);
        if (trailing is not null)
        {
            trailing.VerticalAlignment = VerticalAlignment.Center;
            Put(layout, 2, trailing);
        }
        var banner = new Border { Style = StyleFor("SetupBanner"), Child = layout };
        if (tone == SetupBannerTone.Error && !shell.IsHighContrast && Token("negative") is SolidColorBrush negative)
            banner.BorderBrush = new SolidColorBrush(negative.Color) { Opacity = 0.55 };
        AutomationProperties.SetName(banner, (tone == SetupBannerTone.Error ? "Problem: " : "") + message);
        AutomationProperties.SetLiveSetting(banner, AutomationLiveSetting.Polite);
        return banner;
    }

    private static Button Action(string label, RoutedEventHandler click, string? automationId = null)
    {
        var button = new Button { Content = label };
        button.Click += click;
        if (automationId is not null)
            AutomationProperties.SetAutomationId(button, automationId);
        return button;
    }

    /// <summary>One capsule per shown step: the current one 26 wide in the accent, done ones in the positive colour.</summary>
    private void RenderDots()
    {
        var steps = Setup.Steps;
        int current = Setup.StepNumber;
        StepDots.Children.Clear();
        bool contrast = shell.IsHighContrast;
        for (int position = 1; position <= steps.Count; position++)
        {
            // In a contrast theme the track colour is the window's own, so steps still ahead take the theme's
            // grey-text colour instead of vanishing.
            string key = position == current ? "accent" : position < current ? "positive" : contrast ? "secondary" : "track";
            StepDots.Children.Add(new Border
            {
                Width = position == current ? 26 : 14, Height = 5, CornerRadius = new CornerRadius(2.5),
                Background = ThemeBrush(key, ActualTheme, contrast)
            });
        }
        string title = StepTitle(Setup.Step);
        StepLabel.Text = (session.ReviewingSetup ? "Setting up again · " : "") + $"Step {current} of {steps.Count}";
        AutomationProperties.SetName(StepDots, $"{StepLabel.Text}, {title}");
        AutomationProperties.SetAccessibilityView(StepDots, AccessibilityView.Content);
    }

    internal static string StepTitle(CoD2Unbound.Core.SetupStep step) => step switch
    {
        CoD2Unbound.Core.SetupStep.Data => "Game data", CoD2Unbound.Core.SetupStep.Key => "CD key",
        CoD2Unbound.Core.SetupStep.Personalize => "Make it yours", CoD2Unbound.Core.SetupStep.CrashReports => "Crash reports",
        CoD2Unbound.Core.SetupStep.UsageData => "Usage data", CoD2Unbound.Core.SetupStep.Ready => "Ready", _ => "Welcome"
    };

    private readonly Image SceneArt = new() { Stretch = Stretch.UniformToFill, VerticalAlignment = VerticalAlignment.Top };
    private readonly Border SceneEdge = new() { VerticalAlignment = VerticalAlignment.Top };
    private readonly Border Vignette = new();
    private readonly Grid Backdrop;

    /// <summary>The scene, handed to the shell's backdrop host so it fills the whole window.</summary>
    /// <summary>SetupBackdrop: the scene with the board's sky, edge and vignette, every colour from the tokens.</summary>
    private void RenderBackdrop()
    {
        bool light = ActualTheme == ElementTheme.Light;
        bool contrast = shell.IsHighContrast;
        SceneArt.Visibility = IsVisible(!contrast);
        SceneEdge.Visibility = IsVisible(light && !contrast);
        Vignette.Visibility = IsVisible(!contrast);
        var ground = Token("ground");
        if (contrast || ground is not SolidColorBrush groundBrush)
        {
            Backdrop.Background = ground;
            return;
        }
        var g = groundBrush.Color;
        SceneArt.Source = new SvgImageSource(new Uri($"ms-appx:///Assets/hero-{(light ? "light" : "dark")}.svg"));
        if (light)
            Backdrop.Background = ground;
        else
        {
            var blue = ((SolidColorBrush)Token("GameBlueBrush")).Color;
            var sky = Mix(blue, g, 0.07);
            Backdrop.Background = new LinearGradientBrush
            {
                StartPoint = new(0, 0), EndPoint = new(0, 1),
                GradientStops =
                {
                    new GradientStop { Color = sky, Offset = 0 }, new GradientStop { Color = sky, Offset = 180 / 900.0 },
                    new GradientStop { Color = g, Offset = 780 / 900.0 }
                }
            };
        }
        SceneEdge.Background = new LinearGradientBrush
        {
            StartPoint = new(0, 0), EndPoint = new(0, 1),
            GradientStops =
            {
                new GradientStop { Color = g, Offset = 0 }, new GradientStop { Color = Alpha(g, 0), Offset = 0.22 },
                new GradientStop { Color = Alpha(g, 0), Offset = 0.76 }, new GradientStop { Color = g, Offset = 1 }
            }
        };
        Vignette.Background = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.RelativeToBoundingBox, Center = new(0.5, 0.46), GradientOrigin = new(0.5, 0.46),
            RadiusX = 0.7, RadiusY = 0.62,
            GradientStops =
            {
                new GradientStop { Color = Alpha(g, 0.86), Offset = 0 }, new GradientStop { Color = Alpha(g, 0.66), Offset = 0.6 },
                new GradientStop { Color = Alpha(g, 0.40), Offset = 1 }
            }
        };

        static global::Windows.UI.Color Mix(global::Windows.UI.Color a, global::Windows.UI.Color b, double share) => global::Windows.UI.Color.FromArgb(255,
            (byte)Math.Round(a.R * share + b.R * (1 - share)), (byte)Math.Round(a.G * share + b.G * (1 - share)),
            (byte)Math.Round(a.B * share + b.B * (1 - share)));
        static global::Windows.UI.Color Alpha(global::Windows.UI.Color color, double alpha) =>
            global::Windows.UI.Color.FromArgb((byte)Math.Round(255 * alpha), color.R, color.G, color.B);
    }

    /// <summary>The scene sits where the board puts it: from 19% of the window's height, 69% tall.</summary>
    private void PlaceScene(double height)
    {
        SceneArt.Margin = new Thickness(0, height * 170 / 900, 0, 0);
        SceneArt.Height = SceneEdge.Height = height * 620 / 900;
        SceneEdge.Margin = SceneArt.Margin;
    }
}

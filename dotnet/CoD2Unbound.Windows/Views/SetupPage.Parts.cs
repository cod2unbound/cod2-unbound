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
    // Bundled Fluent glyphs where the subset has them; the system's Fluent icon font for the rest.
    private static readonly Dictionary<string, (string Glyph, bool System)> Icons = new()
    {
        ["folder"] = ("", false), ["lock"] = ("", false), ["eye"] = ("", false),
        ["share"] = ("", false), ["person"] = ("", false), ["check"] = ("", false),
        ["download"] = ("", false), ["shield"] = ("", false),
        ["key"] = ("", true), ["warning"] = ("", true), ["info"] = ("", true),
        ["off"] = ("", true), ["sync"] = ("", true), ["people"] = ("", true)
    };

    internal const double PanelWidth = 660, NarrowPanelWidth = 600;
    private double panelWidth = PanelWidth;

    private Brush Token(string key) => ThemeBrush(key, ActualTheme, shell.IsHighContrast);

    internal static FontIcon Symbol(string name, double size)
    {
        var (glyph, system) = Icons[name];
        var icon = new FontIcon
        {
            Glyph = glyph, FontSize = size,
            FontFamily = (FontFamily)Application.Current.Resources[system ? "SetupSystemIcons" : "FluentRegular"]
        };
        AutomationProperties.SetAccessibilityView(icon, AccessibilityView.Raw);
        return icon;
    }

    /// <summary>The card under each heading: glass at radius 22, padding as the Mac's panel() call.</summary>
    private Grid GlassPanel(double padding, double spacing, params UIElement[] children)
    {
        var stack = new StackPanel { Spacing = spacing, Padding = new Thickness(padding) };
        foreach (var child in children)
            stack.Children.Add(child);
        var glass = new Border { Style = StyleFor("SetupPanel") };
        var highlight = new Border { Style = StyleFor("SetupGlassHighlight"), CornerRadius = glass.CornerRadius, Margin = new Thickness(1) };
        var panel = new Grid { Width = panelWidth, HorizontalAlignment = HorizontalAlignment.Center, Children = { glass, highlight, stack } };
        glass.Shadow = new ThemeShadow();
        glass.Translation = new Vector3(0, 0, 24);
        return panel;
    }

    /// <summary>A well group; rows are separated by a hairline inset past the symbol tile.</summary>
    private Border Group(double inset, params UIElement[] rows)
    {
        var stack = new StackPanel();
        foreach (var row in rows)
        {
            if (stack.Children.Count > 0)
                stack.Children.Add(new Border { Height = 1, Margin = new Thickness(inset, 0, 0, 0), Background = Token("separator") });
            stack.Children.Add(row);
        }
        return new Border { Style = StyleFor("SetupGroup"), Child = stack };
    }

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
    /// <summary>A row as the board measures it: 16 in, 14 between tile and text, 9 above and below with the
    /// tile centred; <paramref name="tall"/> rows (the questions' several-line answers) pad 12 and keep the
    /// tile level with the title.</summary>
    private Grid Row(string symbol, string title, string detail, FrameworkElement? trailing = null, bool mono = false,
        bool negative = false, FrameworkElement? status = null, bool tall = false)
    {
        FrameworkElement[] ends = [.. new[] { status, trailing }.OfType<FrameworkElement>()];
        var row = Columns(14, GridLength.Auto, new(1, GridUnitType.Star), GridLength.Auto);
        row.Padding = new Thickness(16, tall ? 12 : 9, 16, tall ? 12 : 9);
        var tile = Tile(symbol, negative);
        if (tall)
            tile.VerticalAlignment = VerticalAlignment.Top;
        Put(row, 0, tile);
        var heading = new TextBlock { Text = title, Style = StyleFor(mono ? "SetupMonoTitle" : "SetupRowTitle") };
        if (mono)
            ToolTipService.SetToolTip(heading, title);
        var lines = new StackPanel
        {
            Spacing = 3, VerticalAlignment = tall ? VerticalAlignment.Top : VerticalAlignment.Center,
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

    private StackPanel Note(string text, string? symbol = null)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (symbol is not null)
        {
            var icon = Symbol(symbol, 11);
            icon.Foreground = Token("secondary");
            icon.VerticalAlignment = VerticalAlignment.Top;
            icon.Margin = new Thickness(0, 3, 0, 0);
            line.Children.Add(icon);
        }
        var words = new TextBlock { Text = text, Style = StyleFor("SetupDetail") };
        line.Children.Add(words);
        if (symbol is not null)
            words.MaxWidth = panelWidth - 60;
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
        icon.Margin = new Thickness(0, 2, 0, 0);
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
        for (int position = 1; position <= steps.Count; position++)
        {
            StepDots.Children.Add(new Border
            {
                Width = position == current ? 26 : 14, Height = 5, CornerRadius = new CornerRadius(2.5),
                Background = Token(position == current ? "accent" : position < current ? "positive" : "track")
            });
        }
        string title = StepTitle(Setup.Step);
        AutomationProperties.SetName(StepDots, $"Step {current} of {steps.Count}, {title}");
        AutomationProperties.SetAccessibilityView(StepDots, AccessibilityView.Content);
        StepLabel.Text = (session.ReviewingSetup ? "Setting up again · " : "") + $"Step {current} of {steps.Count}";
    }

    internal static string StepTitle(CoD2Unbound.Core.SetupStep step) => step switch
    {
        CoD2Unbound.Core.SetupStep.Data => "Game data", CoD2Unbound.Core.SetupStep.Key => "CD key",
        CoD2Unbound.Core.SetupStep.Personalize => "Make it yours", CoD2Unbound.Core.SetupStep.CrashReports => "Crash reports",
        CoD2Unbound.Core.SetupStep.UsageData => "Usage data", CoD2Unbound.Core.SetupStep.Ready => "Ready", _ => "Welcome"
    };

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

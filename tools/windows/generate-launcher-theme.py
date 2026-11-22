"""Translate the approved design system JSON into WinUI theme resources."""
import argparse
import json
import re
from decimal import Decimal, ROUND_HALF_UP
from pathlib import Path
from xml.etree import ElementTree as ET

# A native control keeps its own template and state transitions.
BRUSHES = {
    "PanelBrush": ("panel", "Window"),
    "PanelStrokeBrush": ("panel-stroke", "WindowText"),
    "FieldBrush": ("field", "Window"),
    "SeparatorBrush": ("separator", "WindowText"),
    "HeadingBrush": ("heading", "WindowText"),
    "HeroInkBrush": ("ink-on-art", "WindowText"),
    "AccentFillColorDefaultBrush": ("prominent-bottom", "Highlight"),
    "AccentFillColorSecondaryBrush": ("prominent-top", "Highlight"),
    "AccentFillColorTertiaryBrush": ("control", "Highlight"),
    "AccentFillColorDisabledBrush": ("control", "GrayText"),
    "AccentTextFillColorPrimaryBrush": ("link", "Hotlight"),
    "AccentTextFillColorSecondaryBrush": ("link", "Hotlight"),
    "AccentTextFillColorTertiaryBrush": ("link", "Hotlight"),
    "TextOnAccentFillColorPrimaryBrush": ("prominent-text", "HighlightText"),
    "AccentFillColorSelectedTextBackgroundBrush": ("selection-stroke", "Highlight"),
    "HyperlinkButtonForeground": ("link", "Hotlight"),
    "HyperlinkButtonForegroundPointerOver": ("link", "Hotlight"),
    "HyperlinkButtonForegroundPressed": ("link", "Hotlight"),
    "HyperlinkForeground": ("link", "Hotlight"),
    "HyperlinkForegroundPointerOver": ("link", "Hotlight"),
    "HyperlinkForegroundPressed": ("link", "Hotlight"),
    "InfoBarHyperlinkButtonForeground": ("link", "Hotlight"),
    "ListViewItemSelectionIndicatorBrush": ("control", "HighlightText"),
    "ListViewItemSelectionIndicatorPointerOverBrush": ("control", "HighlightText"),
    "ListViewItemSelectionIndicatorPressedBrush": ("control", "HighlightText"),
    "LauncherSelectionPillBrush": ("control", "HighlightText"),
    "TextControlSelectionHighlightColor": ("selection-stroke", "Highlight"),
    "SliderThumbBackground": ("prominent-bottom", "Highlight"),
    "SliderThumbBackgroundPointerOver": ("prominent-top", "Highlight"),
    "SliderThumbBackgroundPressed": ("control", "Hotlight"),
    "SliderThumbBackgroundDisabled": ("control", "GrayText"),
    "SliderTrackValueFill": ("prominent-bottom", "Highlight"),
    "SliderTrackValueFillPointerOver": ("prominent-top", "Highlight"),
    "SliderTrackValueFillPressed": ("control", "Hotlight"),
    "SliderTrackValueFillDisabled": ("control", "GrayText"),
    "CheckBoxCheckBackgroundFillChecked": ("prominent-bottom", "Highlight"),
    "CheckBoxCheckBackgroundStrokeChecked": ("prominent-bottom", "Highlight"),
    "CheckBoxCheckBackgroundFillIndeterminate": ("prominent-bottom", "Highlight"),
    "CheckBoxCheckBackgroundStrokeIndeterminate": ("prominent-bottom", "Highlight"),
    "CheckBoxCheckBackgroundFillCheckedPointerOver": ("prominent-top", "Highlight"),
    "CheckBoxCheckBackgroundStrokeCheckedPointerOver": ("prominent-top", "Highlight"),
    "CheckBoxCheckBackgroundFillIndeterminatePointerOver": ("prominent-top", "Highlight"),
    "CheckBoxCheckBackgroundStrokeIndeterminatePointerOver": ("prominent-top", "Highlight"),
    "CheckBoxCheckBackgroundFillCheckedPressed": ("control", "Highlight"),
    "CheckBoxCheckBackgroundStrokeCheckedPressed": ("control", "Highlight"),
    "CheckBoxCheckBackgroundFillIndeterminatePressed": ("control", "Highlight"),
    "CheckBoxCheckBackgroundStrokeIndeterminatePressed": ("control", "Highlight"),
    "CheckBoxCheckBackgroundFillCheckedDisabled": ("control", "GrayText"),
    "CheckBoxCheckBackgroundStrokeCheckedDisabled": ("control", "GrayText"),
    "CheckBoxCheckBackgroundFillIndeterminateDisabled": ("control", "GrayText"),
    "CheckBoxCheckBackgroundStrokeIndeterminateDisabled": ("control", "GrayText"),
    "CheckBoxCheckGlyphForegroundChecked": ("prominent-text", "HighlightText"),
    "CheckBoxCheckGlyphForegroundCheckedPointerOver": ("prominent-text", "HighlightText"),
    "CheckBoxCheckGlyphForegroundCheckedPressed": ("prominent-text", "HighlightText"),
    "CheckBoxCheckGlyphForegroundIndeterminate": ("prominent-text", "HighlightText"),
    "CheckBoxCheckGlyphForegroundIndeterminatePointerOver": ("prominent-text", "HighlightText"),
    "CheckBoxCheckGlyphForegroundIndeterminatePressed": ("prominent-text", "HighlightText"),
    "HeroAccentBrush": ("accent", "WindowText"),
    "FavoriteBrush": ("control", "WindowText"),
    "BodyBrush": ("text", "WindowText"),
    "CaptionBrush": ("secondary", "WindowText"),
    "AddressBrush": ("address", "WindowText"),
    "TertiaryBrush": ("tertiary", "WindowText"),
    "ChipIconBrush": ("chip-icon", "WindowText"),
    "GameRedBrush": ("game-red", "WindowText"),
    "GameGreenBrush": ("game-green", "WindowText"),
    "GameYellowBrush": ("game-yellow", "WindowText"),
    "GameBlueBrush": ("game-blue", "WindowText"),
    "GameCyanBrush": ("game-cyan", "WindowText"),
    "GameVioletBrush": ("game-violet", "WindowText"),
    "EditionStrokeBrush": ("selection-stroke", "WindowText"),
    "LinkBrush": ("link", "Hotlight"),
    "PositiveBrush": ("positive", "WindowText"),
    "ControlFillBrush": ("button-fill", "ButtonFace"),
    "ControlStrokeBrush": ("button-stroke", "ButtonText"),
    "NegativeBrush": ("negative", "WindowText"),
    "SelectorBarItemPillFill": ("control", "Highlight"),
    "ToggleSwitchFillOn": ("control", "Highlight"),
    "ToggleSwitchFillOnPointerOver": ("control", "Highlight"),
    "ToggleSwitchFillOnPressed": ("control", "Highlight"),
    "FocusVisualPrimaryBrush": ("heading", "WindowText"),
    "FocusVisualSecondaryBrush": ("ground", "Window"),
    "NavigationViewSelectionIndicatorForeground": ("control", "Highlight"),
    "AccentButtonBackground": ("prominent-bottom", "Highlight"),
    "AccentButtonBackgroundPointerOver": ("prominent-top", "Highlight"),
    "AccentButtonBackgroundPressed": ("control", "Highlight"),
    "AccentButtonForeground": ("prominent-text", "HighlightText"),
    "AccentButtonForegroundPointerOver": ("prominent-text", "HighlightText"),
    "AccentButtonForegroundPressed": ("prominent-text", "HighlightText"),
}


def xaml_color(value):
    if re.fullmatch(r"#[0-9a-fA-F]{6}", value):
        return value.upper()
    match = re.fullmatch(r"rgba\(\s*(\d+),\s*(\d+),\s*(\d+),\s*([\d.]+)\s*\)", value)
    if not match:
        raise ValueError(f"Unsupported color: {value}")
    red, green, blue = map(int, match.groups()[:3])
    opacity = Decimal(match[4])
    if max(red, green, blue) > 255 or not 0 <= opacity <= 1:
        raise ValueError(f"Out-of-range color: {value}")
    alpha = int((opacity * 255).quantize(Decimal(1), rounding=ROUND_HALF_UP))
    return f"#{alpha:02X}{red:02X}{green:02X}{blue:02X}"


def generate(tokens):
    root = ET.Element("ResourceDictionary", {
        "xmlns": "http://schemas.microsoft.com/winfx/2006/xaml/presentation",
        "xmlns:x": "http://schemas.microsoft.com/winfx/2006/xaml",
    })
    root.append(ET.Comment(" Generated by tools/windows/generate-launcher-theme.py from the approved tokens.json. "))
    themes = ET.SubElement(root, "ResourceDictionary.ThemeDictionaries")
    for key, theme in (("Dark", "dark"), ("Light", "light"), ("HighContrast", None)):
        dictionary = ET.SubElement(themes, "ResourceDictionary", {"x:Key": key})
        for brush, (token, system_color) in BRUSHES.items():
            color = xaml_color(tokens[token][theme]) if theme else f"{{ThemeResource SystemColor{system_color}Color}}"
            ET.SubElement(dictionary, "SolidColorBrush", {"x:Key": brush, "Color": color})
        if theme:
            ET.SubElement(dictionary, "SolidColorBrush", {
                "x:Key": "InfoBarInformationalSeverityIconBackground", "Color": xaml_color(tokens["control"][theme]),
            })
            # Mirror the native TextBox gradient: the flipped two-pixel region
            # paints only the bottom edge of the focused border in the accent.
            focused = ET.SubElement(dictionary, "LinearGradientBrush", {
                "x:Key": "TextControlBorderBrushFocused",
                "MappingMode": "Absolute", "StartPoint": "0,0", "EndPoint": "0,2",
            })
            transform = ET.SubElement(focused, "LinearGradientBrush.RelativeTransform")
            ET.SubElement(transform, "ScaleTransform", {"ScaleY": "-1", "CenterY": "0.5"})
            stops = ET.SubElement(focused, "LinearGradientBrush.GradientStops")
            for token in ("control", "button-stroke"):
                ET.SubElement(stops, "GradientStop", {"Offset": "1", "Color": xaml_color(tokens[token][theme])})
        else:
            for control in ("ListViewItem", "NavigationViewItem"):
                for state in ("Selected", "SelectedPointerOver", "SelectedPressed"):
                    ET.SubElement(dictionary, "SolidColorBrush", {
                        "x:Key": f"{control}Foreground{state}", "Color": "{ThemeResource SystemColorHighlightTextColor}",
                    })
        # The contrast backplate covers text only, leaving the rest of the art visible.
        backplate = "Transparent" if theme else "{ThemeResource SystemColorWindowColor}"
        ET.SubElement(dictionary, "SolidColorBrush", {"x:Key": "HeroBackplateBrush", "Color": backplate})
        frame = "Transparent" if theme else "{ThemeResource SystemColorWindowColor}"
        for brush in ("FrameContrastBrush", "NavigationViewExpandedPaneBackground"):
            ET.SubElement(dictionary, "SolidColorBrush", {"x:Key": brush, "Color": frame})
    ET.indent(root, space="    ")
    return ET.tostring(root, encoding="unicode") + "\n"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("tokens", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    data = json.loads(args.tokens.read_text(encoding="utf-8"))
    args.output.write_text(generate({item["name"]: item["value"] for item in data["color"]["tokens"]}), encoding="utf-8")


if __name__ == "__main__":
    main()

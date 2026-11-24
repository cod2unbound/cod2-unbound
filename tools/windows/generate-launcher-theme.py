"""Generate WinUI theme resources from the Mac palette and approved WinParts metrics."""
import argparse
import re
from decimal import Decimal, ROUND_HALF_UP
from pathlib import Path
from xml.etree import ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
OUTPUT = ROOT / "dotnet/CoD2Unbound.Windows/Themes/DesignTokens.xaml"
PALETTE = ROOT / "launcher/Theme.swift"
ARTWORK = ROOT / "launcher/Artwork.swift"
COMPONENTS = ROOT / "launcher/Components.swift"
HOME = ROOT / "launcher/HomeView.swift"

# Values specified by WinParts/launcher.css. Where there is a directly equivalent
# Swift constant, read it below. The board specifies the Windows DIP geometry.
RADII = dict(ProgressTrack=4, Sheet=26, Sidebar=22, Group=20, Popover=20, Crate=16, Banner=14,
             Menu=14, Plate=14, NavItem=12, Well=12, IconTile=8, InlineField=7,
             MenuItem=7, Badge=6, Keycap=6)
TYPE = {
    "Page": (30, "Bold", 33), "Section": (22, "Bold", 25.3),
    "Group": (20, "Bold", 27), "Panel": (17, "SemiBold", 22.95),
    "Card": (15, "SemiBold", 20.25), "Body": (14, "Normal", 18.9),
    "Subtitle": (13, "Normal", 18.85), "Help": (12, "Normal", 16.8),
    "Label": (11, "SemiBold", 14.85), "Eyebrow": (13, "Bold", 13),
}
BOARD_METRICS = dict(ButtonSmallHeight=24, ButtonRegularHeight=28, ButtonHeroHeight=54,
                     FieldInlineHeight=28, FieldCapsuleHeight=36, CircleHeight=36,
                     SegmentHeight=28, SegmentLargeHeight=36, SwitchWidth=40, SwitchHeight=24,
                     SwitchKnobSize=20, SwitchTravel=16, ProgressTrackHeight=8, BadgeHeight=18, ChipHeight=26, LargeChipHeight=40,
                     SidebarWidth=232, SidebarInset=10, NavHeight=38, CaptionHeight=32,
                     GlassShadowBlur=48, GlassShadowOffset=18,
                     FocusThickness=3, FocusOffset=2)
BOARD_COLORS = {
    "glassSolid": ("#181A14", "#F6F1E4", "Window"),
    "glassShadow": ("#5C000000", "#243C3214", None),
    "scrim": ("#8C000000", "#613C321E", None),
    "prominentEdge": ("#8CFFECBE", "#4DFFFFFF", "ButtonText"),
    "prominentGlow": ("#57C4984C", "#4746522A", None),
    "systemSheet": ("#282727", "#FFFFFF", "Window"),
}
TRANSPARENT = "#00000000"

# XAML template keys are adapters for native state machines. Palette keys remain
# available under their Swift names, in addition to existing screen aliases.
ALIASES = {
    "ground": "GroundBrush", "panel": "PanelBrush",
    "panelStroke": "PanelStrokeBrush", "field": "FieldBrush",
    "separator": "SeparatorBrush", "heading": "HeadingBrush",
    "inkOnArt": "HeroInkBrush", "accent": "HeroAccentBrush",
    "control": "FavoriteBrush", "text": "BodyBrush", "secondary": "CaptionBrush",
    "address": "AddressBrush", "tertiary": "TertiaryBrush", "chipIcon": "ChipIconBrush",
    "selectionStroke": "EditionStrokeBrush", "link": "LinkBrush",
    "positive": "PositiveBrush", "negative": "NegativeBrush",
    "button": "ControlFillBrush", "buttonStroke": "ControlStrokeBrush",
}
WINDOW_FILLS = set("ground panel crate well field track hover press glassFill".split())
EDGES = set("panelStroke wellStroke glassStroke glassHighlight buttonStroke separator".split())


def xaml_color(hex_rgb, opacity="1"):
    alpha = int((Decimal(opacity) * 255).quantize(Decimal(1), rounding=ROUND_HALF_UP))
    return f"#{alpha:02X}{hex_rgb.upper()}"


def swift_color(source):
    match = re.fullmatch(r"(?:Self\.)?(?:hex\(0x([0-9a-f]{6})\)|(\.white|\.black))(?:\.opacity\(([\d.]+)\))?", source)
    if not match:
        raise ValueError(f"Unsupported Swift colour: {source}")
    rgb, neutral, opacity = match.groups()
    return xaml_color(rgb or ("FFFFFF" if neutral == ".white" else "000000"), opacity or "1")


def swift_palettes(source):
    result = []
    for theme in ("dark", "light"):
        args = re.search(rf"static let {theme} = Palette\((.*?)\bdark: (?:true|false)\)", source, re.S)[1]
        palette = {name: swift_color(value.strip())
                   for name, value in re.findall(r"(\w+): ([^,]+),", args)}
        declarations = source.split("struct Palette {", 1)[1].split("static func of", 1)[0]
        stored = set(re.findall(r"(\w+): Color", declarations))
        if set(palette) != stored:
            raise ValueError(f"Palette fields changed: {sorted(set(palette) ^ stored)}")
        for name, dark, light in re.findall(r"var (\w+): Color \{ dark \? (.*?) : (.*?) \}", source):
            palette[name] = swift_color(dark if theme == "dark" else light)
        quake = re.search(rf"let {theme}Codes: \[Int: UInt32\] = \[(.*?)\]", source)[1]
        for index, name in enumerate(("Black", "Red", "Green", "Yellow", "Blue", "Cyan", "Violet")):
            palette[f"Game{name}Brush"] = xaml_color(re.search(rf"{index}: 0x([0-9a-f]{{6}})", quake)[1])
        result.append(palette)
    return result


def system_color(name):
    return f"{{ThemeResource SystemColor{name}Color}}" if name else TRANSPARENT


def high_contrast(token):
    if token in WINDOW_FILLS:
        return "Window"
    if token in EDGES:
        return "WindowText"
    return {
        "button": "ButtonFace", "buttonHover": "Highlight",
        "selection": "Highlight", "selectionStroke": "Highlight",
        "segmentSelected": "Highlight", "link": "Hotlight",
        "prominentText": "HighlightText", "prominentTop": "Highlight",
        "prominentBottom": "Highlight", "control": "Highlight", "knob": "ButtonText",
    }.get(token, "WindowText")


def generate(source):
    dark, light = swift_palettes(source)
    tag = ARTWORK.read_text(encoding="utf-8").split("struct MiniDogTag: View", 1)[1].split("struct StampedDogTag", 1)[0]
    dark_tag, light_tag, dark_hole, light_hole, tag_stamp = re.findall(r"0x([0-9a-f]{6})", tag)
    # The shortcut hint over the hero art has its own ink on the Mac (HeroInk.hint), set for the scrim.
    hero_hint = re.search(r"static let hint = \(dark: Palette\.hex\(0x([0-9a-f]{6})\), light: Palette\.hex\(0x([0-9a-f]{6})\)\)",
                          HOME.read_text(encoding="utf-8")).groups()
    root = ET.Element("ResourceDictionary", {
        "xmlns": "http://schemas.microsoft.com/winfx/2006/xaml/presentation",
        "xmlns:x": "http://schemas.microsoft.com/winfx/2006/xaml",
    })
    root.append(ET.Comment(" Generated by tools/windows/generate-launcher-theme.py. "))
    themes = ET.SubElement(root, "ResourceDictionary.ThemeDictionaries")

    def resource(parent, tag, key, **attributes):
        return ET.SubElement(parent, tag, {"x:Key": key, **attributes})

    for palette, name in ((dark, "Dark"), (light, "Light"), (None, "HighContrast")):
        dictionary = resource(themes, "ResourceDictionary", name)

        def brush(key, color):
            resource(dictionary, "SolidColorBrush", key, Color=color)

        def token(key, value, system=None):
            brush(key, palette[value] if palette is not None else system_color(system or high_contrast(value)))

        def keys(value, system, names):
            for key in names.split():
                token(key, value, system)

        for value in dark:
            token(value, value)
        for value, key in ALIASES.items():
            token(key, value)
        for key, (dark_color, light_color, system) in BOARD_COLORS.items():
            brush(key, dark_color if name == "Dark" else light_color if name == "Light" else system_color(system))
        brush("FocusVisualPrimaryBrush", "#BF" + palette["accent"][-6:] if palette else system_color("WindowText"))
        brush("FocusVisualSecondaryBrush", TRANSPARENT)
        brush("FrameContrastBrush", TRANSPARENT if palette else system_color("Window"))
        brush("HeroBackplateBrush", TRANSPARENT if palette else system_color("Window"))
        brush("HeroHintBrush", xaml_color(hero_hint[name == "Light"]) if palette else system_color("WindowText"))
        # Native presenters can still request these legacy screen resources.
        token("LauncherSelectionPillBrush", "control", "HighlightText")
        token("AccentFillColorDefaultBrush", "prominentBottom", "Highlight")
        token("TextOnAccentFillColorPrimaryBrush", "prominentText", "HighlightText")
        token("SettingsNoteBackground", "well")
        token("TextControlSelectionHighlightColor", "selectionStroke", "Highlight")

        if palette:
            scrim = resource(dictionary, "LinearGradientBrush", "SetupHeroScrimBrush",
                             StartPoint="0,0", EndPoint="1,0")
            for offset, alpha in (("0", "FF"), ("0.5", "E8"), ("1", "22")):
                ET.SubElement(scrim, "GradientStop", Offset=offset, Color="#" + alpha + palette["panel"][-6:])
        else:
            brush("SetupHeroScrimBrush", TRANSPARENT)

        for key in ("GlassAcrylicBrush", "ContentDialogBackground", "FlyoutPresenterBackground"):
            if palette:
                resource(dictionary, "AcrylicBrush", key,
                         TintColor="#FF" + palette["glassFill"][-6:],
                         TintOpacity=str(round(int(palette["glassFill"][1:3], 16) / 255, 2)),
                         FallbackColor=BOARD_COLORS["glassSolid"][name == "Light"])
            else:
                brush(key, system_color("Window"))
        for key, dark_color, light_color, system in (
                ("tagPlate", dark_tag, light_tag, "WindowText"),
                ("tagEyelet", dark_hole, light_hole, "Window"),
                ("tagStamp", tag_stamp, tag_stamp, "Window")):
            brush(key, xaml_color(dark_color if name == "Dark" else light_color) if palette else system_color(system))

        disabled_ink = "#66" + palette["text"][-6:] if palette else system_color("GrayText")
        brush("disabledInk", disabled_ink)

        for key, start, end in (("ProminentBrush", "0.5,0", "0.5,1"),
                                ("ProgressFillBrush", "0,0.5", "1,0.5")):
            if palette:
                gradient = resource(dictionary, "LinearGradientBrush", key, StartPoint=start, EndPoint=end)
                stops = ("prominentTop", "prominentBottom") if key == "ProminentBrush" else ("prominentBottom", "prominentTop")
                for offset, stop in enumerate(stops):
                    ET.SubElement(gradient, "GradientStop", Offset=str(offset), Color=palette[stop])
            else:
                brush(key, system_color("Highlight"))

        # State resources for the native templates and presenters. Keep their
        # named parts, automation peers and input handling intact.
        states = ("", "PointerOver", "Pressed", "Disabled")
        for state, fill, sys_fill in zip(states, ("button", "buttonHover", "press", "button"),
                                         ("ButtonFace", "Highlight", "Highlight", "ButtonFace")):
            active = state in ("PointerOver", "Pressed")
            disabled = state == "Disabled"
            ink = "GrayText" if disabled else "HighlightText" if active else "ButtonText"
            for control in ("Button", "ComboBox"):
                token(f"{control}Background{state}", fill, sys_fill)
                token(f"{control}BorderBrush{state}", "buttonStroke", "GrayText" if disabled else ink)
                if disabled:
                    brush(f"{control}Foreground{state}", disabled_ink)
                else:
                    token(f"{control}Foreground{state}", "text", ink)
            for control in ("SubtleButton", "HyperlinkButton"):
                brush(f"{control}Background{state}", palette[fill] if palette and active else
                      system_color(sys_fill) if active else TRANSPARENT)
                token(f"{control}Foreground{state}", "link", "Hotlight" if not active and not disabled else ink)
                brush(f"{control}BorderBrush{state}", TRANSPARENT)
            if not disabled:
                keys("link", "Hotlight", f"HyperlinkForeground{state}")
            if disabled:
                brush("AccentButtonForegroundDisabled", disabled_ink)
            else:
                token(f"AccentButtonForeground{state}", "prominentText", "HighlightText")
            brush(f"AccentButtonBorderBrush{state}",
                  (palette["buttonStroke"] if disabled else BOARD_COLORS["prominentEdge"][name == "Light"]) if palette
                  else system_color("GrayText" if disabled else "ButtonText"))
            if disabled:
                token(f"AccentButtonBackground{state}", "button", "ButtonFace")
            elif palette:
                # Native key and generated gradient share the same stop source.
                gradient = resource(dictionary, "LinearGradientBrush", f"AccentButtonBackground{state}",
                                    StartPoint="0.5,0", EndPoint="0.5,1")
                stops = ("prominentTop", "prominentBottom") if state != "Pressed" else ("prominentBottom", "prominentBottom")
                for offset, stop in enumerate(stops):
                    ET.SubElement(gradient, "GradientStop", Offset=str(offset), Color=palette[stop])
            else:
                brush(f"AccentButtonBackground{state}", system_color("Highlight"))

            field_state = "Focused" if state == "Pressed" else state
            token(f"TextControlBackground{field_state}", "field", "Window")
            token(f"TextControlBorderBrush{field_state}", "selectionStroke" if field_state == "Focused" else "wellStroke",
                  "Highlight" if field_state == "Focused" else "GrayText" if disabled else "WindowText")
            token(f"TextControlForeground{field_state}", "text", "GrayText" if disabled else "WindowText")
            token(f"TextControlPlaceholderForeground{field_state}", "tertiary", "GrayText" if disabled else "WindowText")
            for check in ("Checked", "Indeterminate"):
                keys("control", "GrayText" if disabled else "Highlight",
                     f"CheckBoxCheckBackgroundFill{check}{state} CheckBoxCheckBackgroundStroke{check}{state}")
                keys("knob", "HighlightText", f"CheckBoxCheckGlyphForeground{check}{state}")
            keys("field", "Window", f"CheckBoxCheckBackgroundFillUnchecked{state}")
            keys("wellStroke", "WindowText", f"CheckBoxCheckBackgroundStrokeUnchecked{state}")
            keys("control", "GrayText" if disabled else "Highlight",
                 f"ToggleSwitchFillOn{state} ToggleSwitchStrokeOn{state} "
                 f"RadioButtonOuterEllipseCheckedStroke{state} RadioButtonOuterEllipseCheckedFill{state}")
            keys("knob", "HighlightText", f"RadioButtonCheckGlyphFill{state}")
            keys("buttonHover" if state == "PointerOver" else "press" if state == "Pressed" else "track",
                 "Window", f"ToggleSwitchFillOff{state}")
            keys("track", "Window", f"SliderTrackFill{state}")
            keys("track", "WindowText", f"ToggleSwitchStrokeOff{state}")
            keys("knob", "ButtonText", f"ToggleSwitchKnobFillOn{state} ToggleSwitchKnobFillOff{state} SliderThumbBackground{state}")
            keys("control", "GrayText" if disabled else "Highlight", f"SliderTrackValueFill{state}")

        for control in ("ListViewItem", "GridViewItem", "NavigationViewItem", "SelectorBarItem", "ComboBoxItem", "MenuFlyoutItem", "MenuFlyoutSubItem"):
            token(f"{control}BackgroundPointerOver", "hover", "Highlight")
            token(f"{control}BackgroundPressed", "press", "Highlight")
            token(f"{control}ForegroundPointerOver", "text", "HighlightText")
            token(f"{control}ForegroundPressed", "text", "HighlightText")
        for control in ("ListViewItem", "GridViewItem", "NavigationViewItem", "SelectorBarItem", "ComboBoxItem"):
            selected = "buttonHover" if control in ("SelectorBarItem", "NavigationViewItem") else "selection"
            for state in ("Selected", "SelectedPointerOver", "SelectedPressed"):
                token(f"{control}Background{state}", selected, "Highlight")
                token(f"{control}Foreground{state}", "heading", "HighlightText")
        for key in ("SelectorBarItemPillFill", "ComboBoxItemPillFillBrush", "NavigationViewSelectionIndicatorForeground",
                    "ListViewItemSelectionIndicatorBrush", "ListViewItemSelectionIndicatorPointerOverBrush",
                    "ListViewItemSelectionIndicatorPressedBrush"):
            brush(key, TRANSPARENT if palette else system_color("HighlightText"))
        keys("track", "WindowText", "RadioButtonOuterEllipseStroke RadioButtonOuterEllipseStrokePointerOver")
        keys("control", "Highlight", "ProgressRingForegroundThemeBrush")
        for key, height in (("TextControlThemeMinHeight", "FieldInlineHeight"), ("ComboBoxMinHeight", "ButtonRegularHeight")):
            resource(dictionary, "x:Double", key).text = str(BOARD_METRICS[height])
        for severity, ink in (("Informational", "accent"), ("Warning", "caution"),
                              ("Success", "positive"), ("Error", "negative")):
            brush(f"InfoBar{severity}SeverityBackgroundBrush",
                  "#1F" + palette[ink][-6:] if palette else system_color("Window"))
            token(f"InfoBar{severity}SeverityIconBackground", ink, "WindowText")
            token(f"InfoBar{severity}SeverityIconForeground", "panel", "Window")
        for key in ("ListViewItemCornerRadius", "GridViewItemCornerRadius"):
            resource(dictionary, "CornerRadius", key).text = str(RADII["Group"])
        keys("text", "WindowText", "InfoBarTitleForeground InfoBarMessageForeground MenuFlyoutItemForeground ContentDialogForeground")
        keys("panel", "Window", "ComboBoxDropDownBackground MenuFlyoutPresenterBackground")
        keys("panelStroke", "WindowText", "ComboBoxDropDownBorderBrush")
        keys("glassStroke", "WindowText", "ContentDialogBorderBrush")
        keys("text", "WindowText", "ComboBoxForegroundFocused ComboBoxForegroundFocusedPressed")
        keys("button", "ButtonFace", "ComboBoxBackgroundFocused ComboBoxBackgroundUnfocused")
        keys("secondary", "WindowText", "ComboBoxDropDownGlyphForeground ComboBoxDropDownGlyphForegroundFocused")
        keys("separator", "WindowText", "MenuFlyoutSeparatorBackground")
        keys("panel", "Window", "ExpanderContentBackground ExpanderDropDownBackground")
        keys("well", "ButtonFace", "ExpanderBackground ExpanderHeaderBackground")
        keys("wellStroke", "WindowText", "ExpanderBorderBrush ExpanderHeaderBorderBrush ExpanderContentBorderBrush ExpanderDropDownBorderBrush")
        keys("buttonHover", "Highlight", "ExpanderChevronPointerOverBackground")
        keys("press", "Highlight", "ExpanderChevronPressedBackground")
        keys("text", "ButtonText", "ExpanderForeground ExpanderHeaderForeground ExpanderChevronForeground")
        keys("heading", "HighlightText", "ExpanderChevronPointerOverForeground ExpanderChevronPressedForeground")
        brush("ContentDialogSmokeFill", BOARD_COLORS["scrim"][name == "Light"] if palette else TRANSPARENT)
        brush("NavigationViewExpandedPaneBackground", TRANSPARENT)

    for name, value in RADII.items():
        resource(root, "CornerRadius", name + "Radius").text = str(value)
    for name, (size, weight, line) in TYPE.items():
        resource(root, "x:Double", name + "Size").text = str(size)
        resource(root, "x:Double", name + "LineHeight").text = str(line)
        style = resource(root, "Style", "Type" + name, TargetType="TextBlock")
        for property_, value in (("FontSize", size), ("FontWeight", weight), ("LineHeight", line)):
            ET.SubElement(style, "Setter", Property=property_, Value=str(value))
    action = re.search(r"static let action: CGFloat = (\d+)", source)[1]
    row = re.search(r"detailRowHeight: CGFloat = (\d+)", COMPONENTS.read_text())[1]
    measures = dict(BOARD_METRICS, ButtonMediumHeight=action, DetailRowHeight=row)
    for name, value in measures.items():
        resource(root, "x:Double", name).text = str(value)
    for name in ("ButtonSmall", "ButtonRegular", "ButtonMedium", "ButtonHero", "FieldCapsule", "Circle",
                 "Segment", "Chip", "LargeChip", "Switch"):
        resource(root, "CornerRadius", name + "Radius").text = str(int(measures[name + "Height"]) // 2)
    resource(root, "CornerRadius", "SegmentItemRadius").text = "11"
    resource(root, "GridLength", "CaptionRowHeight").text = str(BOARD_METRICS["CaptionHeight"])
    resource(root, "Thickness", "Stroke").text = "1"
    resource(root, "Thickness", "FocusStroke").text = str(BOARD_METRICS["FocusThickness"])
    resource(root, "Thickness", "FocusMargin").text = str(-BOARD_METRICS["FocusOffset"] - BOARD_METRICS["FocusThickness"])
    resource(root, "Thickness", "SidebarMargin").text = "10"
    resource(root, "Thickness", "GroupPadding").text = "20"
    resource(root, "Thickness", "RowPadding").text = "20,9"
    ET.indent(root, space="    ")
    return ET.tostring(root, encoding="unicode") + "\n"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="fail when the committed resources are stale")
    args = parser.parse_args()
    xaml = generate(PALETTE.read_text(encoding="utf-8"))
    if args.check:
        if OUTPUT.read_text(encoding="utf-8") != xaml:
            parser.exit(1, "Launcher tokens drifted. Run python3 tools/windows/generate-launcher-theme.py.\n")
        print("Windows launcher tokens match the Mac palette and WinParts metrics.")
    else:
        OUTPUT.write_text(xaml, encoding="utf-8")


if __name__ == "__main__":
    main()

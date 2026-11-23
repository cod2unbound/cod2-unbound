using System.Collections.Immutable;

namespace CoD2Unbound.Core;

/// <summary>Windows game bindings use the unshifted VK mapping and a set-1 scan code.</summary>
public static class WindowsBindInput
{
    private static readonly byte[] Navigation = [184, 190, 188, 182, 185, 183, 187, 189];
    private static readonly byte[] ExtendedNavigation = [164, 163, 166, 165, 156, 154, 157, 155];
    private static readonly byte[] PadDigits = [192, 188, 189, 190, 185, 186, 187, 182, 183, 184];
    private static readonly byte[] International = [181, 191, 223, 224, 225, 228, 229, 230, 231, 232,
        233, 236, 241, 242, 243, 246, 248, 249, 250, 252];

    public static EngineKey? Key(int virtualKey, int scan, bool extended, char unshiftedVirtualKey)
    {
        if (virtualKey is 0 or 0x2c or 0x5b or 0x5c or 0x5d)
            return null;

        int number = virtualKey switch
        {
            _ when scan == 0x29 && !extended => 126,
            >= 0x41 and <= 0x5a => virtualKey + (extended ? 0 : 32),
            >= 0x30 and <= 0x39 => virtualKey,
            >= 0x70 and <= 0x7b => 167 + virtualKey - 0x70,
            >= 0x60 and <= 0x69 => PadDigits[virtualKey - 0x60],
            >= 0x21 and <= 0x28 => (extended ? ExtendedNavigation : Navigation)[virtualKey - 0x21],
            0x08 => 127, 0x09 => 9, 0x0c when !extended => 186,
            0x0d => extended ? 191 : 13,
            0x10 or 0xa0 or 0xa1 => 160,
            0x11 or 0xa2 or 0xa3 => 159,
            0x12 or 0xa4 or 0xa5 => 158,
            0x13 => 153, 0x14 => 151, 0x1b => 27, 0x20 => 32,
            0x2d => extended ? 161 : 192, 0x2e => extended ? 162 : 193,
            0x6a => 198, 0x6b => 196, 0x6d => 195, 0x6e => 193, 0x6f => 194, 0x90 => 197,
            _ => Character(unshiftedVirtualKey)
        };
        return number == 0 ? null : new EngineKey(number);
    }

    private static int Character(char character)
    {
        if (character is < ' ' or > (char)255)
            return 0;
        int index = Array.IndexOf(International, (byte)character);
        return index < 0 ? character : 128 + index;
    }

    public static EngineKey? Button(int number) => number is >= 1 and <= 5 ? new(199 + number) : null;
    public static EngineKey? Wheel(int delta) => EngineInputMapping.FromWheel(delta) is { } key ? new(key) : null;

    public static string Label(EngineKey key) => key.Spelling.ToUpperInvariant() switch
    {
        "SPACE" => "Space", "ENTER" => "Enter", "TAB" => "Tab", "CTRL" => "Ctrl", "SHIFT" => "Shift",
        "ALT" => "Alt", "BACKSPACE" => "Backspace", "ESCAPE" => "Esc",
        "UPARROW" => "↑", "DOWNARROW" => "↓", "LEFTARROW" => "←", "RIGHTARROW" => "→",
        "MWHEELUP" => "Wheel up", "MWHEELDOWN" => "Wheel down",
        "MOUSE1" or "MOUSE2" or "MOUSE3" or "MOUSE4" or "MOUSE5" => $"Mouse {key.Spelling[^1]}",
        _ => key.Spelling.ToUpperInvariant()
    };
}

public sealed class PcKeyboardVerification
{
    private static readonly string[] Prompts =
    [
        "Press the key just to the right of the left Shift key.",
        "Press the key just to the left of the space bar.",
        "Press a number on the numeric keypad, the block of number keys at the right.",
        "Press F1, at the top left.",
        "Press Home, End, Page Up or Page Down.",
        "Press any arrow key."
    ];

    public int Step { get; private set; }
    public KeyboardShape Shape { get; private set; } = KeyboardShape.Ansi;
    public string? Note { get; private set; }
    public ImmutableArray<KeyboardSize> Sizes { get; private set; } = [];
    public bool CanSkip => Step >= 2 && Sizes.IsEmpty;
    public string Prompt => Sizes.IsEmpty ? Prompts[Step] : Sizes.Length == 1
        ? $"Your keys fit {SizeName(Sizes[0])}. Confirm the size of your keyboard."
        : $"Your keys fit {string.Join(" or ", Sizes.Select(SizeName))}. Choose the size of your keyboard.";

    public static string SizeName(KeyboardSize size) => size switch
    {
        KeyboardSize.Tkl => "Tenkeyless", KeyboardSize.Percent75 => "75%",
        KeyboardSize.Percent65 => "65%", KeyboardSize.Percent60 => "60%", _ => size.ToString()
    };

    public void Press(int scan, bool extended)
    {
        if (!Sizes.IsEmpty)
            return;
        Note = null;
        switch (Step)
        {
            case 0 when !extended && scan is 0x2c or 0x56:
                Shape = scan == 0x56 ? KeyboardShape.Iso : KeyboardShape.Ansi;
                Step++;
                break;
            case 1 when !extended && scan is 0x38 or 0x7b:
                if (scan == 0x7b)
                    Shape = KeyboardShape.Jis;
                Step++;
                break;
            case 2 when !extended && scan is >= 0x47 and <= 0x52 and not (0x4a or 0x4e):
                Sizes = [KeyboardSize.Full];
                break;
            case 3 when !extended && scan == 0x3b:
                Step++;
                break;
            case 4 when extended && scan is 0x47 or 0x49 or 0x4f or 0x51:
                Sizes = [KeyboardSize.Tkl, KeyboardSize.Percent75];
                break;
            case 5 when extended && scan is 0x48 or 0x4b or 0x4d or 0x50:
                Sizes = [KeyboardSize.Percent65];
                break;
            default:
                Note = Step switch
                {
                    0 => "That’s a different key. Press the key right next to the left Shift key.",
                    1 => "That’s a different key. Press the one right next to the left end of the space bar.",
                    2 when !extended && scan is >= 0x02 and <= 0x0b => "That’s a number above the letters. Press one on the keypad, or choose I don’t have this key.",
                    2 => "That key isn’t on a numeric keypad. Press a keypad number, or choose I don’t have this key.",
                    3 => "That isn’t F1. Press F1, or choose I don’t have this key.",
                    4 => "That isn’t Home, End, Page Up or Page Down. Press one of them, or choose I don’t have this key.",
                    _ => "That isn’t an arrow key. Press one, or choose I don’t have this key."
                };
                break;
        }
    }

    public void Skip()
    {
        if (!CanSkip)
            return;
        Note = null;
        switch (Step)
        {
            case 2: Step = 3; break;
            case 3: Step = 5; break;
            case 4: Sizes = [KeyboardSize.Percent75]; break;
            case 5: Sizes = [KeyboardSize.Percent60]; break;
        }
    }
}

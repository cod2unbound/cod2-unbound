using System.Collections.Immutable;

namespace CoD2Unbound.Core;

public enum KeyboardShape { Ansi, Iso, Jis }
public enum KeyboardSize { Full, Tkl, Percent75, Percent65, Percent60, Unknown }
public enum KeyboardModifierStyle { Mac, Pc }
public enum InputDeviceKind { Keyboard, Mouse, Trackpad }
public enum InputKeyBasis { Detected, Guessed, UserOverride, Verified, Unknown }

public sealed record InputDevice(string Id, string Name, int? VendorId, InputDeviceKind Kind,
    KeyboardShape? Shape = null, int? ButtonCount = null)
{
    public static InputDeviceKind? DetectKind(int? usagePage, int? usage, string name) => (usagePage, usage) switch
    {
        (1, 6) => InputDeviceKind.Keyboard,
        (13, 5) => InputDeviceKind.Trackpad,
        (1, 1 or 2) => name.Contains("trackpad", StringComparison.OrdinalIgnoreCase) ? InputDeviceKind.Trackpad : InputDeviceKind.Mouse,
        _ => null
    };
}

public sealed record InputDeviceOverride(KeyboardShape? Shape = null, KeyboardSize? Size = null,
    KeyboardModifierStyle? Modifiers = null, bool Verified = false);
public sealed record InputDeviceChoice<T>(T Value, InputKeyBasis Basis, string Explanation);
public sealed record InputDevicePresentation(InputDevice Device, InputDeviceChoice<KeyboardShape> Shape,
    InputDeviceChoice<KeyboardSize> Size, InputDeviceChoice<KeyboardModifierStyle> Modifiers)
{
    public static InputDevicePresentation From(InputDevice device, KeyboardShape currentShape, InputDeviceOverride? change = null)
    {
        change ??= new();
        string name = device.Name.ToLowerInvariant();
        KeyboardSize guess = name switch
        {
            var value when value.Contains("60%", StringComparison.Ordinal) => KeyboardSize.Percent60,
            var value when value.Contains("65%", StringComparison.Ordinal) => KeyboardSize.Percent65,
            var value when value.Contains("75%", StringComparison.Ordinal) ||
                (value.Contains("magic keyboard", StringComparison.Ordinal) && !value.Contains("numeric", StringComparison.Ordinal)) => KeyboardSize.Percent75,
            var value when value.Contains("tkl", StringComparison.Ordinal) || value.Contains("tenkeyless", StringComparison.Ordinal) => KeyboardSize.Tkl,
            var value when value.Contains("numeric", StringComparison.Ordinal) || value.Contains("full size", StringComparison.Ordinal) => KeyboardSize.Full,
            _ => KeyboardSize.Unknown
        };
        return new(device, Choose(change.Shape, device.Shape ?? currentShape, device.Shape is null ? InputKeyBasis.Guessed : InputKeyBasis.Detected,
                "Your keyboard shape", device.Shape is null ? "Current macOS keyboard shape; this device does not report its own" : "macOS keyboard identification"),
            Choose(change.Size, guess, guess == KeyboardSize.Unknown ? InputKeyBasis.Unknown : InputKeyBasis.Guessed,
                "Your keyboard size", guess == KeyboardSize.Unknown ? "Choose your keyboard size, or press keys to verify it"
                    : "Estimated from the device name. Press keys to verify it, or choose a size"),
            Choose(change.Modifiers, device.VendorId == 0x05ac ? KeyboardModifierStyle.Mac : KeyboardModifierStyle.Pc,
                InputKeyBasis.Guessed, "Your modifier labels", "Guess from the keyboard vendor"));

        InputDeviceChoice<T> Choose<T>(T? value, T fallback, InputKeyBasis basis, string own, string explanation) where T : struct =>
            value is { } picked ? new(picked, change.Verified ? InputKeyBasis.Verified : InputKeyBasis.UserOverride,
                change.Verified ? "You confirmed this by pressing keys" : own) : new(fallback, basis, explanation);
    }
}

public sealed record InputKeyMapping(EngineKey? Key, bool IsReserved, string? Explanation)
{
    public static InputKeyMapping FromSdl(int? symbol, string printedLabel, bool isHardwareModifier = false)
    {
        if (isHardwareModifier)
            return new(null, false, "Fn changes other keys in the keyboard hardware and cannot be bound by itself.");
        if (symbol is null)
            return new(null, false, "This physical key is not known to the game's keyboard backend.");
        if (EngineInputMapping.FromSdlKey(symbol.Value) is not { } number)
            return new(null, false, symbol < 1 << 30
                ? $"The original game cannot use {printedLabel} as a binding key."
                : "The game's keyboard backend has no binding for this key.");
        var key = new EngineKey(number);
        if (key.Number != number)
            return new(null, false, "This layout sends an uppercase letter, but the game saves bindings as lowercase letters. Choose another input source to bind this key.");
        return new(key, key.Reserved, key.Reserved ? "This key opens the console and is reserved by the game." : null);
    }
}

public enum PointerButtonPosition { Left, Right, Wheel, Back, Forward }
public sealed record PointerButtonMap(ImmutableDictionary<PointerButtonPosition, int> Buttons, ImmutableArray<int> Extras);

public sealed class PointerButtonLearning
{
    private static readonly string[] Titles = ["Left button", "Right button", "Wheel button", "Back side button", "Forward side button"];
    private static readonly string[] Instructions =
    [
        "Click the left button.", "Click the right button.", "Press down on the scroll wheel.",
        "Click the side button nearer your wrist (usually Back).", "Click the side button further forward (usually Forward)."
    ];
    private readonly Dictionary<PointerButtonPosition, int> learned = new();
    private readonly List<int> extras = [];
    private int index;
    public PointerButtonPosition? Position => index < Titles.Length ? (PointerButtonPosition)index : null;
    public bool IsCollectingExtras => index == Titles.Length;
    public bool IsDone => index > Titles.Length;
    public string? Note { get; private set; }
    public PointerButtonMap? Result => IsDone ? new(learned.ToImmutableDictionary(), [.. extras]) : null;

    public void Click(int button)
    {
        if (IsDone || button < 0 || button == int.MaxValue)
            return;
        var owner = learned.Where(pair => pair.Value == button).Select(pair => (PointerButtonPosition?)pair.Key).FirstOrDefault();
        if (owner is { } other)
        {
            Note = $"That’s your {Titles[(int)other].ToLowerInvariant()}.";
            if (Position is { } position)
                Note += $" {Instructions[(int)position]} If your mouse doesn’t have one, choose I Don’t Have This Button.";
            return;
        }
        string hint = button < 5 ? $"The game reads it as Mouse {button + 1}."
            : "The original game can’t use it. Map it to a key in your mouse’s software.";
        if (Position is { } current)
        {
            learned[current] = button;
            Note = button < 5 ? null : $"Button {button + 1}: {hint}";
            index++;
        }
        else
        {
            if (!extras.Contains(button))
                extras.Add(button);
            Note = $"Button {button + 1}: {hint}";
        }
    }

    public void Skip()
    {
        if (!IsDone)
        {
            index++;
            Note = null;
        }
    }
}

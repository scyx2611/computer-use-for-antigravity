namespace ComputerUse.Native;

/// <summary>
/// Maps the small, public key vocabulary to restricted CDP key events. This
/// deliberately does not expose a raw Input.dispatchKeyEvent parameter bag.
/// </summary>
internal sealed class CdpInputEngine
{
    private static readonly Dictionary<string, KeyDescriptor> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BACKSPACE"] = new("Backspace", "Backspace", 0x08),
        ["TAB"] = new("Tab", "Tab", 0x09),
        ["ENTER"] = new("Enter", "Enter", 0x0D),
        ["RETURN"] = new("Enter", "Enter", 0x0D),
        ["ESC"] = new("Escape", "Escape", 0x1B),
        ["ESCAPE"] = new("Escape", "Escape", 0x1B),
        ["SPACE"] = new(" ", "Space", 0x20, " "),
        ["PAGEUP"] = new("PageUp", "PageUp", 0x21),
        ["PAGEDOWN"] = new("PageDown", "PageDown", 0x22),
        ["END"] = new("End", "End", 0x23),
        ["HOME"] = new("Home", "Home", 0x24),
        ["LEFT"] = new("ArrowLeft", "ArrowLeft", 0x25),
        ["ARROWLEFT"] = new("ArrowLeft", "ArrowLeft", 0x25),
        ["UP"] = new("ArrowUp", "ArrowUp", 0x26),
        ["ARROWUP"] = new("ArrowUp", "ArrowUp", 0x26),
        ["RIGHT"] = new("ArrowRight", "ArrowRight", 0x27),
        ["ARROWRIGHT"] = new("ArrowRight", "ArrowRight", 0x27),
        ["DOWN"] = new("ArrowDown", "ArrowDown", 0x28),
        ["ARROWDOWN"] = new("ArrowDown", "ArrowDown", 0x28),
        ["INSERT"] = new("Insert", "Insert", 0x2D),
        ["DELETE"] = new("Delete", "Delete", 0x2E),
        ["F1"] = new("F1", "F1", 0x70),
        ["F2"] = new("F2", "F2", 0x71),
        ["F3"] = new("F3", "F3", 0x72),
        ["F4"] = new("F4", "F4", 0x73),
        ["F5"] = new("F5", "F5", 0x74),
        ["F6"] = new("F6", "F6", 0x75),
        ["F7"] = new("F7", "F7", 0x76),
        ["F8"] = new("F8", "F8", 0x77),
        ["F9"] = new("F9", "F9", 0x78),
        ["F10"] = new("F10", "F10", 0x79),
        ["F11"] = new("F11", "F11", 0x7A),
        ["F12"] = new("F12", "F12", 0x7B)
    };

    public void Press(CdpConnection connection, string key, IEnumerable<string>? modifiers = null)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ComputerUseException("KEY_REQUIRED", "key is required.");
        }

        var parts = key.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            throw new ComputerUseException("KEY_REQUIRED", "key is required.");
        }

        var modifierNames = parts.Length > 1
            ? parts[..^1].ToList()
            : new List<string>();
        if (modifiers is not null)
        {
            modifierNames.AddRange(modifiers);
        }

        var modifierDescriptor = ReadModifiers(modifierNames);
        var descriptor = ReadKey(parts[^1]);
        var keyDownType = descriptor.Text is not null && modifierDescriptor.Mask == 0
            ? "keyDown"
            : "rawKeyDown";
        connection.DispatchKeyEvent(
            keyDownType,
            descriptor.Key,
            descriptor.Code,
            modifierDescriptor.Mask,
            descriptor.VirtualKey,
            descriptor.Text);
        if (modifierDescriptor.Mask == 0
            && (string.Equals(descriptor.Key, "Enter", StringComparison.Ordinal)
                || string.Equals(descriptor.Key, " ", StringComparison.Ordinal)))
        {
            connection.DispatchKeyEvent(
                "char",
                descriptor.Key,
                descriptor.Code,
                modifierDescriptor.Mask,
                descriptor.VirtualKey,
                descriptor.Key == "Enter" ? "\r" : " ");
        }
        connection.DispatchKeyEvent(
            "keyUp",
            descriptor.Key,
            descriptor.Code,
            modifierDescriptor.Mask,
            descriptor.VirtualKey);
    }

    private static KeyDescriptor ReadKey(string value)
    {
        if (NamedKeys.TryGetValue(value, out var named))
        {
            return named;
        }

        if (value.Length == 1)
        {
            var character = value[0];
            if (char.IsLetter(character))
            {
                var upper = char.ToUpperInvariant(character);
                return new(upper.ToString(), $"Key{upper}", upper, upper.ToString());
            }

            if (char.IsDigit(character))
            {
                return new(character.ToString(), $"Digit{character}", character, character.ToString());
            }

            return new(character.ToString(), $"Unidentified", 0, character.ToString());
        }

        throw new ComputerUseException("UNSUPPORTED_KEY", $"Unsupported key '{value}'.");
    }

    private static ModifierDescriptor ReadModifiers(IEnumerable<string> names)
    {
        var mask = 0;
        var descriptors = new List<KeyDescriptor>();
        foreach (var name in names)
        {
            var normalized = name.Trim().ToUpperInvariant();
            var descriptor = normalized switch
            {
                "SHIFT" => new KeyDescriptor("Shift", "ShiftLeft", 0x10),
                "CTRL" or "CONTROL" => new KeyDescriptor("Control", "ControlLeft", 0x11),
                "ALT" => new KeyDescriptor("Alt", "AltLeft", 0x12),
                "META" or "WIN" or "CMD" or "COMMAND" => new KeyDescriptor("Meta", "MetaLeft", 0x5B),
                _ => throw new ComputerUseException("UNSUPPORTED_KEY", $"Unsupported modifier '{name}'.")
            };

            if (descriptors.Any(existing => existing.Key == descriptor.Key))
            {
                continue;
            }

            descriptors.Add(descriptor);
            mask |= descriptor.Key switch
            {
                "Alt" => 1,
                "Control" => 2,
                "Meta" => 4,
                "Shift" => 8,
                _ => 0
            };
        }

        return new ModifierDescriptor(mask);
    }

    private readonly record struct ModifierDescriptor(int Mask);

    private readonly record struct KeyDescriptor(
        string Key,
        string Code,
        int VirtualKey,
        string? Text = null);
}

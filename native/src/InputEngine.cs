using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace ComputerUse.Native;

internal sealed class InputEngine
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;

    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventRightDown = 0x0008;
    private const uint MouseEventRightUp = 0x0010;
    private const uint MouseEventWheel = 0x0800;

    private const uint KeyboardEventKeyUp = 0x0002;
    private const uint KeyboardEventUnicode = 0x0004;

    private const uint WheelDelta = 120;

    private static readonly Dictionary<string, ushort> VirtualKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BACKSPACE"] = 0x08,
        ["TAB"] = 0x09,
        ["CLEAR"] = 0x0C,
        ["ENTER"] = 0x0D,
        ["RETURN"] = 0x0D,
        ["SHIFT"] = 0x10,
        ["CTRL"] = 0x11,
        ["CONTROL"] = 0x11,
        ["ALT"] = 0x12,
        ["PAUSE"] = 0x13,
        ["CAPSLOCK"] = 0x14,
        ["ESC"] = 0x1B,
        ["ESCAPE"] = 0x1B,
        ["SPACE"] = 0x20,
        ["PAGEUP"] = 0x21,
        ["PAGEDOWN"] = 0x22,
        ["END"] = 0x23,
        ["HOME"] = 0x24,
        ["LEFT"] = 0x25,
        ["UP"] = 0x26,
        ["RIGHT"] = 0x27,
        ["DOWN"] = 0x28,
        ["INSERT"] = 0x2D,
        ["DELETE"] = 0x2E,
        ["WIN"] = 0x5B,
        ["META"] = 0x5B,
        ["LWIN"] = 0x5B,
        ["RWIN"] = 0x5C,
        ["NUM0"] = 0x60,
        ["NUM1"] = 0x61,
        ["NUM2"] = 0x62,
        ["NUM3"] = 0x63,
        ["NUM4"] = 0x64,
        ["NUM5"] = 0x65,
        ["NUM6"] = 0x66,
        ["NUM7"] = 0x67,
        ["NUM8"] = 0x68,
        ["NUM9"] = 0x69,
        ["MULTIPLY"] = 0x6A,
        ["ADD"] = 0x6B,
        ["SUBTRACT"] = 0x6D,
        ["DECIMAL"] = 0x6E,
        ["DIVIDE"] = 0x6F,
        ["F1"] = 0x70,
        ["F2"] = 0x71,
        ["F3"] = 0x72,
        ["F4"] = 0x73,
        ["F5"] = 0x74,
        ["F6"] = 0x75,
        ["F7"] = 0x76,
        ["F8"] = 0x77,
        ["F9"] = 0x78,
        ["F10"] = 0x79,
        ["F11"] = 0x7A,
        ["F12"] = 0x7B,
        ["NUMLOCK"] = 0x90,
        ["SCROLLLOCK"] = 0x91
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint numberOfInputs, [In] Input[] inputs, int sizeOfInput);

    public void Click(AutomationElement element, UiElementSnapshot snapshot, WindowManager windows, IntPtr hwnd)
    {
        EnsureEnabled(element, snapshot);

        if (TryInvoke(element) || TrySelect(element) || TryToggle(element))
        {
            return;
        }

        ClickAt(Center(snapshot.Bounds), windows, hwnd, rightButton: false, doubleClick: false);
    }

    public void DoubleClick(AutomationElement element, UiElementSnapshot snapshot, WindowManager windows, IntPtr hwnd)
    {
        EnsureEnabled(element, snapshot);
        ClickAt(Center(snapshot.Bounds), windows, hwnd, rightButton: false, doubleClick: true);
    }

    public void RightClick(AutomationElement element, UiElementSnapshot snapshot, WindowManager windows, IntPtr hwnd)
    {
        EnsureEnabled(element, snapshot);
        ClickAt(Center(snapshot.Bounds), windows, hwnd, rightButton: true, doubleClick: false);
    }

    public void SetValue(AutomationElement element, UiElementSnapshot snapshot, string value, WindowManager windows, IntPtr hwnd)
    {
        EnsureEnabled(element, snapshot);

        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)
                && pattern is ValuePattern valuePattern
                && !valuePattern.Current.IsReadOnly)
            {
                valuePattern.SetValue(value);
                // ValuePattern changes the value but does not promise keyboard focus.
                // Keep a following Enter/hotkey directed at the same control.
                windows.ActivateWindow(hwnd);
                FocusElement(element, snapshot, windows, hwnd);
                Thread.Sleep(20);
                var currentValue = valuePattern.Current.Value;
                if (string.Equals(currentValue, value, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                Console.Error.WriteLine(
                    $"Computer Use for Antigravity: ValuePattern did not apply the requested value for element {snapshot.Id}; falling back to SendInput.");
            }
        }
        catch (ElementNotEnabledException)
        {
            throw new ComputerUseException("ELEMENT_DISABLED", $"Element {snapshot.Id} is disabled.");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Computer Use for Antigravity: ValuePattern failed for element {snapshot.Id}: {exception.Message}");
        }

        FocusElement(element, snapshot, windows, hwnd);
        PressKey("CTRL+A");
        TypeUnicode(value);
    }

    public void TypeText(AutomationElement? element, UiElementSnapshot? snapshot, string text, WindowManager windows, IntPtr hwnd)
    {
        if (element is not null)
        {
            if (snapshot is not null)
            {
                EnsureEnabled(element, snapshot);
            }

            FocusElement(element, snapshot, windows, hwnd);
        }
        else
        {
            windows.ActivateWindow(hwnd);
        }

        TypeUnicode(text);
    }

    public void PressKey(string key, IEnumerable<string>? modifiers = null)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ComputerUseException("KEY_REQUIRED", "key is required.");
        }

        var parts = key.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var baseKey = parts.Length == 0 ? key.Trim() : parts[^1];
        var modifierNames = new List<string>();
        if (parts.Length > 1)
        {
            modifierNames.AddRange(parts[..^1]);
        }

        if (modifiers is not null)
        {
            modifierNames.AddRange(modifiers);
        }

        if (!TryGetVirtualKey(baseKey, out var virtualKey))
        {
            throw new ComputerUseException("UNSUPPORTED_KEY", $"Unsupported key '{baseKey}'.");
        }

        var modifierKeys = modifierNames
            .Select(name =>
            {
                if (!TryGetModifierKey(name, out var modifierKey))
                {
                    throw new ComputerUseException("UNSUPPORTED_KEY", $"Unsupported modifier '{name}'.");
                }

                return modifierKey;
            })
            .Distinct()
            .ToArray();

        var inputs = new List<Input>(modifierKeys.Length * 2 + 2);
        foreach (var modifierKey in modifierKeys)
        {
            inputs.Add(KeyInput(modifierKey, keyUp: false));
        }

        inputs.Add(KeyInput(virtualKey, keyUp: false));
        inputs.Add(KeyInput(virtualKey, keyUp: true));

        for (var index = modifierKeys.Length - 1; index >= 0; index--)
        {
            inputs.Add(KeyInput(modifierKeys[index], keyUp: true));
        }

        SendInputs(inputs);
    }

    public void Scroll(int amount, int x, int y, WindowManager windows, IntPtr hwnd)
    {
        windows.ActivateWindow(hwnd);
        if (!SetCursorPos(x, y))
        {
            throw new ComputerUseException("INPUT_FAILED", $"Unable to move the cursor (Win32 error {Marshal.GetLastWin32Error()}).");
        }

        var clampedAmount = Math.Clamp(amount, -100, 100);
        var input = new Input
        {
            Type = InputMouse,
            Data = new InputUnion
            {
                Mouse = new MouseInput
                {
                    MouseData = unchecked((uint)(clampedAmount * (int)WheelDelta)),
                    Flags = MouseEventWheel
                }
            }
        };
        SendInputs([input]);
    }

    public void Drag(
        (int X, int Y) start,
        (int X, int Y) end,
        WindowManager windows,
        IntPtr hwnd)
    {
        windows.ActivateWindow(hwnd);
        MoveCursor(start);
        SendMouseButton(MouseEventLeftDown);
        Thread.Sleep(40);
        MoveCursor(end);
        Thread.Sleep(40);
        SendMouseButton(MouseEventLeftUp);
    }

    public void ClickAt(
        (int X, int Y) point,
        WindowManager windows,
        IntPtr hwnd,
        bool rightButton,
        bool doubleClick)
    {
        windows.ActivateWindow(hwnd);
        MoveCursor(point);
        var down = rightButton ? MouseEventRightDown : MouseEventLeftDown;
        var up = rightButton ? MouseEventRightUp : MouseEventLeftUp;
        SendMouseButton(down);
        SendMouseButton(up);

        if (doubleClick)
        {
            Thread.Sleep(40);
            SendMouseButton(down);
            SendMouseButton(up);
        }
    }

    private static void EnsureEnabled(AutomationElement element, UiElementSnapshot snapshot)
    {
        try
        {
            if (!element.Current.IsEnabled || !snapshot.IsEnabled)
            {
                throw new ComputerUseException("ELEMENT_DISABLED", $"Element {snapshot.Id} is disabled.");
            }
        }
        catch (ElementNotAvailableException)
        {
            throw new ComputerUseException("STALE_STATE", "The requested UI element is no longer available; observe again.");
        }
    }

    private void FocusElement(
        AutomationElement element,
        UiElementSnapshot? snapshot,
        WindowManager windows,
        IntPtr hwnd)
    {
        try
        {
            element.SetFocus();
            return;
        }
        catch (Exception exception)
        {
            if (snapshot is not null)
            {
                Console.Error.WriteLine($"Computer Use for Antigravity: SetFocus failed for element {snapshot.Id}: {exception.Message}");
            }
        }

        if (snapshot is not null)
        {
            ClickAt(Center(snapshot.Bounds), windows, hwnd, rightButton: false, doubleClick: false);
        }
        else
        {
            windows.ActivateWindow(hwnd);
        }
    }

    private static bool TryInvoke(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern)
                && pattern is InvokePattern invoke)
            {
                invoke.Invoke();
                return true;
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Computer Use for Antigravity: InvokePattern failed: {exception.Message}");
        }

        return false;
    }

    private static bool TrySelect(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern)
                && pattern is SelectionItemPattern selection)
            {
                selection.Select();
                return true;
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Computer Use for Antigravity: SelectionItemPattern failed: {exception.Message}");
        }

        return false;
    }

    private static bool TryToggle(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(TogglePattern.Pattern, out var pattern)
                && pattern is TogglePattern toggle)
            {
                toggle.Toggle();
                return true;
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Computer Use for Antigravity: TogglePattern failed: {exception.Message}");
        }

        return false;
    }

    private static (int X, int Y) Center(int[] bounds)
    {
        if (bounds.Length < 4)
        {
            throw new ComputerUseException("INVALID_BOUNDS", "The target element has invalid bounds.");
        }

        return (bounds[0] + Math.Max(0, bounds[2] / 2), bounds[1] + Math.Max(0, bounds[3] / 2));
    }

    private static void MoveCursor((int X, int Y) point)
    {
        if (!SetCursorPos(point.X, point.Y))
        {
            throw new ComputerUseException("INPUT_FAILED", $"Unable to move the cursor (Win32 error {Marshal.GetLastWin32Error()}).");
        }
    }

    private static void SendMouseButton(uint flags)
    {
        var input = new Input
        {
            Type = InputMouse,
            Data = new InputUnion
            {
                Mouse = new MouseInput { Flags = flags }
            }
        };
        SendInputs([input]);
    }

    private static void TypeUnicode(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var inputs = new List<Input>(text.Length * 2);
        foreach (var character in text)
        {
            inputs.Add(new Input
            {
                Type = InputKeyboard,
                Data = new InputUnion
                {
                    Keyboard = new KeyboardInput
                    {
                        ScanCode = character,
                        Flags = KeyboardEventUnicode
                    }
                }
            });
            inputs.Add(new Input
            {
                Type = InputKeyboard,
                Data = new InputUnion
                {
                    Keyboard = new KeyboardInput
                    {
                        ScanCode = character,
                        Flags = KeyboardEventUnicode | KeyboardEventKeyUp
                    }
                }
            });
        }

        SendInputs(inputs);
    }

    private static Input KeyInput(ushort virtualKey, bool keyUp)
    {
        return new Input
        {
            Type = InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = virtualKey,
                    Flags = keyUp ? KeyboardEventKeyUp : 0
                }
            }
        };
    }

    private static void SendInputs(IReadOnlyList<Input> inputs)
    {
        if (inputs.Count == 0)
        {
            return;
        }

        var nativeInputs = inputs.ToArray();
        var sent = SendInput((uint)nativeInputs.Length, nativeInputs, Marshal.SizeOf<Input>());
        if (sent != nativeInputs.Length)
        {
            throw new ComputerUseException(
                "INPUT_FAILED",
                $"SendInput sent {sent} of {nativeInputs.Length} events (Win32 error {Marshal.GetLastWin32Error()}).");
        }
    }

    private static bool TryGetVirtualKey(string key, out ushort virtualKey)
    {
        var normalized = key.Trim();
        if (VirtualKeys.TryGetValue(normalized, out virtualKey))
        {
            return true;
        }

        if (normalized.Length == 1)
        {
            var character = normalized[0];
            if ((character >= 'A' && character <= 'Z')
                || (character >= '0' && character <= '9'))
            {
                virtualKey = character;
                return true;
            }
        }

        virtualKey = 0;
        return false;
    }

    private static bool TryGetModifierKey(string name, out ushort virtualKey)
    {
        var normalized = name.Trim();
        if (normalized.Equals("CTRL", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("CONTROL", StringComparison.OrdinalIgnoreCase))
        {
            virtualKey = 0xA2;
            return true;
        }

        if (normalized.Equals("SHIFT", StringComparison.OrdinalIgnoreCase))
        {
            virtualKey = 0xA0;
            return true;
        }

        if (normalized.Equals("ALT", StringComparison.OrdinalIgnoreCase))
        {
            virtualKey = 0xA4;
            return true;
        }

        if (normalized.Equals("WIN", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("META", StringComparison.OrdinalIgnoreCase))
        {
            virtualKey = 0x5B;
            return true;
        }

        virtualKey = 0;
        return false;
    }
}

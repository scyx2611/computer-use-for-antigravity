using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Automation;

namespace ComputerUse.Native;

internal sealed class AutomationElementInfo
{
    public AutomationElementInfo(AutomationElement element, UiElementSnapshot snapshot)
    {
        Element = element;
        Snapshot = snapshot;
    }

    public AutomationElement Element { get; }

    public UiElementSnapshot Snapshot { get; }
}

internal sealed class AutomationEngine
{
    private static readonly HashSet<string> InteractiveRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Button",
        "CheckBox",
        "ComboBox",
        "Edit",
        "Hyperlink",
        "List",
        "ListItem",
        "MenuItem",
        "RadioButton",
        "Slider",
        "Spinner",
        "TabItem",
        "Text",
        "TreeItem",
        "Document"
    };

    public IReadOnlyList<AutomationElementInfo> GetElementInfos(IntPtr hwnd, int maxElements = 1000)
    {
        AutomationElement root;
        try
        {
            root = AutomationElement.FromHandle(hwnd);
        }
        catch (Exception exception)
        {
            throw new ComputerUseException(
                "UIA_UNAVAILABLE",
                $"Unable to access the UI Automation tree: {exception.Message}");
        }

        if (root is null)
        {
            throw new ComputerUseException("UIA_UNAVAILABLE", "The target window has no UI Automation root.");
        }

        AutomationElementCollection descendants;
        try
        {
            descendants = root.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition);
        }
        catch (Exception exception)
        {
            throw new ComputerUseException(
                "UIA_UNAVAILABLE",
                $"Unable to enumerate the UI Automation tree: {exception.Message}");
        }

        var results = new List<AutomationElementInfo>(Math.Min(descendants.Count, maxElements));
        for (var index = 0; index < descendants.Count && results.Count < maxElements; index++)
        {
            try
            {
                var element = descendants[index];
                if (TryReadSnapshot(element, results.Count + 1, out var snapshot))
                {
                    results.Add(new AutomationElementInfo(element, snapshot));
                }
            }
            catch (ElementNotAvailableException)
            {
                // Providers may invalidate an element while the tree is read.
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Computer Use for Antigravity: skipped UIA element: {exception.Message}");
            }
        }

        return results;
    }

    public AutomationElementInfo? FindByRuntimeId(IntPtr hwnd, string runtimeId)
    {
        if (string.IsNullOrWhiteSpace(runtimeId))
        {
            return null;
        }

        return GetElementInfos(hwnd).FirstOrDefault(candidate =>
            string.Equals(candidate.Snapshot.RuntimeId, runtimeId, StringComparison.Ordinal));
    }

    public AutomationElement? GetFocusedElement()
    {
        try
        {
            return AutomationElement.FocusedElement;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public bool IsElementInWindow(AutomationElement element, IntPtr hwnd)
    {
        var current = element;
        try
        {
            for (var depth = 0; depth < 32 && current is not null; depth++)
            {
                if (current.Current.NativeWindowHandle == hwnd.ToInt32())
                {
                    return true;
                }

                current = TreeWalker.RawViewWalker.GetParent(current);
            }
        }
        catch (Exception)
        {
            return false;
        }

        return false;
    }

    public string GetUiSignature(IntPtr hwnd)
    {
        var elements = GetElementInfos(hwnd, maxElements: 250);
        var builder = new StringBuilder();
        foreach (var element in elements)
        {
            builder.Append(element.Snapshot.Role)
                .Append('\u001f')
                .Append(element.Snapshot.Name)
                .Append('\u001f')
                .Append(element.Snapshot.AutomationId)
                .Append('\u001f')
                .Append(string.Join(',', element.Snapshot.Bounds))
                .Append('\u001e');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static bool TryReadSnapshot(
        AutomationElement element,
        int id,
        out UiElementSnapshot snapshot)
    {
        snapshot = new UiElementSnapshot();
        AutomationElement.AutomationElementInformation current;

        try
        {
            current = element.Current;
            var name = current.Name ?? string.Empty;
            var automationId = current.AutomationId ?? string.Empty;
            var role = NormalizeRole(current.ControlType);
            var bounds = ToBounds(current.BoundingRectangle);
            var isOffscreen = current.IsOffscreen;
            var value = ReadValue(element);

            if (bounds[2] <= 0 || bounds[3] <= 0 || isOffscreen)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(name)
                && string.IsNullOrWhiteSpace(automationId)
                && !InteractiveRoles.Contains(role))
            {
                return false;
            }

            snapshot = new UiElementSnapshot
            {
                Id = id,
                Role = role,
                Name = name,
                AutomationId = automationId,
                Bounds = bounds,
                IsEnabled = current.IsEnabled,
                IsOffscreen = isOffscreen,
                RuntimeId = ReadRuntimeId(element),
                Value = value
            };
            return true;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string ReadRuntimeId(AutomationElement element)
    {
        try
        {
            var runtimeId = element.GetRuntimeId();
            return runtimeId is null || runtimeId.Length == 0
                ? string.Empty
                : string.Join('.', runtimeId);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string? ReadValue(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)
                && pattern is ValuePattern valuePattern)
            {
                var value = valuePattern.Current.Value;
                if (string.IsNullOrEmpty(value))
                {
                    return value;
                }

                return value.Length <= 4_096 ? value : value[..4_096];
            }
        }
        catch (ElementNotAvailableException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (ArgumentException)
        {
        }

        return null;
    }

    private static int[] ToBounds(Rect rectangle)
    {
        if (rectangle.IsEmpty
            || double.IsNaN(rectangle.Left)
            || double.IsNaN(rectangle.Top)
            || double.IsNaN(rectangle.Width)
            || double.IsNaN(rectangle.Height)
            || double.IsInfinity(rectangle.Left)
            || double.IsInfinity(rectangle.Top)
            || double.IsInfinity(rectangle.Width)
            || double.IsInfinity(rectangle.Height))
        {
            return [0, 0, 0, 0];
        }

        return
        [
            Convert.ToInt32(Math.Round(rectangle.Left)),
            Convert.ToInt32(Math.Round(rectangle.Top)),
            Math.Max(0, Convert.ToInt32(Math.Round(rectangle.Width))),
            Math.Max(0, Convert.ToInt32(Math.Round(rectangle.Height)))
        ];
    }

    private static string NormalizeRole(ControlType? controlType)
    {
        if (controlType is null)
        {
            return "Unknown";
        }

        var programmaticName = controlType.ProgrammaticName ?? string.Empty;
        var lastDot = programmaticName.LastIndexOf('.');
        return lastDot >= 0 && lastDot + 1 < programmaticName.Length
            ? programmaticName[(lastDot + 1)..]
            : programmaticName;
    }
}

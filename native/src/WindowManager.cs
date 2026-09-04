using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace ComputerUse.Native;

internal sealed class WindowManager
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private enum TokenInformationClass
    {
        TokenElevation = 20
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenElevation
    {
        public uint TokenIsElevated;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        TokenInformationClass tokenInformationClass,
        out TokenElevation tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    public IReadOnlyList<WindowInfo> ListVisibleWindows()
    {
        var windows = new List<WindowInfo>();

        EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!IsWindowVisible(hwnd))
                {
                    return true;
                }

                var title = ReadWindowTitle(hwnd);
                if (string.IsNullOrWhiteSpace(title))
                {
                    return true;
                }

                var pid = GetProcessId(hwnd);
                windows.Add(new WindowInfo
                {
                    Id = FormatWindowId(hwnd),
                    Title = title,
                    Process = ReadProcessName(pid),
                    Pid = pid
                });
            }
            catch (Exception exception)
            {
                // A window can disappear while EnumWindows is traversing it.
                // Keep the listing useful instead of failing the entire call.
                Console.Error.WriteLine($"Computer Use for Antigravity: skipped window {hwnd}: {exception.Message}");
            }

            return true;
        }, IntPtr.Zero);

        return windows;
    }

    public IntPtr ResolveWindowId(string? windowId)
    {
        if (string.IsNullOrWhiteSpace(windowId))
        {
            throw new ComputerUseException("WINDOW_REQUIRED", "window_id is required.");
        }

        if (!TryParseWindowId(windowId, out var hwnd) || !IsWindow(hwnd))
        {
            throw new ComputerUseException(
                "WINDOW_NOT_FOUND",
                $"Window '{windowId}' does not exist.");
        }

        return hwnd;
    }

    public WindowInfo GetWindowInfo(IntPtr hwnd)
    {
        EnsureWindowExists(hwnd);
        return new WindowInfo
        {
            Id = FormatWindowId(hwnd),
            Title = ReadWindowTitle(hwnd),
            Process = ReadProcessName(GetProcessId(hwnd)),
            Pid = GetProcessId(hwnd)
        };
    }

    public WindowSnapshot GetWindowSnapshot(IntPtr hwnd, WindowRectData? rect = null)
    {
        EnsureWindowExists(hwnd);
        var info = GetWindowInfo(hwnd);
        var windowRect = rect ?? GetWindowRectData(hwnd);

        return new WindowSnapshot
        {
            Id = info.Id,
            Title = info.Title,
            X = windowRect.X,
            Y = windowRect.Y,
            Width = windowRect.Width,
            Height = windowRect.Height,
            Pid = info.Pid
        };
    }

    public WindowRectData GetWindowRectData(IntPtr hwnd)
    {
        EnsureWindowExists(hwnd);
        if (!GetWindowRect(hwnd, out var nativeRect))
        {
            throw new ComputerUseException(
                "WINDOW_RECT_UNAVAILABLE",
                $"Unable to read the bounds of window '{FormatWindowId(hwnd)}'.");
        }

        return new WindowRectData
        {
            X = nativeRect.Left,
            Y = nativeRect.Top,
            Width = Math.Max(0, nativeRect.Right - nativeRect.Left),
            Height = Math.Max(0, nativeRect.Bottom - nativeRect.Top)
        };
    }

    public void EnsureWindowExists(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
        {
            throw new ComputerUseException(
                "WINDOW_NOT_FOUND",
                $"Window '{FormatWindowId(hwnd)}' does not exist.");
        }
    }

    public void ActivateWindow(IntPtr hwnd)
    {
        EnsureWindowExists(hwnd);
        if (!SetForegroundWindow(hwnd))
        {
            var error = Marshal.GetLastWin32Error();
            Console.Error.WriteLine($"Computer Use for Antigravity: SetForegroundWindow failed for {FormatWindowId(hwnd)} (Win32 error {error}).");
        }
    }

    public void EnsureInputAllowed(IntPtr hwnd)
    {
        EnsureWindowExists(hwnd);

        if (IsCurrentProcessElevated())
        {
            return;
        }

        var pid = GetProcessId(hwnd);
        if (pid != 0 && TryIsProcessElevated(pid, out var elevated) && elevated)
        {
            throw new ComputerUseException(
                "TARGET_ELEVATED",
                "The target application is elevated. Computer Use for Antigravity will not elevate itself; run the runtime at a matching integrity level only after user approval.",
                new System.Text.Json.Nodes.JsonObject
                {
                    ["pid"] = pid,
                    ["window_id"] = FormatWindowId(hwnd)
                });
        }
    }

    public static string FormatWindowId(IntPtr hwnd)
    {
        return hwnd.ToInt64().ToString(CultureInfo.InvariantCulture);
    }

    private static bool TryParseWindowId(string value, out IntPtr hwnd)
    {
        hwnd = IntPtr.Zero;
        var trimmed = value.Trim();
        long parsed;

        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!long.TryParse(trimmed[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed))
            {
                return false;
            }
        }
        else if (!long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
        {
            return false;
        }

        if (parsed <= 0)
        {
            return false;
        }

        hwnd = new IntPtr(parsed);
        return true;
    }

    private static string ReadWindowTitle(IntPtr hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(length + 1, 32768));
        _ = GetWindowText(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }

    private static uint GetProcessId(IntPtr hwnd)
    {
        _ = GetWindowThreadProcessId(hwnd, out var pid);
        return pid;
    }

    private static string ReadProcessName(uint pid)
    {
        if (pid == 0)
        {
            return string.Empty;
        }

        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static bool IsCurrentProcessElevated()
    {
        return TryIsProcessElevated((uint)Environment.ProcessId, out var elevated) && elevated;
    }

    private static bool TryIsProcessElevated(uint pid, out bool elevated)
    {
        elevated = false;
        var processHandle = OpenProcess(ProcessQueryLimitedInformation, inheritHandle: false, pid);
        if (processHandle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (!OpenProcessToken(processHandle, TokenQuery, out var tokenHandle))
            {
                return false;
            }

            try
            {
                var size = Marshal.SizeOf<TokenElevation>();
                if (!GetTokenInformation(
                        tokenHandle,
                        TokenInformationClass.TokenElevation,
                        out var tokenElevation,
                        size,
                        out _))
                {
                    return false;
                }

                elevated = tokenElevation.TokenIsElevated != 0;
                return true;
            }
            finally
            {
                _ = CloseHandle(tokenHandle);
            }
        }
        finally
        {
            _ = CloseHandle(processHandle);
        }
    }
}

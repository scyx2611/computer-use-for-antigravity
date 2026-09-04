using System.Runtime.InteropServices;

namespace ComputerUse.Native;

internal static class DpiAwareness
{
    private static readonly IntPtr DpiAwarenessContextPerMonitorAwareV2 = new(-4);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDPIAware();

    public static void EnablePerMonitorV2()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("ComputerUse.Native only runs on Windows.");
        }

        if (SetProcessDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2))
        {
            return;
        }

        // Keep a safe fallback for older Windows builds. It is less precise,
        // but still better than leaving the process DPI-unaware.
        if (!SetProcessDPIAware())
        {
            Console.Error.WriteLine($"Computer Use for Antigravity: unable to set DPI awareness (Win32 error {Marshal.GetLastWin32Error()}).");
        }
    }
}

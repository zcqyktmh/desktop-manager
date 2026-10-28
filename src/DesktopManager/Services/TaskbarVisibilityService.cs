using System.Runtime.InteropServices;

namespace DesktopManager.Services;

/// <summary>Restores the Windows taskbar after older Dock versions hid it.</summary>
public sealed class TaskbarVisibilityService
{
    private const uint AbmSetState = 0x0000000A;
    private const int SwShow = 5;

    public void EnsureVisible()
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero) return;

        var data = new AppBarData
        {
            Size = (uint)Marshal.SizeOf<AppBarData>(),
            Window = taskbar,
            Parameter = IntPtr.Zero
        };
        SHAppBarMessage(AbmSetState, ref data);
        ShowWindow(taskbar, SwShow);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public uint Size;
        public IntPtr Window;
        public uint CallbackMessage;
        public uint Edge;
        public NativeRect Rectangle;
        public IntPtr Parameter;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("shell32.dll", EntryPoint = "SHAppBarMessage")]
    private static extern UIntPtr SHAppBarMessage(uint message, ref AppBarData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);
}

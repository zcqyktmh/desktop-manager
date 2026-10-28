using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DesktopManager.Services;

/// <summary>Places a normal WPF window in the desktop z-order, owned by Explorer's Progman.</summary>
public sealed class ShellDesktopHost : IDisposable
{
    private Window? _window;
    private IntPtr _originalOwner;

    public bool IsAttached => _window is not null;

    public bool Attach(Window window)
    {
        if (_window is not null) return true;

        var progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero)
        {
            WriteDiagnostic("Explorer Progman window was not found");
            return false;
        }

        try
        {
            var helper = new WindowInteropHelper(window);
            var hwnd = helper.EnsureHandle();
            _originalOwner = GetWindow(hwnd, GwOwner);
            helper.Owner = progman;

            if (!GetWindowRect(progman, out var bounds))
            {
                helper.Owner = _originalOwner;
                WriteDiagnostic("Could not read Explorer desktop bounds");
                return false;
            }

            var workArea = SystemParameters.WorkArea;
            window.Left = workArea.Left;
            window.Top = workArea.Top;
            window.Width = workArea.Width;
            window.Height = workArea.Height;
            window.ShowActivated = false;
            _window = window;
            WriteDiagnostic($"attached hwnd=0x{hwnd.ToInt64():X} owner=0x{progman.ToInt64():X} bounds={bounds.Left},{bounds.Top},{bounds.Right},{bounds.Bottom} visible={IsWindowVisible(hwnd)}");
            return true;
        }
        catch (Exception ex)
        {
            WriteDiagnostic($"Could not attach WPF desktop window: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public void ShowSurface()
    {
        if (_window is null) return;
        if (!_window.IsVisible) _window.Show();
    }

    public void HideSurface()
    {
        if (_window?.IsVisible == true) _window.Hide();
    }

    public void RefreshWorkArea()
    {
        if (_window is null) return;
        var workArea = SystemParameters.WorkArea;
        _window.Left = workArea.Left;
        _window.Top = workArea.Top;
        _window.Width = workArea.Width;
        _window.Height = workArea.Height;
    }

    public void RestoreDesktopIcons()
    {
        if (_window is null) return;
        if (_window.IsVisible) _window.Hide();
        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd != IntPtr.Zero && IsWindow(hwnd))
            new WindowInteropHelper(_window).Owner = _originalOwner;
        _window = null;
    }

    public void Dispose() => RestoreDesktopIcons();

    private static void WriteDiagnostic(string message)
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopManager", "desktop-host.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"{DateTime.Now:O} {message}{Environment.NewLine}");
        }
        catch { }
    }

    private const uint GwOwner = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr FindWindow(string? className, string? windowName);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool IsWindowVisible(IntPtr window);
}

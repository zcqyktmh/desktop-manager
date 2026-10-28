using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace DesktopManager.Services;

public static class ThemeService
{
    public static void Apply(string theme)
    {
        var dark = theme.Equals("Dark", StringComparison.OrdinalIgnoreCase) ||
                   (theme.Equals("System", StringComparison.OrdinalIgnoreCase) && IsWindowsDarkMode());
        Set("AppBackground", dark ? "#202020" : "#F4F4F4");
        Set("SidebarBackground", dark ? "#252525" : "#F7F7F7");
        Set("SurfaceBackground", dark ? "#2B2B2B" : "#FFFFFF");
        Set("PrimaryText", dark ? "#F3F3F3" : "#202020");
        Set("SecondaryText", dark ? "#B8B8B8" : "#666666");
        Set("DividerBrush", dark ? "#414141" : "#E4E4E4");
        Set("AccentBrush", dark ? "#60A9F5" : "#0866C6");
        Set("HoverBrush", dark ? "#353535" : "#F3F6F9");
    }

    private static bool IsWindowsDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch { return false; }
    }

    private static void Set(string key, string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        Application.Current.Resources[key] = brush;
    }
}

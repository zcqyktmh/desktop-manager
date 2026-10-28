using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DesktopManager.Services;

public sealed class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DesktopManager";
    private const string ShortcutName = "桌面管家.lnk";

    private static string StartupShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup), ShortcutName);

    public bool IsEnabled()
    {
        if (File.Exists(StartupShortcutPath)) return true;
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (key is null) throw new InvalidOperationException("无法打开当前用户的 Windows 启动项。");
        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            if (File.Exists(StartupShortcutPath)) File.Delete(StartupShortcutPath);
            return;
        }

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable)) throw new InvalidOperationException("无法确定应用程序路径。");

        if (TryCreateStartupShortcut(executable))
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        key.SetValue(ValueName, $"\"{executable}\" --background");
    }

    private static bool TryCreateStartupShortcut(string executable)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StartupShortcutPath)!);
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return false;
            shell = Activator.CreateInstance(shellType);
            if (shell is null) return false;
            shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [StartupShortcutPath]);
            if (shortcut is null) return false;

            var shortcutType = shortcut.GetType();
            shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, [executable]);
            shortcutType.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, ["--background"]);
            shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, [Path.GetDirectoryName(executable)!]);
            shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, ["启动桌面管家桌面层"]);
            shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            return File.Exists(StartupShortcutPath);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }
}

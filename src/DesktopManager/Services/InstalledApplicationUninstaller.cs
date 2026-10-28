using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using DesktopManager.Models;
using Microsoft.Win32;

namespace DesktopManager.Services;

public sealed record InstalledApplication(string DisplayName, string UninstallCommand);

public static class InstalledApplicationUninstaller
{
    public static bool IsApplicationShortcut(DesktopItem item)
    {
        var target = DesktopItemIconService.ResolveShortcutTarget(item.Path);
        return !string.IsNullOrWhiteSpace(target) &&
               Path.GetExtension(target).Equals(".exe", StringComparison.OrdinalIgnoreCase);
    }

    public static void Uninstall(DesktopItem item, Window owner)
    {
        var target = DesktopItemIconService.ResolveShortcutTarget(item.Path);
        var application = FindApplication(item, target);
        if (application is null)
        {
            if (MessageBox.Show(owner,
                    $"未能从 Windows 程序列表中识别“{item.Name}”的卸载程序。是否打开“已安装的应用”页面手动查找？",
                    "卸载软件", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true });
            return;
        }

        if (MessageBox.Show(owner,
                $"即将打开“{application.DisplayName}”的卸载程序。请在随后出现的卸载向导中确认操作。要继续吗？",
                "卸载软件", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        try
        {
            var (executable, arguments) = SplitCommand(application.UninstallCommand);
            if (Path.GetFileNameWithoutExtension(executable).Equals("msiexec", StringComparison.OrdinalIgnoreCase))
                arguments = Regex.Replace(arguments, @"(?i)(^|\s)/I(?=\s*\{)", "$1/X");
            Process.Start(new ProcessStartInfo(executable, arguments) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, $"无法启动卸载程序：{ex.Message}", "卸载软件", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static InstalledApplication? FindApplication(DesktopItem item, string? target)
    {
        var targetPath = NormalizeExecutablePath(target);
        var itemName = NormalizeName(item.Name);
        return ReadInstalledApplications()
            .Select(app => new
            {
                App = app,
                IconPath = NormalizeExecutablePath(ReadIconExecutable(app.DisplayIcon)),
                Location = NormalizeDirectory(app.InstallLocation),
                NameMatch = NormalizeName(app.DisplayName) == itemName
            })
            .Where(entry => entry.App.UninstallCommand.Length > 0)
            .Select(entry => new
            {
                entry.App,
                Score = targetPath.Length > 0 && entry.IconPath == targetPath ? 3 :
                        targetPath.Length > 0 && entry.Location.Length > 0 && targetPath.StartsWith(entry.Location, StringComparison.OrdinalIgnoreCase) ? 2 :
                        entry.NameMatch ? 1 : 0
            })
            .Where(entry => entry.Score > 0)
            .OrderByDescending(entry => entry.Score)
            .Select(entry => entry.App)
            .Select(app => new InstalledApplication(app.DisplayName, app.UninstallCommand))
            .FirstOrDefault();
    }

    private static IEnumerable<RegistryApplication> ReadInstalledApplications()
    {
        var applications = new List<RegistryApplication>();
        var locations = new[]
        {
            (RegistryHive.CurrentUser, RegistryView.Default, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.LocalMachine, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, @"Software\Microsoft\Windows\CurrentVersion\Uninstall")
        };
        foreach (var (hive, view, path) in locations)
        {
            RegistryKey? baseKey = null;
            RegistryKey? uninstallKey = null;
            try
            {
                baseKey = RegistryKey.OpenBaseKey(hive, view);
                uninstallKey = baseKey.OpenSubKey(path);
                if (uninstallKey is null) continue;
                string[] subKeyNames;
                try { subKeyNames = uninstallKey.GetSubKeyNames(); }
                catch (UnauthorizedAccessException) { continue; }
                catch (IOException) { continue; }
                foreach (var subKeyName in subKeyNames)
                {
                    try
                    {
                        using var appKey = uninstallKey.OpenSubKey(subKeyName);
                        if (appKey is null) continue;
                        var displayName = appKey.GetValue("DisplayName") as string;
                        var uninstallCommand = appKey.GetValue("UninstallString") as string;
                        if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(uninstallCommand)) continue;
                        applications.Add(new RegistryApplication(
                            displayName.Trim(),
                            Environment.ExpandEnvironmentVariables(uninstallCommand.Trim()),
                            Environment.ExpandEnvironmentVariables((appKey.GetValue("DisplayIcon") as string ?? "").Trim()),
                            Environment.ExpandEnvironmentVariables((appKey.GetValue("InstallLocation") as string ?? "").Trim())));
                    }
                    catch (UnauthorizedAccessException) { }
                    catch (IOException) { }
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
            finally
            {
                uninstallKey?.Dispose();
                baseKey?.Dispose();
            }
        }
        return applications;
    }

    private static string NormalizeExecutablePath(string? value)
    {
        var path = ReadIconExecutable(value ?? "");
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant(); }
        catch { return path.TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant(); }
    }

    private static string NormalizeDirectory(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        try { return Path.GetFullPath(value.Trim().Trim('"')).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant(); }
        catch { return value.Trim().Trim('"').TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant(); }
    }

    private static string ReadIconExecutable(string value)
    {
        var command = Environment.ExpandEnvironmentVariables(value.Trim());
        if (command.StartsWith('"'))
        {
            var closeQuote = command.IndexOf('"', 1);
            if (closeQuote > 1) return command[1..closeQuote];
        }
        var exeIndex = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exeIndex >= 0 ? command[..(exeIndex + 4)] : command.Split(',')[0].Trim();
    }

    private static string NormalizeName(string value) =>
        string.Concat(value.Where(char.IsLetterOrDigit)).ToUpperInvariant();

    private static (string Executable, string Arguments) SplitCommand(string command)
    {
        command = Environment.ExpandEnvironmentVariables(command.Trim());
        if (command.StartsWith('"'))
        {
            var closeQuote = command.IndexOf('"', 1);
            if (closeQuote > 1) return (command[1..closeQuote], command[(closeQuote + 1)..].Trim());
        }

        var exeIndex = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex >= 0)
        {
            var executableEnd = exeIndex + 4;
            return (command[..executableEnd].Trim(), command[executableEnd..].Trim());
        }

        var firstSpace = command.IndexOf(' ');
        return firstSpace < 0 ? (command, "") : (command[..firstSpace], command[(firstSpace + 1)..].Trim());
    }

    private sealed record RegistryApplication(string DisplayName, string UninstallCommand, string DisplayIcon, string InstallLocation);
}

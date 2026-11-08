using DesktopManager.Models;

namespace DesktopManager.Services;

public sealed class DesktopItemDiscovery
{
    public IReadOnlyList<DesktopItem> GetItems(AppState state)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var taskbarPins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var userDesktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var commonDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        AddEntries(paths, userDesktop);
        AddEntries(paths, commonDesktop);
        var taskbarPinsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar");
        AddTaskbarPins(paths, taskbarPins, taskbarPinsDirectory);
        foreach (var assignedPath in state.DesktopAssignments.Keys)
            if (File.Exists(assignedPath) || Directory.Exists(assignedPath))
                paths.Add(assignedPath);
        var hiddenTaskbarPins = new HashSet<string>(state.HiddenTaskbarItems ?? [], StringComparer.OrdinalIgnoreCase);
        paths.ExceptWith(taskbarPins.Where(path => hiddenTaskbarPins.Contains(Normalize(path))));
        WriteDiagnostic($"desktop user='{userDesktop}' exists={Directory.Exists(userDesktop)} common='{commonDesktop}' exists={Directory.Exists(commonDesktop)} taskbarPins={taskbarPins.Count} hiddenPins={hiddenTaskbarPins.Count} entries={paths.Count}");

        var items = paths.Select(path =>
            {
                var key = Normalize(path);
                var group = state.DesktopAssignments.TryGetValue(key, out var assigned) ? assigned : "未分组";
                var directory = Directory.Exists(path);
                var kind = directory ? "文件夹" : Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase) ? "快捷方式" : "文件";
                var name = directory ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
                return new DesktopItem(string.IsNullOrWhiteSpace(name) ? path : name, path, group, kind, taskbarPins.Contains(path));
            })
            .OrderBy(item => item.Group == "未分组" ? 1 : 0)
            .ThenBy(item => item.Group, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        items.AddRange(GetVirtualShellItems(state));
        items = items
            .OrderBy(item => item.Group == "未分组" ? 1 : 0)
            .ThenBy(item => item.Group, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var groups = string.Join(",", items.GroupBy(item => item.Group).Select(group => $"{group.Key}:{group.Count()}"));
        WriteDiagnostic($"discovered={items.Count} assignments={state.DesktopAssignments.Count} groups=[{groups}]");
        return items;
    }

    private static readonly Guid RecycleBinGuid = new("645FF040-5081-101B-9F08-00AA002F954E");
    private static readonly Guid ThisPcGuid = new("20D04FE0-3AEA-1069-A2D8-08002B30309D");
    private static readonly Guid NetworkGuid = new("208D2C60-3AEA-1069-A2D7-08002B30309D");
    private static readonly Guid ControlPanelGuid = new("5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0");

    private static IEnumerable<DesktopItem> GetVirtualShellItems(AppState state)
    {
        const string classicKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel";
        const string policyKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer";
        var classic = ReadDword(classicKey);
        var policy = ReadDword(policyKey);
        bool Visible(Guid guid)
        {
            var guidString = $"{{{guid.ToString().ToUpperInvariant()}}}";
            if (classic.TryGetValue(guidString, out var classicValue))
                return classicValue == 0;
            if (policy.TryGetValue(guidString, out var policyValue))
                return policyValue == 0;
            // Windows shows the Recycle Bin by default (value 1 means hide); other
            // virtual icons are hidden by default (value 0 means show).
            return guid == RecycleBinGuid;
        }
        var result = new List<DesktopItem>();
        var entries = new[]
        {
            (RecycleBinGuid, "回收站"),
            (ThisPcGuid, "此电脑"),
            (NetworkGuid, "网络"),
            (ControlPanelGuid, "控制面板")
        };
        foreach (var (guid, name) in entries)
        {
            if (!Visible(guid)) continue;
            var group = state.DesktopAssignments.TryGetValue(Normalize(name), out var assigned) ? assigned : "未分组";
            result.Add(new DesktopItem(name, name, group, "系统", false) { IsVirtual = true });
        }
        return result;
    }

    private static Dictionary<string, int> ReadDword(string keyPath)
    {
        var values = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath) ?? Microsoft.Win32.Registry.LocalMachine.OpenSubKey(keyPath);
            if (key is null) return values;
            foreach (var valueName in key.GetValueNames())
                if (key.GetValue(valueName) is int intValue) values[valueName] = intValue;
        }
        catch (Exception) { }
        return values;
    }

    public static string Normalize(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();

    private static void AddEntries(HashSet<string> paths, string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                if (!string.Equals(Path.GetFileName(path), "desktop.ini", StringComparison.OrdinalIgnoreCase)) paths.Add(path);
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    private static void AddTaskbarPins(HashSet<string> paths, HashSet<string> taskbarPins, string directory)
    {
        if (!Directory.Exists(directory)) return;
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory)
                         .Where(path => Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase)))
            {
                paths.Add(path);
                taskbarPins.Add(path);
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    private static void WriteDiagnostic(string message)
    {
        try
        {
            var path = Path.Combine(AppDataStore.SharedDataDirectory, "desktop-host.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"{DateTime.Now:O} {message}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>
    /// Startup-stage tracing. The desktop layer runs before the UI is interactive, and an
    /// early hang or exception leaves no UI trace, so each constructor step is recorded.
    /// </summary>
    public static void WriteStartupStage(string message)
    {
        try
        {
            var path = Path.Combine(AppDataStore.SharedDataDirectory, "desktop-startup.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"{DateTime.Now:O} {message}{Environment.NewLine}");
        }
        catch { }
    }
}

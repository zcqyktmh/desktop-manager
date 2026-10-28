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
            .ToArray();
        var groups = string.Join(",", items.GroupBy(item => item.Group).Select(group => $"{group.Key}:{group.Count()}"));
        WriteDiagnostic($"discovered={items.Length} assignments={state.DesktopAssignments.Count} groups=[{groups}]");
        return items;
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
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopManager", "desktop-host.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"{DateTime.Now:O} {message}{Environment.NewLine}");
        }
        catch { }
    }
}

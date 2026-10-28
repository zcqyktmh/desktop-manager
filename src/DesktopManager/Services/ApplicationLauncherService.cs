using System.Runtime.InteropServices;
using System.Text;
using DesktopManager.Models;

namespace DesktopManager.Services;

public sealed record LauncherApplication(string Name, string ShortcutPath, string TargetPath);

public static class ApplicationLauncherService
{
    public static IReadOnlyList<LauncherApplication> GetApplications()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
        }.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
        var apps = new Dictionary<string, LauncherApplication>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories).ToArray(); }
            catch { continue; }
            foreach (var path in files)
            {
                var target = DesktopItemIconService.ResolveShortcutTarget(path) ?? "";
                var name = Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrWhiteSpace(name) || name.Equals("Uninstall", StringComparison.OrdinalIgnoreCase)) continue;
                var key = string.IsNullOrWhiteSpace(target) ? path : target;
                apps.TryAdd(key, new LauncherApplication(name, path, target));
            }
        }
        return apps.Values.OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
}

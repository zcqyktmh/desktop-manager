using System.Collections.ObjectModel;
using System.Text.Json;
using DesktopManager.Models;

namespace DesktopManager.Services;

public sealed class AppDataStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// The shared profile folder for every file the app owns. See <see cref="ResolveDataDirectory"/>
    /// for why this must not rely on SpecialFolder.LocalApplicationData.
    /// </summary>
    public static string SharedDataDirectory { get; } = ResolveDataDirectory();

    public string DataDirectory { get; } = SharedDataDirectory;
    public string DataFile => Path.Combine(DataDirectory, "data.json");

    /// <summary>
    /// Environment.SpecialFolder.LocalApplicationData is redirected by AppContainer sandboxes
    /// (MSIX packaged apps, Store apps, some launchers) into a per-package LocalCache folder. A
    /// desktop replacement must always read and write the same user-visible profile folder, so
    /// the path is resolved from the user profile instead of the sandboxed special folder.
    /// </summary>
    private static string ResolveDataDirectory()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            var candidate = Path.Combine(profile, "AppData", "Local", "DesktopManager");
            if (Directory.Exists(candidate) || CanCreateDirectory(candidate)) return candidate;
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopManager");
    }

    private static bool CanCreateDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public AppState Load()
    {
        Directory.CreateDirectory(DataDirectory);
        if (!File.Exists(DataFile))
        {
            var initial = new AppState();
            Save(initial);
            return initial;
        }

        try
        {
            var state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(DataFile), JsonOptions) ?? new AppState();
            if (LooksTruncated(state))
            {
                // The stored file was written by a build that lost the user's groups. Keep the
                // original on disk instead of overwriting it with the same empty state.
                var preserved = DataFile + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Copy(DataFile, preserved, overwrite: false);
                var recovered = RecoverFromPreservedCopies() ?? new AppState();
                Save(recovered);
                return recovered;
            }

            state.Bookmarks ??= new ObservableCollection<Bookmark>();
            state.DesktopGroups ??= ["工作常用", "系统工具", "临时收集"];
            state.DesktopAssignments = new Dictionary<string, string>(state.DesktopAssignments ?? [], StringComparer.OrdinalIgnoreCase);
            state.HiddenTaskbarItems ??= [];
            state.GroupLayouts = new Dictionary<string, DesktopGroupLayout>(state.GroupLayouts ?? [], StringComparer.OrdinalIgnoreCase);
            state.GroupDisplayModes = new Dictionary<string, string>(state.GroupDisplayModes ?? [], StringComparer.OrdinalIgnoreCase);
            state.AutoWallpaperIntervalMinutes = Math.Clamp(state.AutoWallpaperIntervalMinutes, 15, 1440);
            state.AutoWallpaperStyle ??= "风景";
            state.AutoWallpaperCustomQuery ??= "";
            state.BookmarkCardLayout ??= new DesktopGroupLayout { Left = 365, Width = 350, Height = 240 };
            state.SystemInfoCardLayout ??= new DesktopGroupLayout { Left = -1, Top = -1, Width = 320, Height = 250 };
            state.WeatherCity ??= "";
            state.WeatherCardLayout ??= new DesktopGroupLayout { Left = -1, Top = -1, Width = 300, Height = 210 };
            state.CardZOrder ??= [];
            return state;
        }
        catch (JsonException)
        {
            File.Copy(DataFile, DataFile + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), overwrite: false);
            var initial = new AppState();
            Save(initial);
            return initial;
        }
    }

    private static bool LooksTruncated(AppState state) =>
        state.DesktopGroups.Count == 0 && state.DesktopAssignments.Count == 0;

    /// <summary>
    /// Looks for an earlier copy that still holds the user's group assignments and restores it.
    /// </summary>
    private AppState? RecoverFromPreservedCopies()
    {
        try
        {
            var candidates = Directory.GetFiles(DataDirectory, "data.json*")
                .Where(path => !string.Equals(path, DataFile, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToArray();

            foreach (var candidate in candidates)
            {
                try
                {
                    var state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(candidate), JsonOptions);
                    if (state is null || LooksTruncated(state)) continue;
                    return state;
                }
                catch (Exception)
                {
                    // Ignore unreadable snapshots and try the next one.
                }
            }
        }
        catch (Exception)
        {
            // Recovery is best effort; the caller falls back to a fresh state.
        }

        return null;
    }

    public void Save(AppState state)
    {
        Directory.CreateDirectory(DataDirectory);
        var tempFile = DataFile + ".tmp";
        File.WriteAllText(tempFile, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(tempFile, DataFile, overwrite: true);
    }
}

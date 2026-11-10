using System.Collections.ObjectModel;
using System.Text.Json;
using DesktopManager.Models;

namespace DesktopManager.Services;

public sealed class AppDataStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private const int MaxRollingBackups = 8;

    /// <summary>
    /// The shared profile folder for every file the app owns. See <see cref="ResolveDataDirectory"/>
    /// for why this must not rely on SpecialFolder.LocalApplicationData.
    /// </summary>
    public static string SharedDataDirectory { get; } = ResolveDataDirectory();

    public string DataDirectory { get; } = SharedDataDirectory;
    public string DataFile => Path.Combine(DataDirectory, "data.json");

    /// <summary>
    /// Data lives next to the executable, in a plain folder the app owns outright.
    ///
    /// Under %LOCALAPPDATA% the file is subject to several mechanisms that make the bytes the
    /// process reads differ from the bytes on disk: AppContainer sandboxes redirect the folder
    /// into a per-package LocalCache, and this machine's profile also exposes the same file
    /// through a hard link. The symptom was that Explorer-launched instances read a stale
    /// default state while the file on disk still held the user's groups.
    ///
    /// A folder beside the app is free of all of that, so Explorer startup and interactive
    /// startup see exactly the same bytes.
    /// </summary>
    private static string ResolveDataDirectory()
    {
        var baseDirectory = AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(baseDirectory))
        {
            var candidate = Path.Combine(baseDirectory, "data");
            if (CanCreateDirectory(candidate)) return candidate;
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            var candidate = Path.Combine(profile, "AppData", "Local", "DesktopManager");
            if (CanCreateDirectory(candidate)) return candidate;
        }

        return Path.Combine(Path.GetTempPath(), "DesktopManager");
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

    private static void Trace(string message)
    {
        try
        {
            var path = Path.Combine(SharedDataDirectory, "desktop-startup.log");
            Directory.CreateDirectory(SharedDataDirectory);
            File.AppendAllText(path, $"{DateTime.Now:O} {message}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>
    /// One-time import of a state saved under the old %LOCALAPPDATA% folder, so users who
    /// already arranged their desktop keep their groups after the data folder moved.
    /// </summary>
    private void MigrateFromProfileDirectory()
    {
        if (File.Exists(DataFile)) return;

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profile)) return;

        var legacy = Path.Combine(profile, "AppData", "Local", "DesktopManager", "data.json");
        if (!File.Exists(legacy)) return;

        try
        {
            var state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(legacy), JsonOptions);
            if (state is null || LooksEmpty(state))
            {
                Trace("Migrate: legacy file held no real data, skipping");
                return;
            }

            Save(state);
            Trace($"Migrate: imported legacy state groups=[{string.Join(",", state.DesktopGroups)}] assign={(state.DesktopAssignments?.Count ?? 0)}");
        }
        catch (Exception ex)
        {
            Trace($"Migrate: failed ({ex.GetType().Name})");
        }
    }

    /// <summary>
    /// True when the state carries no user data: either truly empty, or still holding the
    /// factory default group names with no icon assignments. A fresh AppState has three default
    /// group names, so counting groups alone is not enough to tell it apart from real data.
    /// </summary>
    private static bool LooksEmpty(AppState state)
    {
        var assignments = state.DesktopAssignments?.Count ?? 0;
        var groups = state.DesktopGroups;
        var onlyDefaults = groups is null || groups.Count == 0 ||
            groups.All(name => string.Equals(name, "工作常用", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(name, "系统工具", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(name, "临时收集", StringComparison.OrdinalIgnoreCase));
        return assignments == 0 && onlyDefaults;
    }

    public AppState Load()
    {
        Directory.CreateDirectory(DataDirectory);
        MigrateFromProfileDirectory();
        var exists = File.Exists(DataFile);
        var length = exists ? new FileInfo(DataFile).Length : -1;
        Trace($"Load: file='{DataFile}' exists={exists} length={length}");
        if (!File.Exists(DataFile))
        {
            var initial = new AppState();
            Save(initial);
            return initial;
        }

        try
        {
            var text = File.ReadAllText(DataFile);
            var state = JsonSerializer.Deserialize<AppState>(text, JsonOptions) ?? new AppState();
            var groups = state.DesktopGroups;
            Trace($"Load: parsed groups=[{(groups is null ? "null" : string.Join(",", groups))}] assign={(state.DesktopAssignments is null ? "null" : state.DesktopAssignments.Count.ToString())} empty={LooksEmpty(state)}");
            if (LooksEmpty(state))
            {
                var preserved = DataFile + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Copy(DataFile, preserved, overwrite: false);
                Trace($"Load: empty state detected, preserved {Path.GetFileName(preserved)}");
                var recovered = RecoverFromPreservedCopies();
                if (recovered is not null)
                {
                    Trace("Load: recovered from snapshot");
                    Save(recovered);
                    return Normalize(recovered);
                }
                Trace("Load: no usable snapshot");
            }
            return Normalize(state);
        }
        catch (JsonException)
        {
            File.Copy(DataFile, DataFile + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), overwrite: false);
            var initial = new AppState();
            Save(initial);
            return initial;
        }
    }

    private static AppState Normalize(AppState state)
    {
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
        state.TodoItems ??= [];
        state.TodoCardLayout ??= new DesktopGroupLayout { Left = -1, Top = -1, Width = 320, Height = 360 };
        if (!state.TodoCardPreferenceInitialized)
        {
            state.TodoCardEnabled = true;
            state.TodoCardPreferenceInitialized = true;
        }
        return state;
    }

    /// <summary>
    /// Looks for the most recent snapshot that still holds the user's group assignments.
    /// </summary>
    private AppState? RecoverFromPreservedCopies()
    {
        try
        {
            var candidates = Directory.GetFiles(DataDirectory, "snapshot-*.json")
                .Concat(Directory.GetFiles(DataDirectory, "data.json.bak*"))
                .Concat(Directory.GetFiles(DataDirectory, "data.json.backup*"))
                .Where(path => !string.Equals(path, DataFile, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToArray();

            foreach (var candidate in candidates)
            {
                try
                {
                    var state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(candidate), JsonOptions);
                    if (state is null || LooksEmpty(state)) continue;
                    Trace($"Recover: using {Path.GetFileName(candidate)}");
                    return Normalize(state);
                }
                catch (Exception)
                {
                }
            }
        }
        catch (Exception)
        {
        }

        return null;
    }

    public void Save(AppState state)
    {
        Directory.CreateDirectory(DataDirectory);

        // Never let an empty in-memory state overwrite a file that still holds real data. If a
        // transient UI or startup path ends up with no groups or assignments, the existing file
        // is worth more than the empty one, so the write is skipped and reported.
        if (File.Exists(DataFile) && LooksEmpty(state))
        {
            try
            {
                var existing = JsonSerializer.Deserialize<AppState>(File.ReadAllText(DataFile), JsonOptions);
                if (existing is not null && !LooksEmpty(existing))
                {
                    Trace("Save: refused to overwrite populated data with an empty state");
                    return;
                }
            }
            catch (JsonException)
            {
                // Unreadable existing file: fall through and overwrite it.
            }
        }

        WriteRollingBackup();
        var tempFile = DataFile + ".tmp";
        File.WriteAllText(tempFile, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(tempFile, DataFile, overwrite: true);
    }

    /// <summary>Keeps a short history of good states so a single bad write is recoverable.</summary>
    private void WriteRollingBackup()
    {
        try
        {
            if (!File.Exists(DataFile)) return;
            var stamp = File.GetLastWriteTimeUtc(DataFile).ToLocalTime().ToString("yyyyMMdd-HHmmss");
            var target = Path.Combine(DataDirectory, $"snapshot-{stamp}.json");
            if (File.Exists(target)) return;
            File.Copy(DataFile, target, overwrite: false);

            var snapshots = Directory.GetFiles(DataDirectory, "snapshot-*.json")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Skip(MaxRollingBackups)
                .ToArray();
            foreach (var stale in snapshots)
                File.Delete(stale);
        }
        catch (Exception)
        {
            // Backups are best effort and must never block the main save.
        }
    }
}

using System.Collections.ObjectModel;
using System.Text.Json;
using DesktopManager.Models;

namespace DesktopManager.Services;

public sealed class AppDataStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string DataDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopManager");
    public string DataFile => Path.Combine(DataDirectory, "data.json");

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

    public void Save(AppState state)
    {
        Directory.CreateDirectory(DataDirectory);
        var tempFile = DataFile + ".tmp";
        File.WriteAllText(tempFile, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(tempFile, DataFile, overwrite: true);
    }
}

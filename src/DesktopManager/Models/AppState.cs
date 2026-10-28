using System.Collections.ObjectModel;

namespace DesktopManager.Models;

public sealed class AppState
{
    public ObservableCollection<Bookmark> Bookmarks { get; set; } = [];
    public ObservableCollection<string> DesktopGroups { get; set; } = ["工作常用", "系统工具", "临时收集"];
    public Dictionary<string, string> DesktopAssignments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> HiddenTaskbarItems { get; set; } = [];
    public Dictionary<string, DesktopGroupLayout> GroupLayouts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> GroupDisplayModes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> CardZOrder { get; set; } = [];
    public double LayoutCanvasWidth { get; set; }
    public double LayoutCanvasHeight { get; set; }
    public DesktopGroupLayout BookmarkCardLayout { get; set; } = new() { Left = 365, Width = 350, Height = 240 };
    public string Theme { get; set; } = "System";
    public string WallpaperPath { get; set; } = "";
    public string WallpaperTitle { get; set; } = "";
    public string WallpaperAuthor { get; set; } = "";
    public string WallpaperLicense { get; set; } = "";
    public string WallpaperSourceUrl { get; set; } = "";
    public bool AutoWallpaperEnabled { get; set; }
    public int AutoWallpaperIntervalMinutes { get; set; } = 60;
    public string AutoWallpaperStyle { get; set; } = "风景";
    public string AutoWallpaperCustomQuery { get; set; } = "";
    public DateTime? AutoWallpaperLastChangedUtc { get; set; }
    public bool SystemInfoCardEnabled { get; set; }
    public bool SystemInfoShowCpu { get; set; } = true;
    public bool SystemInfoShowMemory { get; set; } = true;
    public bool SystemInfoShowCpuTemperature { get; set; } = true;
    public bool SystemInfoShowGpuTemperature { get; set; } = true;
    public bool SystemInfoShowSystemDrive { get; set; }
    public DesktopGroupLayout SystemInfoCardLayout { get; set; } = new() { Left = -1, Top = -1, Width = 320, Height = 250 };
    public bool StartWithWindows { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool WeatherCardEnabled { get; set; }
    public string WeatherCity { get; set; } = "";
    public DesktopGroupLayout WeatherCardLayout { get; set; } = new() { Left = -1, Top = -1, Width = 300, Height = 210 };
    public int DesktopIconSize { get; set; } = 40;
}

public sealed class DesktopGroupLayout
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; } = 350;
    public double Height { get; set; } = 190;
}

public sealed class Bookmark
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string Folder { get; set; } = "未分类";
    public bool ShowOnHome { get; set; } = true;
    public string Group { get; set; } = "常用网址";
}

public sealed record DesktopItem(string Name, string Path, string Group, string Kind, bool IsTaskbarShortcut = false);

public sealed record DesktopCardItem(string Name, string Path, string Group, string Kind, Bookmark? Bookmark = null)
{
    public bool IsBookmark => Bookmark is not null;
}

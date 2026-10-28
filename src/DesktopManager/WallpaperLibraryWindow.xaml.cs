using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Collections.ObjectModel;
using DesktopManager.Services;

namespace DesktopManager;

public partial class WallpaperLibraryWindow : Window
{
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmSystemBackdropAcrylic = 3;
    private const int DwmWindowCornerRound = 2;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    private bool _loading;
    public WallpaperSearchResult? SelectedWallpaper { get; private set; }
    public string? SelectedWallpaperPath { get; private set; }

    public WallpaperLibraryWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyAcrylicBackdrop();
        Loaded += async (_, _) => await SearchWallpapersAsync("landscape wallpaper panorama");
    }

    private async void Search_Click(object sender, RoutedEventArgs e) =>
        await SearchWallpapersAsync(string.IsNullOrWhiteSpace(SearchBox.Text) ? "landscape wallpaper panorama" : SearchBox.Text.Trim());

    private async void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string query }) return;
        SearchBox.Text = query;
        await SearchWallpapersAsync(query);
    }

    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SearchWallpapersAsync(string.IsNullOrWhiteSpace(SearchBox.Text) ? "landscape wallpaper panorama" : SearchBox.Text.Trim());
    }

    private async Task SearchWallpapersAsync(string query)
    {
        if (_loading) return;
        _loading = true;
        StatusText.Text = "正在搜索壁纸…";
        Results.ItemsSource = null;
        try
        {
            var wallpapers = (await WallpaperLibraryService.SearchAsync(query)).Take(18).ToArray();
            var cards = new ObservableCollection<WallpaperSearchResult>(wallpapers);
            Results.ItemsSource = cards;
            StatusText.Text = wallpapers.Length == 0
                ? "没有找到横向高清图片，试试更短的英文关键词。"
                : $"找到 {wallpapers.Length} 张横向高清图片，正在载入预览…";
            using var throttle = new SemaphoreSlim(6, 6);
            await Task.WhenAll(wallpapers.Select(async (wallpaper, index) =>
            {
                await throttle.WaitAsync();
                try
                {
                    var thumbnail = await WallpaperLibraryService.LoadThumbnailAsync(wallpaper.ThumbnailUrl);
                    if (thumbnail is not null) cards[index] = wallpaper with { Thumbnail = thumbnail };
                }
                finally { throttle.Release(); }
            }));
            StatusText.Text = wallpapers.Length == 0
                ? "没有找到横向高清图片，试试更短的英文关键词。"
                : $"已载入 {wallpapers.Length} 张壁纸 · 选择后会下载到本机";
        }
        catch (Exception ex)
        {
            StatusText.Text = "壁纸库暂时无法连接。请检查网络后重试。";
            MessageBox.Show(this, $"无法连接 Wikimedia Commons：{ex.Message}", "壁纸库", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally { _loading = false; }
    }

    private async void SelectWallpaper_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: WallpaperSearchResult wallpaper } button) return;
        button.IsEnabled = false;
        StatusText.Text = $"正在下载“{wallpaper.Title}”…";
        try
        {
            SelectedWallpaperPath = await WallpaperLibraryService.DownloadAsync(wallpaper);
            SelectedWallpaper = wallpaper;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            button.IsEnabled = true;
            StatusText.Text = "下载失败，请检查网络后重试。";
            MessageBox.Show(this, $"无法下载这张壁纸：{ex.Message}", "壁纸库", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenSource_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: WallpaperSearchResult wallpaper }) return;
        Process.Start(new ProcessStartInfo(wallpaper.DescriptionUrl) { UseShellExecute = true });
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void ZoomWindow_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ZoomWindow_Click(sender, e);
            return;
        }
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void ApplyAcrylicBackdrop()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var backdrop = DwmSystemBackdropAcrylic;
            DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdrop, sizeof(int));
            var corners = DwmWindowCornerRound;
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corners, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }
}

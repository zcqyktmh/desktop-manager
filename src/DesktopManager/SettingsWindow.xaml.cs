using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using DesktopManager.Models;
using DesktopManager.Services;
using Microsoft.Win32;

namespace DesktopManager;

public partial class SettingsWindow : Window
{
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmSystemBackdropAcrylic = 3;
    private const int DwmWindowCornerRound = 2;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    private readonly AppState _state;
    private readonly StartupService _startup;
    private readonly AppDataStore _store;
    private readonly Action _save;
    private readonly Action _exit;
    private readonly Action<string?> _wallpaperChanged;

    public SettingsWindow(AppState state, StartupService startup, AppDataStore store, Action save, Action exit, Action<string?> wallpaperChanged)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyAcrylicBackdrop();
        _state = state; _startup = startup; _store = store; _save = save; _exit = exit; _wallpaperChanged = wallpaperChanged;
        ThemePicker.ItemsSource = new[] { "跟随 Windows", "浅色", "深色" };
        ThemePicker.SelectedItem = state.Theme switch { "Light" => "浅色", "Dark" => "深色", _ => "跟随 Windows" };
        StartupCheck.IsChecked = state.StartWithWindows;
        TrayCheck.IsChecked = state.CloseToTray;
        SystemInfoCardCheck.IsChecked = state.SystemInfoCardEnabled;
        ShowCpuCheck.IsChecked = state.SystemInfoShowCpu;
        ShowMemoryCheck.IsChecked = state.SystemInfoShowMemory;
        ShowCpuTemperatureCheck.IsChecked = state.SystemInfoShowCpuTemperature;
        ShowGpuTemperatureCheck.IsChecked = state.SystemInfoShowGpuTemperature;
        ShowSystemDriveCheck.IsChecked = state.SystemInfoShowSystemDrive;
        WeatherCardCheck.IsChecked = state.WeatherCardEnabled;
        WeatherCityBox.Text = state.WeatherCity;
        state.DesktopIconSize = Math.Clamp(state.DesktopIconSize, 32, 80);
        IconSizeSlider.Value = state.DesktopIconSize;
        UpdateIconSizeDescription(state.DesktopIconSize);
        IconSizeSlider.ValueChanged += (_, e) =>
        {
            var size = (int)e.NewValue;
            IconSizeValue.Text = $"{size} 像素";
            UpdateIconSizeDescription(size);
        };
        ThemePicker.SelectionChanged += Theme_Changed;
        StartupCheck.Click += Startup_Click;
        TrayCheck.Click += (_, _) => { _state.CloseToTray = TrayCheck.IsChecked == true; _save(); };
        SystemInfoCardCheck.Click += (_, _) => { _state.SystemInfoCardEnabled = SystemInfoCardCheck.IsChecked == true; _save(); };
        ShowCpuCheck.Click += (_, _) => { _state.SystemInfoShowCpu = ShowCpuCheck.IsChecked == true; _save(); };
        ShowMemoryCheck.Click += (_, _) => { _state.SystemInfoShowMemory = ShowMemoryCheck.IsChecked == true; _save(); };
        ShowCpuTemperatureCheck.Click += (_, _) => { _state.SystemInfoShowCpuTemperature = ShowCpuTemperatureCheck.IsChecked == true; _save(); };
        ShowGpuTemperatureCheck.Click += (_, _) => { _state.SystemInfoShowGpuTemperature = ShowGpuTemperatureCheck.IsChecked == true; _save(); };
        ShowSystemDriveCheck.Click += (_, _) => { _state.SystemInfoShowSystemDrive = ShowSystemDriveCheck.IsChecked == true; _save(); };
        WeatherCardCheck.Click += (_, _) => { _state.WeatherCardEnabled = WeatherCardCheck.IsChecked == true; _save(); };
        WeatherCityBox.TextChanged += (_, _) => { _state.WeatherCity = WeatherCityBox.Text.Trim(); _save(); };
        BackupButton.Click += (_, _) => ExportBackup();
        UpdateWallpaperDescription();
        WallpaperLibraryButton.Click += (_, _) => OpenWallpaperLibrary();
        ChooseWallpaperButton.Click += (_, _) => ChooseWallpaper();
        AutoWallpaperStylePicker.ItemsSource = new[] { "风景", "自然", "星空", "海洋", "城市", "自定义关键词" };
        AutoWallpaperStylePicker.SelectedItem = state.AutoWallpaperStyle;
        AutoWallpaperIntervalPicker.ItemsSource = new[]
        {
            new { Label = "每 15 分钟", Minutes = 15 },
            new { Label = "每 30 分钟", Minutes = 30 },
            new { Label = "每 1 小时", Minutes = 60 },
            new { Label = "每 3 小时", Minutes = 180 },
            new { Label = "每 6 小时", Minutes = 360 },
            new { Label = "每 12 小时", Minutes = 720 },
            new { Label = "每天", Minutes = 1440 }
        };
        AutoWallpaperIntervalPicker.DisplayMemberPath = "Label";
        AutoWallpaperIntervalPicker.SelectedValuePath = "Minutes";
        AutoWallpaperIntervalPicker.SelectedValue = state.AutoWallpaperIntervalMinutes;
        AutoWallpaperCheck.IsChecked = state.AutoWallpaperEnabled;
        AutoWallpaperCustomQueryBox.Text = state.AutoWallpaperCustomQuery;
        UpdateCustomWallpaperQueryVisibility();
        AutoWallpaperStylePicker.SelectionChanged += (_, _) =>
        {
            if (AutoWallpaperStylePicker.SelectedItem is string style) _state.AutoWallpaperStyle = style;
            UpdateCustomWallpaperQueryVisibility();
            _save();
        };
        AutoWallpaperIntervalPicker.SelectionChanged += (_, _) =>
        {
            if (AutoWallpaperIntervalPicker.SelectedValue is int minutes) _state.AutoWallpaperIntervalMinutes = minutes;
            _save();
        };
        AutoWallpaperCheck.Click += (_, _) => { _state.AutoWallpaperEnabled = AutoWallpaperCheck.IsChecked == true; _save(); };
        AutoWallpaperCustomQueryBox.TextChanged += (_, _) => { _state.AutoWallpaperCustomQuery = AutoWallpaperCustomQueryBox.Text.Trim(); _save(); };
        ResetWallpaperButton.Click += (_, _) =>
        {
            _state.WallpaperPath = "";
            _state.WallpaperTitle = "";
            _state.WallpaperAuthor = "";
            _state.WallpaperLicense = "";
            _state.WallpaperSourceUrl = "";
            UpdateWallpaperDescription();
            _save();
            _wallpaperChanged(null);
        };
    }

    private void UpdateCustomWallpaperQueryVisibility() =>
        CustomWallpaperQueryRow.Visibility = string.Equals(AutoWallpaperStylePicker.SelectedItem as string, "自定义关键词", StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ThemePicker.SelectedItem is not string value) return;
        _state.Theme = value switch { "浅色" => "Light", "深色" => "Dark", _ => "System" };
        ThemeService.Apply(_state.Theme); _save();
    }

    private void SectionToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton toggle || toggle.Tag is not string sectionName) return;
        var targetName = sectionName switch
        {
            "外观与图标" => "AppearanceSectionCard",
            "桌面信息卡片" => "SystemInfoSectionCard",
            "天气小组件" => "WeatherSectionCard",
            "启动与行为" => "BehaviorSectionCard",
            "数据" => "DataSectionCard",
            _ => ""
        };
        if (FindName(targetName) is UIElement content)
            content.Visibility = toggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        toggle.Content = $"{(toggle.IsChecked == true ? "⌄" : "›")}  {sectionName}";
    }

    private void UpdateIconSizeDescription(int size)
    {
        IconSizeValue.Text = $"{size} 像素";
        IconSizeDescription.Text = "调整桌面分组中图标的显示大小";
    }

    private void Startup_Click(object sender, RoutedEventArgs e)
    {
        var requested = StartupCheck.IsChecked == true;
        try { _startup.SetEnabled(requested); _state.StartWithWindows = requested; _save(); }
        catch (Exception ex) { StartupCheck.IsChecked = _state.StartWithWindows; MessageBox.Show(this, $"无法更新启动设置：{ex.Message}", "桌面管家", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void ExportBackup()
    {
        var dialog = new SaveFileDialog { Title = "导出桌面管家备份", Filter = "JSON 文件 (*.json)|*.json", DefaultExt = ".json", FileName = $"桌面管家备份-{DateTime.Now:yyyyMMdd}.json" };
        if (dialog.ShowDialog(this) != true) return;
        try { _store.Save(_state); File.Copy(_store.DataFile, dialog.FileName, true); MessageBox.Show(this, "备份已导出。", "桌面管家", MessageBoxButton.OK, MessageBoxImage.Information); }
        catch (Exception ex) { MessageBox.Show(this, $"导出失败：{ex.Message}", "桌面管家", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void ChooseWallpaper()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择桌面壁纸",
            Filter = "图片文件|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(dialog.FileName, UriKind.Absolute);
            image.EndInit();
            _state.WallpaperPath = dialog.FileName;
            _state.WallpaperTitle = "";
            _state.WallpaperAuthor = "";
            _state.WallpaperLicense = "";
            _state.WallpaperSourceUrl = "";
            UpdateWallpaperDescription();
            _save();
            _wallpaperChanged(dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"无法读取这张图片：{ex.Message}", "设置壁纸", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenWallpaperLibrary()
    {
        var library = new WallpaperLibraryWindow { Owner = this };
        if (library.ShowDialog() != true || library.SelectedWallpaper is not { } wallpaper || library.SelectedWallpaperPath is not { } path) return;
        _state.WallpaperPath = path;
        _state.WallpaperTitle = wallpaper.Title;
        _state.WallpaperAuthor = wallpaper.Author;
        _state.WallpaperLicense = wallpaper.License;
        _state.WallpaperSourceUrl = wallpaper.DescriptionUrl;
        UpdateWallpaperDescription();
        _save();
        _wallpaperChanged(path);
    }

    private void UpdateWallpaperDescription()
    {
        WallpaperPathText.Text = string.IsNullOrWhiteSpace(_state.WallpaperPath)
            ? "使用 Windows 当前壁纸"
            : string.IsNullOrWhiteSpace(_state.WallpaperTitle)
                ? _state.WallpaperPath
                : $"{_state.WallpaperTitle} · {_state.WallpaperLicense}";
        WallpaperPathText.ToolTip = string.IsNullOrWhiteSpace(_state.WallpaperAuthor)
            ? _state.WallpaperPath
            : $"作者：{_state.WallpaperAuthor}\n许可：{_state.WallpaperLicense}\n来源：{_state.WallpaperSourceUrl}";
    }

    private void Exit_Click(object sender, RoutedEventArgs e) { Close(); _exit(); }
    private void Done_Click(object sender, RoutedEventArgs e)
    {
        _state.DesktopIconSize = (int)IconSizeSlider.Value;
        _save();
        Close();
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void ZoomWindow_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ZoomWindow_Click(sender, e);
            return;
        }
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
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

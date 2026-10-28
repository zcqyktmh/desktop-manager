using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Ellipse = System.Windows.Shapes.Ellipse;
using Microsoft.VisualBasic.FileIO;
using Line = System.Windows.Shapes.Line;
using DesktopManager.Models;
using DesktopManager.Services;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace DesktopManager;

public partial class MainWindow : Window
{
    private sealed record SpacingSnap(double Value, Border First, Border? Second, double Gap, bool Horizontal, bool AfterFirst);
    private enum ResizeAnchor { TopLeft, TopRight, BottomLeft, BottomRight }
    private const string DesktopItemDragFormat = "DesktopManager.DesktopItemPath";
    private const string BookmarkDragFormat = "DesktopManager.BookmarkId";
    private static readonly HttpClient FaviconHttpClient = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static readonly ConcurrentDictionary<string, Task<BitmapSource?>> FaviconTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly AppDataStore _store = new();
    private readonly DesktopItemDiscovery _desktopItems = new();
    private readonly StartupService _startup = new();
    private readonly SystemMetricsService _systemMetrics = new();
    private readonly ShellDesktopHost _shellHost = new();
    private readonly TaskbarVisibilityService _taskbarVisibility = new();
    private readonly AppState _state;
    private readonly Forms.NotifyIcon _trayIcon;
    private bool _trayDisposed;
    private bool _allowExit;
    private bool _wallpaperRotationRunning;
    private System.Windows.Threading.DispatcherTimer? _wallpaperRotationTimer;
    private System.Windows.Threading.DispatcherTimer? _systemInfoTimer;
    private System.Windows.Threading.DispatcherTimer? _weatherTimer;
    private System.Windows.Threading.DispatcherTimer? _desktopClockTimer;
    private TextBlock? _desktopClockText;
    private TextBlock? _weatherCityValue;
    private TextBlock? _weatherSymbolValue;
    private TextBlock? _weatherTemperatureValue;
    private TextBlock? _weatherDescriptionValue;
    private TextBlock? _weatherDetailsValue;
    private TextBlock? _weatherUpdatedValue;
    private bool _weatherRefreshRunning;
    private readonly Dictionary<string, TextBlock> _systemInfoValues = new(StringComparer.Ordinal);
    private bool _systemMetricsRefreshRunning;
    private int _groupZOrder;
    private string? _activeGroup;
    public bool DesktopLayerReady { get; }

    public MainWindow(bool startInTray = false)
    {
        InitializeComponent();
        RootSurface.PreviewKeyDown += OnRootPreviewKeyDown;
        _state = _store.Load();
        _taskbarVisibility.EnsureVisible();
        _state.StartWithWindows = _startup.IsEnabled();
        ThemeService.Apply(_state.Theme);

        var workArea = SystemParameters.WorkArea;
        Width = Math.Max(800, workArea.Width);
        Height = Math.Max(600, workArea.Height);
        Left = workArea.Left;
        Top = workArea.Top;

        var menu = CreateDesktopMenu();
        RootSurface.ContextMenu = menu;
        RenderDesktop();
        SaveState();
        DesktopSurface.ContextMenu = menu;
        RootSurface.Background = CreateDesktopWallpaperBrush(_state.WallpaperPath);
        DesktopLayerReady = _shellHost.Attach(this);
        if (!DesktopLayerReady)
        {
            MessageBox.Show("无法创建 Windows 桌面层。Windows 桌面图标保持原样，桌面管家已退出。", "桌面管家", MessageBoxButton.OK, MessageBoxImage.Warning);
            _trayIcon = new Forms.NotifyIcon();
            return;
        }

        var trayMenu = new Forms.ContextMenuStrip();
        trayMenu.Items.Add("显示桌面层", null, (_, _) => ShowDesktopLayer());
        trayMenu.Items.Add("管理书签", null, (_, _) => ShowBookmarkManager());
        trayMenu.Items.Add("桌面分组", null, (_, _) => RefreshDesktop());
        trayMenu.Items.Add("设置", null, (_, _) => ShowSettings());
        trayMenu.Items.Add(new Forms.ToolStripSeparator());
        trayMenu.Items.Add("退出并恢复 Windows 桌面", null, (_, _) => ExitApplication());
        _trayIcon = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "桌面管家", Visible = true, ContextMenuStrip = trayMenu };
        _trayIcon.DoubleClick += (_, _) => ShowDesktopLayer();
        Closing += (_, e) =>
        {
            if (_allowExit) return;
            e.Cancel = true;
            if (_state.CloseToTray) _shellHost.HideSurface();
            else ExitApplication();
        };
        ConfigureAutoWallpaperTimer();
        ConfigureSystemInfoTimer();
        ConfigureWeatherTimer();
        _desktopClockTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _desktopClockTimer.Tick += (_, _) => UpdateDesktopClock();
        _desktopClockTimer.Start();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => _taskbarVisibility.EnsureVisible();
    }

    private ContextMenu CreateDesktopMenu()
    {
        var menu = CreateMacContextMenu();
        menu.Items.Add(MenuItem("添加网址", (_, _) => AddBookmark()));
        menu.Items.Add(MenuItem("管理首页书签…", (_, _) => ShowBookmarkManager()));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("新建桌面分组", (_, _) => CreateGroup()));
        menu.Items.Add(MenuItem("按文件类别自动分组", (_, _) => AutoGroupByFileCategory()));
        menu.Items.Add(MenuItem("刷新桌面项目", (_, _) => RefreshDesktop()));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("设置…", (_, _) => ShowSettings()));
        menu.Items.Add(MenuItem("退出并恢复 Windows 桌面", (_, _) => ExitApplication()));
        return menu;
    }

    private static MenuItem MenuItem(string label, RoutedEventHandler action)
    {
        var item = new MenuItem { Header = label };
        item.Click += action;
        return item;
    }

    private ContextMenu CreateMacContextMenu()
    {
        var menu = new ContextMenu
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HasDropShadow = false,
            SnapsToDevicePixels = true,
            Template = CreateMacContextMenuTemplate()
        };
        menu.Resources[typeof(MenuItem)] = CreateMacMenuItemStyle();
        menu.Resources[typeof(Separator)] = CreateMacMenuSeparatorStyle();
        return menu;
    }

    private ControlTemplate CreateMacContextMenuTemplate()
    {
        var template = new ControlTemplate(typeof(ContextMenu));
        var chrome = new FrameworkElementFactory(typeof(Border));
        chrome.Name = "Chrome";
        chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));
        chrome.SetValue(Border.BackgroundProperty, IsDark()
            ? new SolidColorBrush(Color.FromArgb(240, 40, 42, 47))
            : new SolidColorBrush(Color.FromArgb(246, 250, 251, 253)));
        chrome.SetValue(Border.BorderBrushProperty, IsDark()
            ? new SolidColorBrush(Color.FromArgb(75, 255, 255, 255))
            : new SolidColorBrush(Color.FromArgb(190, 255, 255, 255)));
        chrome.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        chrome.SetValue(Border.PaddingProperty, new Thickness(5));
        chrome.SetValue(Border.EffectProperty, new DropShadowEffect { BlurRadius = 20, ShadowDepth = 5, Direction = 270, Opacity = .22, Color = Colors.Black });
        var items = new FrameworkElementFactory(typeof(ItemsPresenter));
        chrome.AppendChild(items);
        template.VisualTree = chrome;
        return template;
    }

    private Style CreateMacMenuItemStyle()
    {
        var style = new Style(typeof(MenuItem));
        style.Setters.Add(new Setter(Control.ForegroundProperty, PrimaryBrush()));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 12d));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(11, 7, 12, 7)));
        style.Setters.Add(new Setter(Control.MarginProperty, new Thickness(1, 1, 1, 1)));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.TemplateProperty, CreateMacMenuItemTemplate()));
        return style;
    }

    private ControlTemplate CreateMacMenuItemTemplate()
    {
        var template = new ControlTemplate(typeof(MenuItem));
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "ItemChrome";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetValue(Border.PaddingProperty, new Thickness(11, 7, 12, 7));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.ContentSourceProperty, "Header");
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        var hover = new Trigger { Property = System.Windows.Controls.MenuItem.IsHighlightedProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, IsDark()
            ? new SolidColorBrush(Color.FromArgb(55, 255, 255, 255))
            : new SolidColorBrush(Color.FromArgb(225, 229, 235, 243)), "ItemChrome"));
        template.Triggers.Add(hover);
        var disabled = new Trigger { Property = System.Windows.Controls.MenuItem.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(Control.ForegroundProperty, SecondaryBrush()));
        template.Triggers.Add(disabled);
        return template;
    }

    private static Style CreateMacMenuSeparatorStyle()
    {
        var style = new Style(typeof(Separator));
        style.Setters.Add(new Setter(FrameworkElement.HeightProperty, 1d));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(9, 4, 9, 4)));
        style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromArgb(55, 110, 118, 130))));
        return style;
    }

    private void RenderDesktop()
    {
        DesktopSurface.Children.Clear();
        _systemInfoValues.Clear();
        _weatherCityValue = _weatherSymbolValue = _weatherTemperatureValue = _weatherDescriptionValue = _weatherDetailsValue = _weatherUpdatedValue = null;
        var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 18), LastChildFill = false };
        var status = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _desktopClockText = new TextBlock { FontSize = 11, Foreground = PrimaryBrush(), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        UpdateDesktopClock();
        status.Children.Add(_desktopClockText);
        Popup? notificationPopup = null;
        var notificationButton = OverlayButton("◉", () => { if (notificationPopup is not null) notificationPopup.IsOpen = !notificationPopup.IsOpen; }, "通知中心");
        status.Children.Add(notificationButton);
        notificationPopup = new Popup
        {
            PlacementTarget = notificationButton,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border
            {
                Width = 300,
                Margin = new Thickness(0, 8, 0, 0),
                Padding = new Thickness(18),
                CornerRadius = new CornerRadius(18),
                Background = new SolidColorBrush(Color.FromArgb(238, 246, 248, 252)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 5, Opacity = .18 },
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = "通知中心", FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = PrimaryBrush() },
                        new TextBlock { Text = DateTime.Now.ToString("yyyy年M月d日 dddd"), FontSize = 11, Foreground = SecondaryBrush(), Margin = new Thickness(0, 5, 0, 14) },
                        new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(55, 110, 118, 130)) },
                        new TextBlock { Text = "暂时没有新通知", FontSize = 12, Foreground = SecondaryBrush(), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 10) }
                    }
                }
            }
        };
        status.Children.Add(notificationPopup);
        DockPanel.SetDock(status, Dock.Right);
        toolbar.Children.Add(status);
        DesktopSurface.Children.Add(toolbar);

        var items = _desktopItems.GetItems(_state);
        var groups = _state.DesktopGroups.Concat(["常用网址", "未分组"]).Distinct(StringComparer.CurrentCultureIgnoreCase).ToArray();
        var groupCanvas = new Canvas
        {
            Width = Math.Max(700, SystemParameters.WorkArea.Width - 68),
            Height = GetDesktopCanvasHeight(),
            ClipToBounds = true
        };
        ScaleLayoutsForCanvas(groupCanvas.Width, groupCanvas.Height);
        var bookmarks = _state.Bookmarks.Where(bookmark => bookmark.ShowOnHome).ToArray();
        var columns = Math.Max(1, (int)((groupCanvas.Width + 15) / 365));
        for (var index = 0; index < groups.Length; index++)
        {
            var group = groups[index];
            var groupItems = items.Where(item => string.Equals(item.Group, group, StringComparison.CurrentCultureIgnoreCase)).ToArray();
            var groupBookmarks = bookmarks.Where(bookmark => string.Equals(bookmark.Group, group, StringComparison.CurrentCultureIgnoreCase)).ToArray();
            if (group == "未分组" && groupItems.Length == 0 && groupBookmarks.Length == 0) continue;
            if (group == "常用网址" && groupItems.Length == 0 && groupBookmarks.Length == 0) continue;
            var layout = group == "常用网址"
                ? _state.BookmarkCardLayout
                : GetGroupLayout(group, index, columns, groupItems.Length, groupCanvas.Height);
            if (group == "常用网址")
            {
                layout = _state.BookmarkCardLayout;
                layout.Width = Math.Clamp(layout.Width, 260, Math.Max(260, groupCanvas.Width - 20));
                layout.Height = Math.Clamp(layout.Height, 150, groupCanvas.Height);
                layout.Left = Math.Clamp(layout.Left, 0, Math.Max(0, groupCanvas.Width - layout.Width));
                layout.Top = Math.Clamp(layout.Top, 0, Math.Max(0, groupCanvas.Height - layout.Height));
            }
            var panel = CreateGroupPanel(group, groupItems, groups, layout, groupCanvas, groupBookmarks, isBookmarkCard: group == "常用网址");
            Canvas.SetLeft(panel, layout.Left);
            Canvas.SetTop(panel, layout.Top);
            groupCanvas.Children.Add(panel);
        }
        if (items.Count == 0)
        {
            groupCanvas.Children.Add(new TextBlock { Text = "桌面上还没有文件、文件夹或快捷方式。", FontSize = 12, Foreground = SecondaryBrush(), Margin = new Thickness(6, 5, 6, 14) });
        }
        if (_state.SystemInfoCardEnabled)
        {
            var infoLayout = _state.SystemInfoCardLayout;
            infoLayout.Width = Math.Clamp(infoLayout.Width, 260, Math.Max(260, groupCanvas.Width - 12));
            infoLayout.Height = Math.Clamp(infoLayout.Height, 150, Math.Max(150, groupCanvas.Height));
            if (infoLayout.Left < 0 || infoLayout.Top < 0)
            {
                infoLayout.Left = Math.Max(0, groupCanvas.Width - infoLayout.Width - 20);
                infoLayout.Top = 20;
            }
            infoLayout.Left = Math.Clamp(infoLayout.Left, 0, Math.Max(0, groupCanvas.Width - infoLayout.Width));
            infoLayout.Top = Math.Clamp(infoLayout.Top, 0, Math.Max(0, groupCanvas.Height - infoLayout.Height));
            var infoCard = CreateSystemInfoCard(groupCanvas, infoLayout);
            Canvas.SetLeft(infoCard, infoLayout.Left);
            Canvas.SetTop(infoCard, infoLayout.Top);
            Panel.SetZIndex(infoCard, ++_groupZOrder);
            groupCanvas.Children.Add(infoCard);
        }
        if (_state.WeatherCardEnabled)
        {
            var weatherLayout = _state.WeatherCardLayout;
            weatherLayout.Width = Math.Clamp(weatherLayout.Width, 260, Math.Max(260, groupCanvas.Width - 12));
            weatherLayout.Height = Math.Clamp(weatherLayout.Height, 175, Math.Max(175, groupCanvas.Height));
            if (weatherLayout.Left < 0 || weatherLayout.Top < 0)
            {
                weatherLayout.Left = Math.Max(0, groupCanvas.Width - weatherLayout.Width - (_state.SystemInfoCardEnabled ? _state.SystemInfoCardLayout.Width + 40 : 20));
                weatherLayout.Top = _state.SystemInfoCardEnabled ? Math.Max(0, _state.SystemInfoCardLayout.Top) : 20;
            }
            weatherLayout.Left = Math.Clamp(weatherLayout.Left, 0, Math.Max(0, groupCanvas.Width - weatherLayout.Width));
            weatherLayout.Top = Math.Clamp(weatherLayout.Top, 0, Math.Max(0, groupCanvas.Height - weatherLayout.Height));
            var weatherCard = CreateWeatherCard(groupCanvas, weatherLayout);
            Canvas.SetLeft(weatherCard, weatherLayout.Left);
            Canvas.SetTop(weatherCard, weatherLayout.Top);
            Panel.SetZIndex(weatherCard, ++_groupZOrder);
            groupCanvas.Children.Add(weatherCard);
        }
        ApplyStoredCardZOrder(groupCanvas);
        DesktopSurface.Children.Add(groupCanvas);
    }

    private double GetDesktopCanvasHeight()
    {
        const double desktopTopMargin = 24;
        const double desktopBottomMargin = 28;
        const double toolbarAndGap = 54;
        return Math.Max(240, SystemParameters.WorkArea.Height - desktopTopMargin - desktopBottomMargin - toolbarAndGap);
    }

    private void ScaleLayoutsForCanvas(double width, double height)
    {
        var previousWidth = _state.LayoutCanvasWidth;
        var previousHeight = _state.LayoutCanvasHeight;
        if (previousWidth > 0 && previousHeight > 0)
        {
            var scaleX = Math.Clamp(width / previousWidth, .5, 2);
            var scaleY = Math.Clamp(height / previousHeight, .5, 2);
            if (Math.Abs(scaleX - 1) > .02 || Math.Abs(scaleY - 1) > .02)
            {
                foreach (var layout in _state.GroupLayouts.Values
                    .Concat([_state.BookmarkCardLayout, _state.SystemInfoCardLayout, _state.WeatherCardLayout]))
                {
                    layout.Left *= scaleX;
                    layout.Top *= scaleY;
                    layout.Width *= scaleX;
                    layout.Height *= scaleY;
                }
            }
        }

        _state.LayoutCanvasWidth = width;
        _state.LayoutCanvasHeight = height;
    }

    private void UpdateDesktopClock()
    {
        if (_desktopClockText is null) return;
        _desktopClockText.Text = DateTime.Now.ToString("M月d日 ddd  HH:mm");
    }

    private DesktopGroupLayout GetGroupLayout(string name, int index, int columns, int itemCount, double canvasHeight)
    {
        if (!_state.GroupLayouts.TryGetValue(name, out var layout))
        {
            var tileWidth = GetDesktopTileWidth();
            var tileColumns = Math.Max(1, (int)((350 - 32) / tileWidth));
            var rows = Math.Max(1, (int)Math.Ceiling(itemCount / (double)tileColumns));
            layout = new DesktopGroupLayout
            {
                Left = index % columns * 365,
                Top = index / columns * 205,
                Width = 350,
                Height = Math.Min(canvasHeight, itemCount == 0 ? 190 : Math.Max(190, GetGroupRequiredHeight(rows)))
            };
            _state.GroupLayouts[name] = layout;
        }
        layout.Width = Math.Clamp(layout.Width, 260, Math.Max(260, SystemParameters.WorkArea.Width - 90));
        layout.Height = Math.Clamp(layout.Height, 130, canvasHeight);
        layout.Left = Math.Clamp(layout.Left, 0, Math.Max(0, SystemParameters.WorkArea.Width - 68 - layout.Width));
        layout.Top = Math.Clamp(layout.Top, 0, Math.Max(0, canvasHeight - layout.Height));
        return layout;
    }

    private Border CreateGroupPanel(string name, DesktopItem[] items, string[] allGroups, DesktopGroupLayout groupLayout, Canvas groupCanvas, Bookmark[]? bookmarks = null, bool isBookmarkCard = false)
    {
        bookmarks ??= [];
        var cardItems = items.Length + bookmarks.Length;
        var panel = new Border { Tag = isBookmarkCard ? "@bookmarks" : $"group:{name}", Width = groupLayout.Width, Height = groupLayout.Height, Margin = new Thickness(0), Padding = new Thickness(16), CornerRadius = new CornerRadius(21), Background = PanelBrush(), BorderBrush = PanelBorderBrush(), BorderThickness = new Thickness(.6), Effect = CardShadow(), AllowDrop = true, Focusable = true, FocusVisualStyle = null };
        panel.PreviewMouseLeftButtonDown += (_, _) => { _activeGroup = name; Keyboard.Focus(panel); };
        panel.DragOver += (_, e) =>
        {
            var isFileDrop = e.Data.GetDataPresent(DataFormats.FileDrop);
            var isDesktopItem = e.Data.GetDataPresent(DesktopItemDragFormat);
            var isBookmark = e.Data.GetDataPresent(BookmarkDragFormat);
            e.Effects = isDesktopItem || isBookmark ? DragDropEffects.Move : isFileDrop ? DragDropEffects.Copy : DragDropEffects.None;
            panel.BorderBrush = e.Effects == DragDropEffects.None ? PanelBorderBrush() : DropTargetBrush();
            e.Handled = true;
        };
        panel.DragLeave += (_, _) => panel.BorderBrush = PanelBorderBrush();
        panel.Drop += (_, e) =>
        {
            _activeGroup = name;
            if (e.Data.GetDataPresent(DesktopItemDragFormat) && e.Data.GetData(DesktopItemDragFormat) is string path)
            {
                var draggedItem = _desktopItems.GetItems(_state).FirstOrDefault(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
                if (draggedItem is not null)
                {
                    var targetFolder = e.OriginalSource is DependencyObject source ? FindDesktopFolderTile(source) : null;
                    var targetFolderItem = targetFolder?.Tag as DesktopItem;
                    if (targetFolderItem is not null && !string.Equals(targetFolderItem.Path, draggedItem.Path, StringComparison.OrdinalIgnoreCase))
                        MoveIntoDesktopFolder(draggedItem, targetFolderItem);
                    else
                        MoveDesktopItem(draggedItem, name);
                }
            }
            else if (e.Data.GetDataPresent(BookmarkDragFormat) && e.Data.GetData(BookmarkDragFormat) is string bookmarkId &&
                     Guid.TryParse(bookmarkId, out var parsedId) && _state.Bookmarks.FirstOrDefault(bookmark => bookmark.Id == parsedId) is { } bookmark)
            {
                bookmark.Group = name;
                SaveAndRender();
            }
            else
            {
                PasteIntoGroup(e.Data, name);
            }
            panel.BorderBrush = PanelBorderBrush();
            e.Handled = true;
        };
        panel.ContextMenu = CreateMacContextMenu();
        AddCardLayerActions(panel.ContextMenu, (string)panel.Tag);
        panel.ContextMenu.Items.Add(new Separator());
        if (!isBookmarkCard)
            panel.ContextMenu.Items.Add(MenuItem("粘贴到此分组", (_, _) => PasteIntoGroup(Clipboard.GetDataObject(), name)));
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new DockPanel { Margin = new Thickness(18, 0, 18, 7), LastChildFill = false, Background = Brushes.Transparent };
        var title = new TextBlock { Text = "⠿  " + name, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = PrimaryBrush(), VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.SizeAll };
        header.Children.Add(title);
        var count = new TextBlock { Text = cardItems.ToString(), FontSize = 10, Foreground = SecondaryBrush(), Margin = new Thickness(7, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(count);
        var more = new Button { Content = "···", Width = 30, Height = 28, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = SecondaryBrush(), Padding = new Thickness(0), Cursor = Cursors.Hand, ToolTip = "分组操作", Template = RoundedButtonTemplate(14) };
        more.ContextMenu = CreateMacContextMenu();
        more.ContextMenu.Items.Add(MenuItem("添加书签…", (_, _) => AddBookmark(name)));
        more.ContextMenu.Items.Add(MenuItem("引入浏览器书签…", (_, _) => ImportBrowserBookmarks(name)));
        more.ContextMenu.Items.Add(new Separator());
        var displayMenu = new MenuItem { Header = "显示方式" };
        var isListMode = _state.GroupDisplayModes.TryGetValue(name, out var displayMode) && displayMode == "List";
        var iconMode = new MenuItem { Header = "图标模式", IsCheckable = true, IsChecked = !isListMode };
        iconMode.Click += (_, _) => SetGroupDisplayMode(name, "Icons");
        var listMode = new MenuItem { Header = "列表模式", IsCheckable = true, IsChecked = isListMode };
        listMode.Click += (_, _) => SetGroupDisplayMode(name, "List");
        displayMenu.Items.Add(iconMode);
        displayMenu.Items.Add(listMode);
        more.ContextMenu.Items.Add(displayMenu);
        if (isBookmarkCard)
        {
            more.ContextMenu.Items.Add(new Separator());
            more.ContextMenu.Items.Add(MenuItem("删除卡片", (_, _) => DeleteBookmarkCard()));
        }
        else if (name != "未分组")
        {
            more.ContextMenu.Items.Add(new Separator());
            more.ContextMenu.Items.Add(MenuItem("重命名分组", (_, _) => RenameGroup(name)));
            more.ContextMenu.Items.Add(MenuItem("删除分组", (_, _) => DeleteGroup(name)));
        }
        more.Click += (_, _) => more.ContextMenu.IsOpen = true;
        DockPanel.SetDock(more, Dock.Right);
        header.Children.Add(more);
        layout.Children.Add(header);
        Grid.SetRow(header, 0);
        EnableGroupDragging(title, panel, groupCanvas, groupLayout);

        UIElement body;
        if (cardItems == 0)
        {
            body = new TextBlock { Text = "右键桌面项目可将它移入此分组。", FontSize = 10, Foreground = SecondaryBrush(), Margin = new Thickness(3, 6, 3, 8) };
        }
        else
        {
            var wrap = new WrapPanel();
            var listModeEnabled = _state.GroupDisplayModes.TryGetValue(name, out var savedMode) && savedMode == "List";
            var rowWidth = Math.Max(180, groupLayout.Width - 84);
            foreach (var bookmark in bookmarks) wrap.Children.Add(CreateBookmarkTile(bookmark, allGroups, listModeEnabled, rowWidth));
            foreach (var item in items) wrap.Children.Add(CreateDesktopTile(item, allGroups, listModeEnabled, rowWidth));
            var scrollViewer = new ScrollViewer
            {
                Margin = new Thickness(16, 0, 16, 16),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                CanContentScroll = false,
                PanningMode = PanningMode.VerticalOnly,
                Focusable = false,
                FocusVisualStyle = null,
                Content = wrap,
                Background = Brushes.Transparent
            };
            scrollViewer.Resources[typeof(ScrollBar)] = (Style)FindResource("MacScrollBarStyle");
            ScrollBar? verticalScrollBar = null;
            var fadeScrollBar = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(850) };
            fadeScrollBar.Tick += (_, _) =>
            {
                fadeScrollBar.Stop();
                if (verticalScrollBar is not null && !scrollViewer.IsMouseOver) verticalScrollBar.Opacity = .12;
            };
            scrollViewer.Loaded += (_, _) =>
            {
                scrollViewer.ApplyTemplate();
                verticalScrollBar = scrollViewer.Template.FindName("PART_VerticalScrollBar", scrollViewer) as ScrollBar;
                if (verticalScrollBar is not null) verticalScrollBar.Opacity = .12;
            };
            scrollViewer.MouseEnter += (_, _) => { if (verticalScrollBar is not null) verticalScrollBar.Opacity = .72; };
            scrollViewer.MouseLeave += (_, _) =>
            {
                fadeScrollBar.Stop();
                if (verticalScrollBar is not null) verticalScrollBar.Opacity = .12;
            };
            scrollViewer.ScrollChanged += (_, _) =>
            {
                if (verticalScrollBar is not null) verticalScrollBar.Opacity = .92;
                fadeScrollBar.Stop();
                fadeScrollBar.Start();
            };
            scrollViewer.PreviewMouseWheel += (_, e) =>
            {
                scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - e.Delta * .65);
                e.Handled = true;
            };
            body = scrollViewer;
        }
        layout.Children.Add(body);
        Grid.SetRow(body, 1);
        var content = new Grid { ClipToBounds = true };
        content.Children.Add(layout);
        AddResizeGrip(content, panel, groupCanvas, groupLayout, ResizeAnchor.TopLeft);
        AddResizeGrip(content, panel, groupCanvas, groupLayout, ResizeAnchor.TopRight);
        AddResizeGrip(content, panel, groupCanvas, groupLayout, ResizeAnchor.BottomLeft);
        AddResizeGrip(content, panel, groupCanvas, groupLayout, ResizeAnchor.BottomRight);
        panel.Child = content;
        return panel;
    }

    private void AddResizeGrip(Grid content, Border panel, Canvas canvas, DesktopGroupLayout layout, ResizeAnchor anchor)
    {
        var grip = new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(7),
            Background = Brushes.Transparent,
            Cursor = anchor is ResizeAnchor.TopLeft or ResizeAnchor.BottomRight ? Cursors.SizeNWSE : Cursors.SizeNESW,
            ToolTip = "拖动此角调整卡片大小",
            HorizontalAlignment = anchor is ResizeAnchor.TopLeft or ResizeAnchor.BottomLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            VerticalAlignment = anchor is ResizeAnchor.TopLeft or ResizeAnchor.TopRight ? VerticalAlignment.Top : VerticalAlignment.Bottom,
            Margin = new Thickness(1),
            IsHitTestVisible = true
        };
        grip.MouseEnter += (_, _) => grip.Background = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255));
        grip.MouseLeave += (_, _) => grip.Background = Brushes.Transparent;
        Panel.SetZIndex(grip, 2);
        content.Children.Add(grip);
        EnableGroupResizing(grip, panel, canvas, layout, anchor);
    }

    private void EnableGroupDragging(UIElement handle, Border panel, Canvas canvas, DesktopGroupLayout savedLayout)
    {
        Point start = default;
        double startLeft = 0, startTop = 0;
        var dragging = false;
        handle.PreviewMouseLeftButtonDown += (_, e) =>
        {
            start = e.GetPosition(canvas);
            startLeft = Canvas.GetLeft(panel);
            startTop = Canvas.GetTop(panel);
            dragging = handle.CaptureMouse();
            if (dragging) Panel.SetZIndex(panel, ++_groupZOrder);
            e.Handled = dragging;
        };
        handle.PreviewMouseMove += (_, e) =>
        {
            if (!dragging || e.LeftButton != MouseButtonState.Pressed) return;
            var point = e.GetPosition(canvas);
            var left = Math.Clamp(startLeft + point.X - start.X, 0, Math.Max(0, canvas.ActualWidth - panel.Width));
            var top = Math.Clamp(startTop + point.Y - start.Y, 0, Math.Max(0, canvas.ActualHeight - panel.Height));
            var peers = canvas.Children.OfType<Border>().Where(other => !ReferenceEquals(other, panel)).ToArray();
            var xTargets = peers.SelectMany(other => new[]
            {
                Canvas.GetLeft(other), Canvas.GetLeft(other) + other.Width / 2, Canvas.GetLeft(other) + other.Width
            }).Concat(new[] { 0d, canvas.ActualWidth / 2, canvas.ActualWidth });
            var yTargets = peers.SelectMany(other => new[]
            {
                Canvas.GetTop(other), Canvas.GetTop(other) + other.Height / 2, Canvas.GetTop(other) + other.Height
            }).Concat(new[] { 0d, canvas.ActualHeight / 2, canvas.ActualHeight });
            var snapX = SnapAxis(left, panel.Width, xTargets);
            var snapY = SnapAxis(top, panel.Height, yTargets);
            var spacingX = FindEqualSpacing(left, panel.Width, top, panel.Height, peers, horizontal: true);
            var spacingY = FindEqualSpacing(top, panel.Height, left, panel.Width, peers, horizontal: false);
            if (spacingX is { } sx && Math.Abs(sx.Value - left) <= 18 &&
                Math.Abs(sx.Value - left) <= Math.Abs(snapX.Value - left) + 2)
                left = sx.Value;
            else spacingX = null;
            if (spacingY is { } sy && Math.Abs(sy.Value - top) <= 18 &&
                Math.Abs(sy.Value - top) <= Math.Abs(snapY.Value - top) + 2)
                top = sy.Value;
            else spacingY = null;
            top = Math.Clamp(spacingY?.Value ?? snapY.Value, 0, Math.Max(0, canvas.ActualHeight - panel.Height));
            left = Math.Clamp(spacingX?.Value ?? snapX.Value, 0, Math.Max(0, canvas.ActualWidth - panel.Width));
            Canvas.SetLeft(panel, left);
            Canvas.SetTop(panel, top);
            savedLayout.Left = left;
            savedLayout.Top = top;
            ShowAlignmentGuides(canvas,
                snapX.Guide is not null && Math.Abs(left - snapX.Value) < .1 ? snapX.Guide : null,
                snapY.Guide is not null && Math.Abs(top - snapY.Value) < .1 ? snapY.Guide : null);
            if (spacingX is { } horizontalSpacing) ShowSpacingIndicator(canvas, panel, horizontalSpacing);
            if (spacingY is { } verticalSpacing) ShowSpacingIndicator(canvas, panel, verticalSpacing);
        };
        handle.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (!dragging) return;
            dragging = false;
            handle.ReleaseMouseCapture();
            ClearAlignmentGuides(canvas);
            SaveState();
            e.Handled = true;
        };
        handle.LostMouseCapture += (_, _) =>
        {
            if (!dragging) return;
            dragging = false;
            savedLayout.Left = Canvas.GetLeft(panel);
            savedLayout.Top = Canvas.GetTop(panel);
            ClearAlignmentGuides(canvas);
            SaveState();
        };
    }

    private void EnableGroupResizing(UIElement grip, Border panel, Canvas canvas, DesktopGroupLayout savedLayout, ResizeAnchor anchor)
    {
        Point start = default;
        double startWidth = 0, startHeight = 0;
        double startLeft = 0, startTop = 0;
        var resizing = false;
        grip.PreviewMouseLeftButtonDown += (_, e) =>
        {
            start = e.GetPosition(canvas);
            startWidth = panel.Width;
            startHeight = panel.Height;
            startLeft = Canvas.GetLeft(panel);
            startTop = Canvas.GetTop(panel);
            resizing = grip.CaptureMouse();
            if (resizing) Panel.SetZIndex(panel, ++_groupZOrder);
            e.Handled = resizing;
        };
        grip.PreviewMouseMove += (_, e) =>
        {
            if (!resizing || e.LeftButton != MouseButtonState.Pressed) return;
            var point = e.GetPosition(canvas);
            var leftAnchored = anchor is ResizeAnchor.TopLeft or ResizeAnchor.BottomLeft;
            var topAnchored = anchor is ResizeAnchor.TopLeft or ResizeAnchor.TopRight;
            var rightEdge = startLeft + startWidth;
            var bottomEdge = startTop + startHeight;
            var delta = point - start;
            var left = startLeft;
            var top = startTop;
            var peers = canvas.Children.OfType<Border>().Where(other => !ReferenceEquals(other, panel)).ToArray();
            var maxWidth = leftAnchored ? rightEdge : canvas.ActualWidth - startLeft;
            var maxHeight = topAnchored ? bottomEdge : canvas.ActualHeight - startTop;
            var desiredWidth = Math.Clamp(startWidth + (leftAnchored ? -delta.X : delta.X), 260, Math.Max(260, maxWidth));
            var desiredHeight = Math.Clamp(startHeight + (topAnchored ? -delta.Y : delta.Y), 130, Math.Max(130, maxHeight));
            var resizeLeft = leftAnchored ? rightEdge - desiredWidth : startLeft;
            var resizeTop = topAnchored ? bottomEdge - desiredHeight : startTop;
            var widthTargets = peers.SelectMany(other => new[] { other.Width, Canvas.GetLeft(other) + other.Width - resizeLeft });
            var heightTargets = peers.SelectMany(other => new[] { other.Height, Canvas.GetTop(other) + other.Height - resizeTop });
            var snapWidth = SnapAxis(desiredWidth, 0, widthTargets);
            var snapHeight = SnapAxis(desiredHeight, 0, heightTargets);
            panel.Width = Math.Clamp(snapWidth.Value, 260, Math.Max(260, maxWidth));
            panel.Height = Math.Clamp(snapHeight.Value, 130, Math.Max(130, maxHeight));
            left = leftAnchored ? rightEdge - panel.Width : startLeft;
            top = topAnchored ? bottomEdge - panel.Height : startTop;
            Canvas.SetLeft(panel, left);
            Canvas.SetTop(panel, top);
            savedLayout.Width = panel.Width;
            savedLayout.Height = panel.Height;
            savedLayout.Left = left;
            savedLayout.Top = top;
            ToolTipService.SetToolTip(grip, $"{panel.Width:0} × {panel.Height:0}");
            ShowAlignmentGuides(canvas,
                snapWidth.Guide is not null && Math.Abs(panel.Width - snapWidth.Value) < .1 ? leftAnchored ? left : left + panel.Width : null,
                snapHeight.Guide is not null && Math.Abs(panel.Height - snapHeight.Value) < .1 ? topAnchored ? top : top + panel.Height : null);
        };
        grip.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (!resizing) return;
            resizing = false;
            grip.ReleaseMouseCapture();
            ClearAlignmentGuides(canvas);
            SaveState();
            e.Handled = true;
        };
        grip.LostMouseCapture += (_, _) =>
        {
            if (!resizing) return;
            resizing = false;
            savedLayout.Width = panel.Width;
            savedLayout.Height = panel.Height;
            ClearAlignmentGuides(canvas);
            SaveState();
        };
    }

    private static (double Value, double? Guide) SnapAxis(double desired, double size, IEnumerable<double> targets)
    {
        const double threshold = 12;
        var candidates = size <= 0 ? new[] { desired } : new[] { desired, desired + size / 2, desired + size };
        var bestDistance = threshold;
        var snapped = desired;
        double? guide = null;
        foreach (var target in targets)
        foreach (var candidate in candidates)
        {
            var distance = Math.Abs(target - candidate);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            snapped = desired + target - candidate;
            guide = target;
        }
        return (snapped, guide);
    }

    private static SpacingSnap? FindEqualSpacing(double desired, double size, double crossStart, double crossSize, Border[] peers, bool horizontal)
    {
        const double crossTolerance = 24;
        var crossCenter = crossStart + crossSize / 2;
        var aligned = peers.Where(peer =>
        {
            var peerStart = horizontal ? Canvas.GetTop(peer) : Canvas.GetLeft(peer);
            var peerSize = horizontal ? peer.Height : peer.Width;
            return Math.Abs(peerStart + peerSize / 2 - crossCenter) <= crossTolerance;
        }).OrderBy(peer => horizontal ? Canvas.GetLeft(peer) : Canvas.GetTop(peer)).ToArray();
        if (aligned.Length < 2) return null;

        var gaps = new List<double>();
        for (var index = 1; index < aligned.Length; index++)
        {
            var previous = aligned[index - 1];
            var current = aligned[index];
            var previousStart = horizontal ? Canvas.GetLeft(previous) : Canvas.GetTop(previous);
            var previousSize = horizontal ? previous.Width : previous.Height;
            var currentStart = horizontal ? Canvas.GetLeft(current) : Canvas.GetTop(current);
            var gap = currentStart - previousStart - previousSize;
            if (gap >= 4 && gap <= 120) gaps.Add(gap);
        }
        if (gaps.Count == 0) return null;
        var sortedGaps = gaps.Order().ToArray();
        var referenceGap = sortedGaps[sortedGaps.Length / 2];

        SpacingSnap? best = null;
        var bestDistance = 19d;
        void Consider(double value, Border first, Border? second, double displayGap, bool afterFirst)
        {
            var distance = Math.Abs(value - desired);
            if (distance >= bestDistance) return;
            bestDistance = distance;
            best = new SpacingSnap(value, first, second, displayGap, horizontal, afterFirst);
        }

        for (var index = 0; index < aligned.Length; index++)
        {
            var peer = aligned[index];
            var peerStart = horizontal ? Canvas.GetLeft(peer) : Canvas.GetTop(peer);
            var peerSize = horizontal ? peer.Width : peer.Height;
            Consider(peerStart + peerSize + referenceGap, peer, null, referenceGap, true);
            Consider(peerStart - referenceGap - size, peer, null, referenceGap, false);
        }

        for (var index = 1; index < aligned.Length; index++)
        {
            var previous = aligned[index - 1];
            var current = aligned[index];
            var previousStart = horizontal ? Canvas.GetLeft(previous) : Canvas.GetTop(previous);
            var previousSize = horizontal ? previous.Width : previous.Height;
            var currentStart = horizontal ? Canvas.GetLeft(current) : Canvas.GetTop(current);
            var available = currentStart - previousStart - previousSize;
            if (available < size + 8) continue;
            var halfGap = (available - size) / 2;
            Consider(previousStart + previousSize + halfGap, previous, current, halfGap, true);
        }

        return best;
    }

    private void ShowSpacingIndicator(Canvas canvas, Border panel, SpacingSnap spacing)
    {
        double x1, y1, x2, y2;
        if (spacing.Horizontal)
        {
            var panelTop = Canvas.GetTop(panel);
            var peerTop = Canvas.GetTop(spacing.First);
            var centerY = Math.Max(panelTop, peerTop) + Math.Min(panel.Height, spacing.First.Height) / 2;
            if (spacing.Second is { } next)
            {
                var start = Canvas.GetLeft(spacing.First) + spacing.First.Width;
                var middleStart = Canvas.GetLeft(panel);
                var end = Canvas.GetLeft(next);
                DrawSpacingLine(canvas, start, centerY, middleStart, centerY, spacing.Gap);
                DrawSpacingLine(canvas, middleStart + panel.Width, centerY, end, centerY, spacing.Gap);
                return;
            }
            var peerLeft = Canvas.GetLeft(spacing.First);
            if (spacing.AfterFirst)
            {
                x1 = peerLeft + spacing.First.Width; x2 = Canvas.GetLeft(panel);
            }
            else
            {
                x1 = Canvas.GetLeft(panel) + panel.Width; x2 = peerLeft;
            }
            y1 = y2 = centerY;
        }
        else
        {
            var panelLeft = Canvas.GetLeft(panel);
            var peerLeft = Canvas.GetLeft(spacing.First);
            var centerX = Math.Max(panelLeft, peerLeft) + Math.Min(panel.Width, spacing.First.Width) / 2;
            if (spacing.Second is { } next)
            {
                var start = Canvas.GetTop(spacing.First) + spacing.First.Height;
                var middleStart = Canvas.GetTop(panel);
                var end = Canvas.GetTop(next);
                DrawSpacingLine(canvas, centerX, start, centerX, middleStart, spacing.Gap);
                DrawSpacingLine(canvas, centerX, middleStart + panel.Height, centerX, end, spacing.Gap);
                return;
            }
            var peerTop = Canvas.GetTop(spacing.First);
            if (spacing.AfterFirst)
            {
                y1 = peerTop + spacing.First.Height; y2 = Canvas.GetTop(panel);
            }
            else
            {
                y1 = Canvas.GetTop(panel) + panel.Height; y2 = peerTop;
            }
            x1 = x2 = centerX;
        }
        DrawSpacingLine(canvas, x1, y1, x2, y2, spacing.Gap);
    }

    private static void DrawSpacingLine(Canvas canvas, double x1, double y1, double x2, double y2, double gap)
    {
        var brush = new SolidColorBrush(Color.FromArgb(220, 35, 126, 220));
        var line = new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = brush, StrokeThickness = 1.5, StrokeDashArray = [2, 2], IsHitTestVisible = false, Tag = "alignment-helper" };
        Panel.SetZIndex(line, int.MaxValue);
        canvas.Children.Add(line);
        var label = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(235, 247, 250, 255)),
            BorderBrush = brush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(5, 2, 5, 2),
            Child = new TextBlock { Text = $"{gap:0} px", FontSize = 9, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(35, 105, 190)) },
            IsHitTestVisible = false,
            Tag = "alignment-helper"
        };
        Canvas.SetLeft(label, (x1 + x2) / 2 - 22);
        Canvas.SetTop(label, (y1 + y2) / 2 - 12);
        Panel.SetZIndex(label, int.MaxValue);
        canvas.Children.Add(label);
    }

    private static void ShowAlignmentGuides(Canvas canvas, double? vertical, double? horizontal)
    {
        ClearAlignmentGuides(canvas);
        var guideBrush = new SolidColorBrush(Color.FromArgb(215, 35, 126, 220));
        guideBrush.Freeze();
        if (vertical is { } x)
        {
            var line = new Line { X1 = x, X2 = x, Y1 = 0, Y2 = canvas.ActualHeight, Stroke = guideBrush, StrokeThickness = 1, StrokeDashArray = [3, 3], IsHitTestVisible = false };
            Panel.SetZIndex(line, int.MaxValue);
            canvas.Children.Add(line);
        }
        if (horizontal is { } y)
        {
            var line = new Line { X1 = 0, X2 = canvas.ActualWidth, Y1 = y, Y2 = y, Stroke = guideBrush, StrokeThickness = 1, StrokeDashArray = [3, 3], IsHitTestVisible = false };
            Panel.SetZIndex(line, int.MaxValue);
            canvas.Children.Add(line);
        }
    }

    private MenuItem CreateCardLayersMenu()
    {
        var menu = new MenuItem { Header = "调整卡片前后顺序" };
        var canvas = DesktopSurface.Children.OfType<Canvas>().FirstOrDefault();
        var cards = canvas?.Children.OfType<Border>().Where(IsDesktopCard).ToArray() ?? [];
        if (cards.Length == 0)
        {
            menu.Items.Add(new MenuItem { Header = "当前没有卡片", IsEnabled = false });
            return menu;
        }
        foreach (var card in cards.OrderBy(card => Panel.GetZIndex(card)))
        {
            var key = (string)card.Tag;
            var cardMenu = new MenuItem { Header = GetCardDisplayName(key) };
            cardMenu.Items.Add(MenuItem("置于最前", (_, _) => SetCardLayer(key, toFront: true)));
            cardMenu.Items.Add(MenuItem("置于最后", (_, _) => SetCardLayer(key, toFront: false)));
            menu.Items.Add(cardMenu);
        }
        return menu;
    }

    private void AddCardLayerActions(ContextMenu? menu, string key)
    {
        if (menu is null) return;
        menu.Items.Add(MenuItem("置于最前", (_, _) => SetCardLayer(key, toFront: true)));
        menu.Items.Add(MenuItem("置于最后", (_, _) => SetCardLayer(key, toFront: false)));
    }

    private void SetCardLayer(string key, bool toFront)
    {
        var canvas = DesktopSurface.Children.OfType<Canvas>().FirstOrDefault();
        if (canvas is null) return;
        var keys = canvas.Children.OfType<Border>().Where(IsDesktopCard)
            .OrderBy(card => Panel.GetZIndex(card))
            .Select(card => (string)card.Tag)
            .ToList();
        if (!keys.Remove(key)) return;
        if (toFront) keys.Add(key);
        else keys.Insert(0, key);
        _state.CardZOrder = keys;
        ApplyStoredCardZOrder(canvas);
        SaveState();
    }

    private void ApplyStoredCardZOrder(Canvas canvas)
    {
        var cards = canvas.Children.OfType<Border>().Where(IsDesktopCard).ToArray();
        var order = _state.CardZOrder ?? [];
        var cardKeys = cards.Select(card => (string)card.Tag).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var keys = order.Where(cardKeys.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        keys.AddRange(cards.Select(card => (string)card.Tag).Where(key => !keys.Contains(key, StringComparer.OrdinalIgnoreCase)));
        for (var i = 0; i < keys.Count; i++)
        {
            var card = cards.FirstOrDefault(item => string.Equals((string)item.Tag, keys[i], StringComparison.OrdinalIgnoreCase));
            if (card is not null) Panel.SetZIndex(card, i + 1);
        }
        _groupZOrder = keys.Count + 1;
    }

    private static bool IsDesktopCard(Border border) => border.Tag is string tag &&
        (tag.StartsWith("group:", StringComparison.OrdinalIgnoreCase) || tag is "@bookmarks" or "@system-info" or "@weather");

    private static string GetCardDisplayName(string key) => key switch
    {
        "@bookmarks" => "常用网址",
        "@system-info" => "系统状态",
        "@weather" => "天气",
        _ when key.StartsWith("group:", StringComparison.OrdinalIgnoreCase) => key[6..],
        _ => key
    };


    private static void ClearAlignmentGuides(Canvas canvas)
    {
        foreach (var helper in canvas.Children.OfType<FrameworkElement>().Where(element => element.Tag as string == "alignment-helper" || element is Line).ToArray())
            canvas.Children.Remove(helper);
    }

    private Border CreateDesktopTile(DesktopItem item, string[] groups, bool listMode = false, double listWidth = 220)
    {
        var iconSize = Math.Clamp(_state.DesktopIconSize, 32, 80);
        var displayIconSize = listMode ? 30 : iconSize;
        var tileWidth = listMode ? listWidth : GetDesktopTileWidth();
        var tileHeight = listMode ? 46 : GetDesktopTileHeight();
        var tile = new Border { Tag = item, Width = tileWidth, Height = tileHeight, Margin = new Thickness(1), Padding = new Thickness(2), CornerRadius = new CornerRadius(12), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        Point dragStart = default;
        tile.PreviewMouseLeftButtonDown += (_, e) => dragStart = e.GetPosition(tile);
        tile.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            var current = e.GetPosition(tile);
            if (Math.Abs(current.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(current.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            var payload = new DataObject();
            payload.SetData(DesktopItemDragFormat, item.Path);
            payload.SetData(DataFormats.FileDrop, new[] { item.Path });
            DragDrop.DoDragDrop(tile, payload, DragDropEffects.Move);
        };
        if (Directory.Exists(item.Path))
        {
            tile.AllowDrop = true;
            tile.DragOver += (_, e) =>
            {
                var draggedPath = e.Data.GetDataPresent(DesktopItemDragFormat) ? e.Data.GetData(DesktopItemDragFormat) as string : null;
                var isSelf = string.Equals(draggedPath, item.Path, StringComparison.OrdinalIgnoreCase);
                var valid = !isSelf && (draggedPath is not null || e.Data.GetDataPresent(DataFormats.FileDrop));
                e.Effects = valid ? DragDropEffects.Move : DragDropEffects.None;
                tile.BorderBrush = valid ? DropTargetBrush() : Brushes.Transparent;
                tile.BorderThickness = valid ? new Thickness(1.5) : new Thickness(0);
                e.Handled = true;
            };
            tile.DragLeave += (_, _) => { tile.BorderBrush = Brushes.Transparent; tile.BorderThickness = new Thickness(0); };
            tile.Drop += (_, e) =>
            {
                try
                {
                    if (e.Data.GetDataPresent(DesktopItemDragFormat) && e.Data.GetData(DesktopItemDragFormat) is string draggedPath)
                    {
                        var sourceItem = _desktopItems.GetItems(_state).FirstOrDefault(candidate => string.Equals(candidate.Path, draggedPath, StringComparison.OrdinalIgnoreCase));
                        if (sourceItem is not null && !string.Equals(sourceItem.Path, item.Path, StringComparison.OrdinalIgnoreCase))
                            MoveIntoDesktopFolder(sourceItem, item);
                    }
                    else if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
                    {
                        foreach (var sourcePath in paths.Where(path => File.Exists(path) || Directory.Exists(path)))
                            MovePathIntoFolder(sourcePath, item.Path);
                        SaveAndRender();
                    }
                }
                finally
                {
                    tile.BorderBrush = Brushes.Transparent;
                    tile.BorderThickness = new Thickness(0);
                }
                e.Handled = true;
            };
        }
        var nativeIcon = DesktopItemIconService.GetIcon(item.Path);
        var imagePreview = TryLoadImagePreview(item.Path);
        var itemIcon = imagePreview is not null
            ? (UIElement)new Image { Source = imagePreview, Width = displayIconSize, Height = displayIconSize, Stretch = System.Windows.Media.Stretch.UniformToFill, SnapsToDevicePixels = true }
            : nativeIcon is null
                ? IconTile(item.Name, displayIconSize)
                : new Image { Source = nativeIcon, Width = displayIconSize, Height = displayIconSize, Stretch = System.Windows.Media.Stretch.Uniform, SnapsToDevicePixels = true };
        UIElement content;
        if (listMode)
        {
            var row = new Grid { Margin = new Thickness(5, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(itemIcon);
            var name = new TextBlock { Text = item.Name, FontSize = 11, Foreground = PrimaryBrush(), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 8, 0) };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            var kind = new TextBlock { Text = item.Kind, FontSize = 9, Foreground = SecondaryBrush(), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 3, 0) };
            Grid.SetColumn(kind, 2);
            row.Children.Add(kind);
            content = row;
        }
        else
        {
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(itemIcon);
            stack.Children.Add(new TextBlock { Text = item.Name, FontSize = 9.5, Foreground = PrimaryBrush(), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = tileWidth - 6 });
            content = stack;
        }
        tile.Child = content;
        tile.ToolTip = $"{item.Name}\n{item.Kind}\n{item.Path}";
        tile.MouseEnter += (_, _) => tile.Background = HoverBrush();
        tile.MouseLeave += (_, _) => tile.Background = Brushes.Transparent;
        tile.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) OpenPath(item.Path); };
        var menu = CreateMacContextMenu();
        menu.Items.Add(MenuItem("打开", (_, _) => OpenPath(item.Path)));
        if (InstalledApplicationUninstaller.IsApplicationShortcut(item))
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem("卸载软件…", (_, _) => InstalledApplicationUninstaller.Uninstall(item, this)));
        }
        var moveMenu = new MenuItem { Header = "移动到分组" };
        foreach (var group in groups)
        {
            var destination = group;
            var move = new MenuItem { Header = group, IsCheckable = true, IsChecked = string.Equals(group, item.Group, StringComparison.CurrentCultureIgnoreCase) };
            move.Click += (_, _) => MoveDesktopItem(item, destination);
            moveMenu.Items.Add(move);
        }
        menu.Items.Add(moveMenu);
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("粘贴到此分组", (_, _) => PasteIntoGroup(Clipboard.GetDataObject(), item.Group)));
        menu.Items.Add(MenuItem("从分组移除", (_, _) => MoveDesktopItem(item, "未分组")));
        menu.Items.Add(new Separator());
        if (item.IsTaskbarShortcut)
            menu.Items.Add(MenuItem("从桌面隐藏", (_, _) => HideTaskbarItem(item)));
        else
            menu.Items.Add(MenuItem("删除", (_, _) => DeleteDesktopItem(item)));
        tile.ContextMenu = menu;
        return tile;
    }

    private double GetDesktopTileWidth() => Math.Max(76, Math.Clamp(_state.DesktopIconSize, 32, 80) + 36);

    private double GetDesktopTileHeight() => Math.Clamp(_state.DesktopIconSize, 32, 80) + 40;

    private double GetGroupRequiredHeight(int rows) => 58 + rows * GetDesktopTileHeight() + 36;

    private void DeleteDesktopItem(DesktopItem item)
    {
        if (!File.Exists(item.Path) && !Directory.Exists(item.Path))
        {
            MessageBox.Show("这个项目已经不存在了。刷新桌面后会自动移除。", "删除桌面项目", MessageBoxButton.OK, MessageBoxImage.Information);
            RenderDesktop();
            return;
        }

        if (MessageBox.Show($"要将“{item.Name}”移到回收站吗？", "删除桌面项目", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        try
        {
            if (Directory.Exists(item.Path))
                FileSystem.DeleteDirectory(item.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            else
                FileSystem.DeleteFile(item.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);

            _state.DesktopAssignments.Remove(DesktopItemDiscovery.Normalize(item.Path));
            SaveAndRender();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法删除“{item.Name}”：{ex.Message}", "删除桌面项目", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void HideTaskbarItem(DesktopItem item)
    {
        var key = DesktopItemDiscovery.Normalize(item.Path);
        if (!_state.HiddenTaskbarItems.Contains(key, StringComparer.OrdinalIgnoreCase))
            _state.HiddenTaskbarItems.Add(key);
        SaveAndRender();
    }

    private void RestoreHiddenTaskbarItems()
    {
        _state.HiddenTaskbarItems.Clear();
        SaveAndRender();
    }

    private void OnRootPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || (Keyboard.Modifiers & ModifierKeys.Control) == 0 || string.IsNullOrWhiteSpace(_activeGroup)) return;
        if (PasteIntoGroup(Clipboard.GetDataObject(), _activeGroup)) e.Handled = true;
    }

    private bool PasteIntoGroup(IDataObject? data, string group)
    {
        if (data is null) return false;
        var paths = new List<string>();
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] dropped)
            paths.AddRange(dropped.Where(path => File.Exists(path) || Directory.Exists(path)));

        if (paths.Count == 0 && data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
        {
            var imagePath = SaveClipboardImage(bitmap);
            if (imagePath is not null) paths.Add(imagePath);
        }
        if (paths.Count == 0) return false;

        var added = 0;
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                _state.DesktopAssignments[DesktopItemDiscovery.Normalize(path)] = group;
                added++;
            }
            catch (Exception) { }
        }
        if (added == 0) return false;
        GrowGroupToFitItems(group);
        SaveAndRender();
        return true;
    }

    private void GrowGroupToFitItems(string group)
    {
        if (!_state.GroupLayouts.TryGetValue(group, out var layout)) return;
        var width = SystemParameters.WorkArea.Width - 68;
        var canvasHeight = GetDesktopCanvasHeight();
        var columns = Math.Max(1, (int)((layout.Width - 32) / GetDesktopTileWidth()));
        var count = _desktopItems.GetItems(_state).Count(item => string.Equals(item.Group, group, StringComparison.CurrentCultureIgnoreCase));
        var rows = Math.Max(1, (int)Math.Ceiling(count / (double)columns));
        var requiredHeight = GetGroupRequiredHeight(rows);
        layout.Height = Math.Min(canvasHeight, Math.Max(layout.Height, requiredHeight));
        layout.Top = Math.Clamp(layout.Top, 0, Math.Max(0, canvasHeight - layout.Height));
        layout.Left = Math.Clamp(layout.Left, 0, Math.Max(0, width - layout.Width));
    }

    private string? SaveClipboardImage(BitmapSource image)
    {
        try
        {
            var directory = Path.Combine(_store.DataDirectory, "ClipboardImages");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"剪贴图片-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = File.Create(path);
            encoder.Save(stream);
            return path;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存剪贴板图片失败：{ex.Message}", "桌面管家", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
    }

    private static BitmapSource? TryLoadImagePreview(string path)
    {
        var extension = Path.GetExtension(path);
        if (!new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp" }.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 96;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    private UIElement IconTile(string text, double size)
    {
        var palette = new[] { "#3978C6", "#33816D", "#8167A8", "#B77237", "#AA5558" };
        var color = (Color)ColorConverter.ConvertFromString(palette[Math.Abs(text.GetHashCode()) % palette.Length]);
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size * .29), Background = new SolidColorBrush(color),
            Child = new TextBlock { Text = string.IsNullOrWhiteSpace(text) ? "?" : text[..1].ToUpperInvariant(), FontSize = size * .42, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
    }

    private Button OverlayButton(string text, Action action, string? tooltip = null)
    {
        var button = new Button
        {
            Content = text, MinWidth = 32, Height = 34, Margin = new Thickness(5, 0, 0, 0),
            Padding = new Thickness(12, 2, 12, 2), FontSize = 11.5, FontWeight = FontWeights.Normal,
            Background = ButtonBrush(), BorderBrush = PanelBorderBrush(),
            BorderThickness = new Thickness(1), Foreground = PrimaryBrush(),
            Cursor = Cursors.Hand, ToolTip = tooltip, FocusVisualStyle = null
        };
        button.Template = RoundedButtonTemplate(18);
        button.Click += (_, _) => action();
        return button;
    }

    private void AddBookmark(string group = "常用网址")
    {
        var dialog = new BookmarkDialog { WindowStartupLocation = WindowStartupLocation.CenterScreen };
        if (dialog.ShowDialog() != true || dialog.Result is not { } bookmark) return;
        bookmark.Group = group;
        _state.Bookmarks.Add(bookmark);
        SaveAndRender();
    }

    private void ImportBrowserBookmarks(string group)
    {
        var sources = BrowserBookmarkImporter.DiscoverSources();
        if (sources.Count == 0)
        {
            MessageBox.Show(this, "没有发现 Chrome、Edge、Brave、Vivaldi 或 Opera 的本地书签。", "导入浏览器书签", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new BrowserBookmarkImportWindow(sources) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.ImportedBookmarks.Count == 0) return;
        var knownUrls = new HashSet<string>(_state.Bookmarks.Select(item => BrowserBookmarkImporter.NormalizeUrl(item.Url)), StringComparer.OrdinalIgnoreCase);
        var imported = 0;
        var duplicates = 0;
        foreach (var bookmark in dialog.ImportedBookmarks)
        {
            if (!knownUrls.Add(BrowserBookmarkImporter.NormalizeUrl(bookmark.Url)))
            {
                duplicates++;
                continue;
            }
            bookmark.Group = group;
            bookmark.ShowOnHome = true;
            _state.Bookmarks.Add(bookmark);
            imported++;
        }

        if (imported > 0) SaveAndRender();
        MessageBox.Show(this, $"已导入 {imported} 个书签，跳过 {duplicates} 个重复网址。", "导入完成", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private ControlTemplate RoundedButtonTemplate(double radius)
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Chrome";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        var hover = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, HoverBrush(), "Chrome"));
        template.Triggers.Add(hover);
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Border.BackgroundProperty, PressedBrush(), "Chrome"));
        template.Triggers.Add(pressed);
        return template;
    }

    private void ShowBookmarkManager()
    {
        var window = new BookmarkManagerWindow(_state, SaveState) { WindowStartupLocation = WindowStartupLocation.CenterScreen };
        window.ShowDialog();
        SaveAndRender();
    }

    private void ShowSettings()
    {
        var window = new SettingsWindow(_state, _startup, _store, SaveState, ExitApplication, path => RootSurface.Background = CreateDesktopWallpaperBrush(path)) { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        window.ShowDialog();
        ThemeService.Apply(_state.Theme);
        RootSurface.Background = CreateDesktopWallpaperBrush(_state.WallpaperPath);
        ConfigureAutoWallpaperTimer();
        RenderDesktop();
        ConfigureSystemInfoTimer();
        ConfigureWeatherTimer();
    }

    private Border CreateSystemInfoCard(Canvas canvas, DesktopGroupLayout savedLayout)
    {
        var panel = new Border
        {
            Tag = "@system-info",
            Width = savedLayout.Width,
            Height = savedLayout.Height,
            Padding = new Thickness(15),
            CornerRadius = new CornerRadius(21),
            Background = PanelBrush(),
            BorderBrush = PanelBorderBrush(),
            BorderThickness = new Thickness(.6),
            Effect = CardShadow(),
            Focusable = true,
            FocusVisualStyle = null
        };
        panel.ContextMenu = CreateMacContextMenu();
        AddCardLayerActions(panel.ContextMenu, "@system-info");
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new DockPanel { LastChildFill = false, Margin = new Thickness(3, 0, 3, 8) };
        var title = new TextBlock { Text = "⠿  系统状态", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = PrimaryBrush(), Cursor = Cursors.SizeAll, VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(title);
        var more = new Button { Content = "···", Width = 30, Height = 28, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = SecondaryBrush(), Padding = new Thickness(0), Cursor = Cursors.Hand, ToolTip = "信息卡片设置", Template = RoundedButtonTemplate(14) };
        more.Click += (_, _) => ShowSettings();
        DockPanel.SetDock(more, Dock.Right);
        header.Children.Add(more);
        EnableGroupDragging(title, panel, canvas, savedLayout);
        grid.Children.Add(header);

        var rows = new StackPanel { Margin = new Thickness(5, 0, 5, 2), VerticalAlignment = VerticalAlignment.Top };
        if (_state.SystemInfoShowCpu) AddSystemInfoRow(rows, "CPU 使用率", "cpu");
        if (_state.SystemInfoShowMemory) AddSystemInfoRow(rows, "内存占用", "memory");
        if (_state.SystemInfoShowCpuTemperature) AddSystemInfoRow(rows, "CPU 温度", "cpu-temp");
        if (_state.SystemInfoShowGpuTemperature) AddSystemInfoRow(rows, "GPU 温度", "gpu-temp");
        if (_state.SystemInfoShowSystemDrive) AddSystemInfoRow(rows, "系统盘占用", "system-drive");
        if (_systemInfoValues.Count == 0)
            rows.Children.Add(new TextBlock { Text = "请在设置中选择要显示的项目。", FontSize = 10, Foreground = SecondaryBrush(), Margin = new Thickness(3, 12, 3, 0), TextWrapping = TextWrapping.Wrap });
        var scroll = new ScrollViewer
        {
            Content = rows,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = Brushes.Transparent,
            Focusable = false
        };
        scroll.Resources[typeof(ScrollBar)] = (Style)FindResource("MacScrollBarStyle");
        grid.Children.Add(scroll);
        Grid.SetRow(scroll, 1);
        var content = new Grid { ClipToBounds = true };
        content.Children.Add(grid);
        AddResizeGrip(content, panel, canvas, savedLayout, ResizeAnchor.TopLeft);
        AddResizeGrip(content, panel, canvas, savedLayout, ResizeAnchor.TopRight);
        AddResizeGrip(content, panel, canvas, savedLayout, ResizeAnchor.BottomLeft);
        AddResizeGrip(content, panel, canvas, savedLayout, ResizeAnchor.BottomRight);
        panel.Child = content;
        return panel;
    }

    private Border CreateWeatherCard(Canvas canvas, DesktopGroupLayout savedLayout)
    {
        var panel = new Border
        {
            Tag = "@weather",
            Width = savedLayout.Width,
            Height = savedLayout.Height,
            Padding = new Thickness(15),
            CornerRadius = new CornerRadius(21),
            Background = PanelBrush(),
            BorderBrush = PanelBorderBrush(),
            BorderThickness = new Thickness(.6),
            Effect = CardShadow(),
            Focusable = true,
            FocusVisualStyle = null
        };
        panel.ContextMenu = CreateMacContextMenu();
        AddCardLayerActions(panel.ContextMenu, "@weather");
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new DockPanel { LastChildFill = false, Margin = new Thickness(3, 0, 3, 5) };
        var title = new TextBlock { Text = "⠿  天气", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = PrimaryBrush(), Cursor = Cursors.SizeAll, VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(title);
        var refresh = new Button { Content = "↻", Width = 30, Height = 28, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = SecondaryBrush(), Padding = new Thickness(0), Cursor = Cursors.Hand, ToolTip = "刷新天气", Template = RoundedButtonTemplate(14) };
        refresh.Click += async (_, _) => await RefreshWeatherAsync();
        DockPanel.SetDock(refresh, Dock.Right);
        header.Children.Add(refresh);
        EnableGroupDragging(title, panel, canvas, savedLayout);
        root.Children.Add(header);

        var body = new Grid { Margin = new Thickness(8, 5, 8, 0) };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _weatherCityValue = new TextBlock { Text = string.IsNullOrWhiteSpace(_state.WeatherCity) ? "设置城市" : _state.WeatherCity, FontSize = 11, FontWeight = FontWeights.Medium, Foreground = SecondaryBrush(), Margin = new Thickness(3, 0, 0, 2) };
        body.Children.Add(_weatherCityValue);

        var hero = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(82) });
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var weatherIcon = new Border
        {
            Width = 68, Height = 68, CornerRadius = new CornerRadius(24),
            Background = new SolidColorBrush(Color.FromArgb(38, 255, 190, 76)),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        _weatherSymbolValue = new TextBlock { Text = "☁", FontSize = 43, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        weatherIcon.Child = _weatherSymbolValue;
        hero.Children.Add(weatherIcon);
        var summary = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0) };
        _weatherTemperatureValue = new TextBlock { Text = "读取中…", FontSize = 39, FontWeight = FontWeights.Light, Foreground = PrimaryBrush(), LineHeight = 45 };
        _weatherDescriptionValue = new TextBlock { Text = "正在获取天气", FontSize = 11, Foreground = SecondaryBrush(), Margin = new Thickness(3, -1, 0, 0) };
        summary.Children.Add(_weatherTemperatureValue);
        summary.Children.Add(_weatherDescriptionValue);
        Grid.SetColumn(summary, 1);
        hero.Children.Add(summary);
        Grid.SetRow(hero, 1);
        body.Children.Add(hero);

        var footer = new StackPanel { Margin = new Thickness(2, 2, 2, 0) };
        _weatherDetailsValue = new TextBlock { Text = "体感 —°    湿度 —%    风速 —", FontSize = 9.5, Foreground = SecondaryBrush(), TextTrimming = TextTrimming.CharacterEllipsis };
        _weatherUpdatedValue = new TextBlock { Text = "每 30 分钟更新", FontSize = 8.5, Foreground = SecondaryBrush(), Opacity = .78, Margin = new Thickness(0, 4, 0, 0) };
        footer.Children.Add(_weatherDetailsValue);
        footer.Children.Add(_weatherUpdatedValue);
        Grid.SetRow(footer, 2);
        body.Children.Add(footer);
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var content = new Grid { ClipToBounds = true };
        content.Children.Add(root);
        AddResizeGrip(content, panel, canvas, savedLayout, ResizeAnchor.TopLeft);
        AddResizeGrip(content, panel, canvas, savedLayout, ResizeAnchor.TopRight);
        AddResizeGrip(content, panel, canvas, savedLayout, ResizeAnchor.BottomLeft);
        AddResizeGrip(content, panel, canvas, savedLayout, ResizeAnchor.BottomRight);
        panel.Child = content;
        return panel;
    }

    private void ConfigureWeatherTimer()
    {
        _weatherTimer?.Stop();
        _weatherTimer = null;
        if (!_state.WeatherCardEnabled || !DesktopLayerReady) return;
        _weatherTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
        _weatherTimer.Tick += async (_, _) => await RefreshWeatherAsync();
        _weatherTimer.Start();
        _ = RefreshWeatherAsync();
    }

    private async Task RefreshWeatherAsync()
    {
        if (_weatherRefreshRunning || !_state.WeatherCardEnabled) return;
        _weatherRefreshRunning = true;
        try
        {
            if (string.IsNullOrWhiteSpace(_state.WeatherCity))
            {
                if (_weatherTemperatureValue is not null) _weatherTemperatureValue.Text = "—";
                if (_weatherDescriptionValue is not null) _weatherDescriptionValue.Text = "请在设置中填写城市";
                if (_weatherUpdatedValue is not null) _weatherUpdatedValue.Text = "天气位置由你手动设置";
                return;
            }
            if (_weatherUpdatedValue is not null) _weatherUpdatedValue.Text = "正在更新…";
            var weather = await WeatherService.GetCurrentAsync(_state.WeatherCity);
            if (weather is null)
            {
                if (_weatherTemperatureValue is not null) _weatherTemperatureValue.Text = "—";
                if (_weatherDescriptionValue is not null) _weatherDescriptionValue.Text = "未找到该城市";
                if (_weatherUpdatedValue is not null) _weatherUpdatedValue.Text = "检查设置中的城市名称";
                return;
            }
            var (description, symbol) = WeatherService.Describe(weather.WeatherCode, weather.IsDay);
            if (_weatherCityValue is not null) _weatherCityValue.Text = weather.City;
            if (_weatherSymbolValue is not null) _weatherSymbolValue.Text = symbol;
            if (_weatherTemperatureValue is not null) _weatherTemperatureValue.Text = $"{weather.Temperature:0}°";
            if (_weatherDescriptionValue is not null) _weatherDescriptionValue.Text = description;
            if (_weatherDetailsValue is not null) _weatherDetailsValue.Text = $"体感 {weather.FeelsLike:0}°   湿度 {weather.RelativeHumidity}%   风速 {weather.WindSpeed:0.#} km/h";
            if (_weatherUpdatedValue is not null) _weatherUpdatedValue.Text = $"更新于 {weather.UpdatedAt:HH:mm} · Open-Meteo";
        }
        catch
        {
            if (_weatherTemperatureValue is not null) _weatherTemperatureValue.Text = "—";
            if (_weatherDescriptionValue is not null) _weatherDescriptionValue.Text = "天气暂时不可用";
            if (_weatherUpdatedValue is not null) _weatherUpdatedValue.Text = "请检查网络后重试";
        }
        finally { _weatherRefreshRunning = false; }
    }

    private void AddSystemInfoRow(Panel parent, string label, string key)
    {
        var row = new Grid { MinHeight = 36, Margin = new Thickness(2, 1, 2, 1) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = label, FontSize = 10.5, Foreground = PrimaryBrush(), VerticalAlignment = VerticalAlignment.Center });
        var value = new TextBlock { Text = "读取中…", FontSize = 10, Foreground = SecondaryBrush(), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 190 };
        Grid.SetColumn(value, 1);
        row.Children.Add(value);
        parent.Children.Add(row);
        _systemInfoValues[key] = value;
    }

    private void ConfigureSystemInfoTimer()
    {
        _systemInfoTimer?.Stop();
        _systemInfoTimer = null;
        if (!_state.SystemInfoCardEnabled || !DesktopLayerReady)
        {
            _systemMetrics.CloseSensors();
            return;
        }
        _systemInfoTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _systemInfoTimer.Tick += async (_, _) => await RefreshSystemInfoAsync();
        _systemInfoTimer.Start();
        _ = RefreshSystemInfoAsync();
    }

    private async Task RefreshSystemInfoAsync()
    {
        if (_systemMetricsRefreshRunning || !_state.SystemInfoCardEnabled) return;
        _systemMetricsRefreshRunning = true;
        try
        {
            var snapshot = await Task.Run(_systemMetrics.ReadSnapshot);
            if (_systemInfoValues.TryGetValue("cpu", out var cpu))
                cpu.Text = snapshot.CpuLoadPercent is { } load ? $"{load:0}%" : "采样中…";
            if (_systemInfoValues.TryGetValue("memory", out var memory))
            {
                var percent = snapshot.MemoryTotalBytes == 0 ? 0 : snapshot.MemoryUsedBytes * 100d / snapshot.MemoryTotalBytes;
                memory.Text = snapshot.MemoryTotalBytes == 0 ? "不可用" : $"{FormatBytes(snapshot.MemoryUsedBytes)} / {FormatBytes(snapshot.MemoryTotalBytes)} ({percent:0}%)";
            }
            if (_systemInfoValues.TryGetValue("cpu-temp", out var cpuTemp))
                cpuTemp.Text = snapshot.CpuTemperatureCelsius is { } ct ? $"{ct:0} °C" : "不可用";
            if (_systemInfoValues.TryGetValue("gpu-temp", out var gpuTemp))
                gpuTemp.Text = snapshot.GpuTemperatureCelsius is { } gt ? $"{gt:0} °C" : "不可用";
            if (_systemInfoValues.TryGetValue("system-drive", out var drive))
                drive.Text = snapshot.SystemDriveUsedPercent is { } used && snapshot.SystemDriveFreeBytes is { } free
                    ? $"{used:0}% · 剩余 {FormatBytes((ulong)free)}"
                    : "不可用";
        }
        catch { }
        finally { _systemMetricsRefreshRunning = false; }
    }

    private static string FormatBytes(ulong bytes)
    {
        var size = (double)bytes;
        var units = new[] { "B", "KB", "MB", "GB", "TB" };
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:0.#} {units[unit]}";
    }

    private void ConfigureAutoWallpaperTimer()
    {
        _wallpaperRotationTimer?.Stop();
        _wallpaperRotationTimer = null;
        if (!_state.AutoWallpaperEnabled || !DesktopLayerReady) return;

        _state.AutoWallpaperIntervalMinutes = Math.Clamp(_state.AutoWallpaperIntervalMinutes, 15, 1440);
        _wallpaperRotationTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(_state.AutoWallpaperIntervalMinutes)
        };
        _wallpaperRotationTimer.Tick += async (_, _) => await RotateWallpaperAsync();
        _wallpaperRotationTimer.Start();
    }

    private async Task RotateWallpaperAsync()
    {
        if (_wallpaperRotationRunning || !_state.AutoWallpaperEnabled) return;
        _wallpaperRotationRunning = true;
        try
        {
            var query = WallpaperLibraryService.GetStyleQuery(_state.AutoWallpaperStyle, _state.AutoWallpaperCustomQuery);
            var results = await WallpaperLibraryService.SearchAsync(query);
            if (results.Count == 0) throw new InvalidOperationException("当前壁纸风格没有找到可用图片。");

            var alternatives = results.Where(item => !string.Equals(item.DescriptionUrl, _state.WallpaperSourceUrl, StringComparison.OrdinalIgnoreCase)).ToArray();
            var candidates = alternatives.Length > 0 ? alternatives : results.ToArray();
            var next = candidates[Random.Shared.Next(candidates.Length)];
            var path = await WallpaperLibraryService.DownloadAsync(next);

            _state.WallpaperPath = path;
            _state.WallpaperTitle = next.Title;
            _state.WallpaperAuthor = next.Author;
            _state.WallpaperLicense = next.License;
            _state.WallpaperSourceUrl = next.DescriptionUrl;
            _state.AutoWallpaperLastChangedUtc = DateTime.UtcNow;
            RootSurface.Background = CreateDesktopWallpaperBrush(path);
            SaveState();
        }
        catch (Exception ex)
        {
            try
            {
                var logPath = Path.Combine(_store.DataDirectory, "wallpaper-rotation.log");
                File.AppendAllText(logPath, $"{DateTime.Now:O} {ex}{Environment.NewLine}");
            }
            catch { }
        }
        finally { _wallpaperRotationRunning = false; }
    }

    private void CreateGroup()
    {
        var name = PromptForText("新建桌面分组", "分组名称", "");
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        if (name.Equals("未分组", StringComparison.OrdinalIgnoreCase) || _state.DesktopGroups.Contains(name, StringComparer.CurrentCultureIgnoreCase))
        { MessageBox.Show("这个分组名称已存在。", "桌面管家", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        _state.DesktopGroups.Add(name);
        SaveAndRender();
    }

    private void RenameGroup(string oldName)
    {
        var name = PromptForText("重命名桌面分组", "新的分组名称", oldName)?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Equals(oldName, StringComparison.OrdinalIgnoreCase)) return;
        if (name.Equals("未分组", StringComparison.OrdinalIgnoreCase) || _state.DesktopGroups.Contains(name, StringComparer.CurrentCultureIgnoreCase))
        { MessageBox.Show("这个分组名称已存在。", "桌面管家", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var index = _state.DesktopGroups.IndexOf(oldName);
        if (index >= 0) _state.DesktopGroups[index] = name;
        if (_state.GroupLayouts.Remove(oldName, out var groupLayout))
            _state.GroupLayouts[name] = groupLayout;
        if (_state.GroupDisplayModes.Remove(oldName, out var displayMode))
            _state.GroupDisplayModes[name] = displayMode;
        foreach (var path in _state.DesktopAssignments.Keys.ToArray())
            if (_state.DesktopAssignments[path].Equals(oldName, StringComparison.OrdinalIgnoreCase)) _state.DesktopAssignments[path] = name;
        SaveAndRender();
    }

    private void DeleteGroup(string name)
    {
        if (MessageBox.Show($"删除“{name}”分组？项目会回到未分组，桌面文件不会删除。", "删除桌面分组", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _state.DesktopGroups.Remove(name);
        _state.GroupLayouts.Remove(name);
        _state.GroupDisplayModes.Remove(name);
        foreach (var path in _state.DesktopAssignments.Keys.ToArray())
            if (_state.DesktopAssignments[path].Equals(name, StringComparison.OrdinalIgnoreCase))
                _state.DesktopAssignments[path] = "未分组";
        SaveAndRender();
    }

    private void SetGroupDisplayMode(string group, string mode)
    {
        _state.GroupDisplayModes[group] = mode;
        SaveAndRender();
    }

    private void DeleteBookmarkCard()
    {
        var visibleBookmarks = _state.Bookmarks.Where(bookmark => bookmark.ShowOnHome &&
            string.Equals(bookmark.Group, "常用网址", StringComparison.CurrentCultureIgnoreCase)).ToArray();
        if (visibleBookmarks.Length == 0) return;
        if (MessageBox.Show("删除“常用网址”卡片？书签会保留，并移入“未分组”卡片。", "删除书签卡片", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        foreach (var bookmark in visibleBookmarks) bookmark.Group = "未分组";
        SaveAndRender();
    }

    private void MoveDesktopItem(DesktopItem item, string target)
    {
        var key = DesktopItemDiscovery.Normalize(item.Path);
        _state.DesktopAssignments[key] = target;
        SaveAndRender();
    }

    private static Border? FindDesktopFolderTile(DependencyObject source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is Border tile && tile.Tag is DesktopItem desktopItem && Directory.Exists(desktopItem.Path))
                return tile;
        return null;
    }

    private void MoveIntoDesktopFolder(DesktopItem source, DesktopItem folder)
    {
        if (!Directory.Exists(folder.Path)) return;
        try
        {
            MovePathIntoFolder(source.Path, folder.Path);
            _state.DesktopAssignments.Remove(DesktopItemDiscovery.Normalize(source.Path));
            SaveAndRender();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法将“{source.Name}”移动到“{folder.Name}”：\n{ex.Message}", "移动失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void MovePathIntoFolder(string sourcePath, string destinationFolder)
    {
        var sourceFullPath = Path.GetFullPath(sourcePath);
        var destinationFullPath = Path.GetFullPath(destinationFolder);
        if (string.Equals(sourceFullPath.TrimEnd(Path.DirectorySeparatorChar), destinationFullPath.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return;
        if (Directory.Exists(sourceFullPath) && destinationFullPath.StartsWith(sourceFullPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("不能将文件夹移动到它自身的子文件夹中。");
        var destinationPath = Path.Combine(destinationFullPath, Path.GetFileName(sourceFullPath));
        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
            throw new IOException($"目标文件夹中已存在“{Path.GetFileName(sourceFullPath)}”。");
        if (Directory.Exists(sourceFullPath)) Directory.Move(sourceFullPath, destinationPath);
        else if (File.Exists(sourceFullPath)) File.Move(sourceFullPath, destinationPath);
        else throw new FileNotFoundException("要移动的桌面项目已不存在。", sourceFullPath);
    }

    private void AutoGroupByFileCategory()
    {
        var items = _desktopItems.GetItems(_state);
        if (items.Count == 0) return;

        var categories = new[] { "文件夹", "应用程序", "文档", "图片", "视频", "音频", "压缩包", "其他文件" };
        foreach (var category in categories)
        {
            if (!_state.DesktopGroups.Contains(category, StringComparer.CurrentCultureIgnoreCase))
                _state.DesktopGroups.Add(category);
        }

        foreach (var item in items)
        {
            var category = GetFileCategory(item);
            _state.DesktopAssignments[DesktopItemDiscovery.Normalize(item.Path)] = category;
        }

        var categoryCounts = items.GroupBy(item => GetFileCategory(item), StringComparer.CurrentCultureIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.CurrentCultureIgnoreCase);
        foreach (var category in categories)
        {
            if (!categoryCounts.TryGetValue(category, out var count) || count == 0) continue;

            var layout = _state.GroupLayouts.TryGetValue(category, out var existing)
                ? existing
                : _state.GroupLayouts[category] = new DesktopGroupLayout();
            var columns = Math.Clamp((int)((SystemParameters.WorkArea.Width * .34 - 32) / GetDesktopTileWidth()), 2, 6);
            var rows = (int)Math.Ceiling(count / (double)columns);
            layout.Width = Math.Clamp(32 + columns * GetDesktopTileWidth(), 260, Math.Max(260, SystemParameters.WorkArea.Width - 90));
            layout.Height = Math.Clamp(GetGroupRequiredHeight(rows), 150, Math.Max(150, GetDesktopCanvasHeight()));
        }

        ArrangeCategoryGroups(categories);
        SaveState();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(RenderDesktop));
    }

    private static string GetFileCategory(DesktopItem item)
    {
        if (Directory.Exists(item.Path)) return "文件夹";
        var extension = Path.GetExtension(item.Path).ToLowerInvariant();
        if (extension is ".lnk" or ".url") return "应用程序";
        if (extension == ".exe" || extension == ".com" || extension == ".bat" || extension == ".cmd" || extension == ".msi") return "应用程序";
        if (new[] { ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".pdf", ".txt", ".md", ".rtf", ".csv" }.Contains(extension)) return "文档";
        if (new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".svg", ".ico" }.Contains(extension)) return "图片";
        if (new[] { ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".m4v" }.Contains(extension)) return "视频";
        if (new[] { ".mp3", ".wav", ".flac", ".aac", ".m4a", ".ogg", ".wma" }.Contains(extension)) return "音频";
        if (new[] { ".zip", ".rar", ".7z", ".tar", ".gz", ".iso" }.Contains(extension)) return "压缩包";
        return "其他文件";
    }

    private void ArrangeCategoryGroups(IReadOnlyList<string> categories)
    {
        const double gap = 14;
        var canvasWidth = Math.Max(700, SystemParameters.WorkArea.Width - 68);
        var canvasHeight = GetDesktopCanvasHeight();
        double left = 0, top = 0, rowHeight = 0;
        foreach (var category in categories)
        {
            var layout = _state.GroupLayouts[category];
            if (left > 0 && left + layout.Width > canvasWidth)
            {
                left = 0;
                top += rowHeight + gap;
                rowHeight = 0;
            }
            layout.Left = left;
            layout.Top = Math.Min(top, Math.Max(0, canvasHeight - layout.Height));
            left += layout.Width + gap;
            rowHeight = Math.Max(rowHeight, layout.Height);
        }
    }

    private void RefreshDesktop()
    {
        RenderDesktop();
        SaveState();
    }

    private void AutoArrangeGroups()
    {
        var canvas = DesktopSurface.Children.OfType<Canvas>().FirstOrDefault();
        if (canvas is null) return;

        const double gap = 14;
        const double rowTolerance = 16;
        var remaining = canvas.Children.OfType<Border>()
            .OrderBy(panel => Canvas.GetTop(panel))
            .ThenBy(panel => Canvas.GetLeft(panel))
            .ToList();
        var ordered = new List<Border>();
        while (remaining.Count > 0)
        {
            var rowTop = Canvas.GetTop(remaining[0]);
            var row = remaining.Where(panel => Math.Abs(Canvas.GetTop(panel) - rowTop) <= rowTolerance)
                .OrderBy(panel => Canvas.GetLeft(panel)).ToArray();
            ordered.AddRange(row);
            foreach (var panel in row) remaining.Remove(panel);
        }

        double left = 0, top = 0, rowHeight = 0;
        foreach (var panel in ordered)
        {
            if (left > 0 && left + panel.Width > canvas.ActualWidth)
            {
                left = 0;
                top += rowHeight + gap;
                rowHeight = 0;
            }
            if (left > 0 && top + panel.Height > canvas.ActualHeight)
            {
                left = 0;
                top = 0;
                rowHeight = 0;
            }

            Canvas.SetLeft(panel, left);
            Canvas.SetTop(panel, top);
            Panel.SetZIndex(panel, ++_groupZOrder);
            if (panel.Tag is string name && name == "@bookmarks")
            {
                _state.BookmarkCardLayout.Left = left;
                _state.BookmarkCardLayout.Top = top;
                _state.BookmarkCardLayout.Width = panel.Width;
                _state.BookmarkCardLayout.Height = panel.Height;
            }
            else if (panel.Tag is string groupName && _state.GroupLayouts.TryGetValue(groupName, out var savedLayout))
            {
                savedLayout.Left = left;
                savedLayout.Top = top;
                savedLayout.Width = panel.Width;
                savedLayout.Height = panel.Height;
            }
            left += panel.Width + gap;
            rowHeight = Math.Max(rowHeight, panel.Height);
        }
        SaveState();
    }

    private string? PromptForText(string title, string label, string initial)
    {
        var window = new Window { Title = title, Width = 380, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen, Background = SurfaceBrush(), Foreground = PrimaryBrush(), FontFamily = FontFamily };
        var layout = new StackPanel { Margin = new Thickness(22) };
        layout.Children.Add(new TextBlock { Text = label, FontSize = 12, Margin = new Thickness(0, 0, 0, 6) });
        var input = new TextBox { Text = initial, Height = 34, Padding = new Thickness(8, 5, 8, 5), MaxLength = 40 };
        layout.Children.Add(input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 17, 0, 0) };
        var cancel = new Button { Content = "取消", Width = 76, Height = 32, Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => window.DialogResult = false;
        var save = new Button { Content = "确定", Width = 76, Height = 32, IsDefault = true };
        save.Click += (_, _) => window.DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(save); layout.Children.Add(buttons);
        window.Content = layout;
        window.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return window.ShowDialog() == true ? input.Text : null;
    }

    private void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show($"无法打开项目：{ex.Message}", "桌面管家", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
    private Brush PrimaryBrush() => (Brush)Application.Current.Resources["PrimaryText"];
    private Brush SecondaryBrush() => (Brush)Application.Current.Resources["SecondaryText"];
    private Brush PanelBrush() => new SolidColorBrush(IsDark() ? Color.FromArgb(132, 34, 37, 43) : Color.FromArgb(126, 247, 250, 253));
    private Brush PanelBorderBrush() => new SolidColorBrush(IsDark() ? Color.FromArgb(82, 255, 255, 255) : Color.FromArgb(112, 255, 255, 255));
    private Brush ButtonBrush() => PanelBrush();
    private Brush DropTargetBrush() => new SolidColorBrush(IsDark() ? Color.FromArgb(210, 180, 205, 240) : Color.FromArgb(220, 67, 137, 220));
    private Brush HoverBrush() => new SolidColorBrush(IsDark() ? Color.FromArgb(145, 95, 99, 106) : Color.FromArgb(155, 255, 255, 255));
    private Brush PressedBrush() => new SolidColorBrush(IsDark() ? Color.FromArgb(165, 92, 98, 108) : Color.FromArgb(185, 234, 239, 245));
    private DropShadowEffect CardShadow() => new() { BlurRadius = 12, ShadowDepth = 2, Direction = 270, Opacity = IsDark() ? .16 : .08, Color = Colors.Black };
    private static bool IsDark() => Application.Current.Resources["AppBackground"] is SolidColorBrush brush && brush.Color.R < 80;

    private Border CreateBookmarkTile(Bookmark bookmark, string[] groups, bool listMode = false, double listWidth = 220)
    {
        var iconSize = Math.Clamp(_state.DesktopIconSize, 32, 80);
        var displayIconSize = listMode ? 30 : iconSize;
        var tileWidth = listMode ? listWidth : GetDesktopTileWidth();
        var tile = new Border { Width = tileWidth, Height = listMode ? 46 : GetDesktopTileHeight(), Margin = new Thickness(1), Padding = new Thickness(2), CornerRadius = new CornerRadius(12), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        UIElement content;
        var icon = new Image { Width = displayIconSize, Height = displayIconSize, Stretch = System.Windows.Media.Stretch.Uniform, SnapsToDevicePixels = true, UseLayoutRounding = true, Source = RenderIconTile(bookmark.Name, displayIconSize) };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        if (listMode)
        {
            var row = new Grid { Margin = new Thickness(5, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(icon);
            var name = new TextBlock { Text = bookmark.Name, FontSize = 11, Foreground = PrimaryBrush(), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 8, 0) };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            content = row;
        }
        else
        {
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(icon);
            stack.Children.Add(new TextBlock { Text = bookmark.Name, FontSize = 9.5, Foreground = PrimaryBrush(), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = tileWidth - 6 });
            content = stack;
        }
        tile.Child = content;
        tile.ToolTip = bookmark.Url;
        tile.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) OpenPath(bookmark.Url); };
        Point dragStart = default;
        tile.PreviewMouseLeftButtonDown += (_, e) => dragStart = e.GetPosition(tile);
        tile.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            var current = e.GetPosition(tile);
            if (Math.Abs(current.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(current.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            var payload = new DataObject();
            payload.SetData(BookmarkDragFormat, bookmark.Id.ToString());
            DragDrop.DoDragDrop(tile, payload, DragDropEffects.Move);
        };
        tile.ContextMenu = CreateMacContextMenu();
        tile.ContextMenu.Items.Add(MenuItem("打开网址", (_, _) => OpenPath(bookmark.Url)));
        var moveMenu = new MenuItem { Header = "移动到分组" };
        foreach (var group in groups)
        {
            var destination = group;
            var move = new MenuItem { Header = group, IsCheckable = true, IsChecked = string.Equals(bookmark.Group, group, StringComparison.CurrentCultureIgnoreCase) };
            move.Click += (_, _) => { bookmark.Group = destination; SaveAndRender(); };
            moveMenu.Items.Add(move);
        }
        tile.ContextMenu.Items.Add(moveMenu);
        tile.ContextMenu.Items.Add(MenuItem("从桌面隐藏", (_, _) => { bookmark.ShowOnHome = false; SaveAndRender(); }));
        LoadFaviconAsync(bookmark, icon);
        return tile;
    }

    private BitmapSource RenderIconTile(string text, int size)
    {
        var visual = new DrawingVisual();
        var palette = new[] { "#3978C6", "#33816D", "#8167A8", "#B77237", "#AA5558" };
        var color = (Color)ColorConverter.ConvertFromString(palette[Math.Abs(text.GetHashCode()) % palette.Length]);
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRoundedRectangle(new SolidColorBrush(color), null, new Rect(0, 0, size, size), size * .29, size * .29);
            var glyph = string.IsNullOrWhiteSpace(text) ? "?" : text[..1].ToUpperInvariant();
            var formatted = new FormattedText(glyph, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), size * .42, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            drawing.DrawText(formatted, new Point((size - formatted.Width) / 2, (size - formatted.Height) / 2));
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static async void LoadFaviconAsync(Bookmark bookmark, Image target)
    {
        if (!Uri.TryCreate(bookmark.Url, UriKind.Absolute, out var siteUri)) return;
        var key = bookmark.Url.TrimEnd('/');
        var task = FaviconTasks.GetOrAdd(key, _ => DownloadFaviconAsync(siteUri));
        var source = await task;
        if (source is null) return;
        await target.Dispatcher.InvokeAsync(() =>
        {
            var nativeSize = Math.Min(source.PixelWidth, source.PixelHeight);
            var displaySize = Math.Min(target.Width, nativeSize);
            target.Width = displaySize;
            target.Height = displaySize;
            target.Source = source;
        });
    }

    private static async Task<BitmapSource?> DownloadFaviconAsync(Uri siteUri)
    {
        var declaredCandidates = new List<(int Quality, Uri Uri)>();
        try
        {
            var html = await FaviconHttpClient.GetStringAsync(siteUri);
            foreach (Match tag in Regex.Matches(html, @"<link\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                var rel = Regex.Match(tag.Value, @"\brel\s*=\s*[""']?([^\s""'>]+(?:\s+[^""'>]+)*)", RegexOptions.IgnoreCase);
                var href = Regex.Match(tag.Value, @"\bhref\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
                if (!href.Success) href = Regex.Match(tag.Value, @"\bhref\s*=\s*([^\s>]+)", RegexOptions.IgnoreCase);
                if (!rel.Success || !href.Success) continue;
                var relValue = WebUtility.HtmlDecode(rel.Groups[1].Value);
                if (!Regex.IsMatch(relValue, @"(?:^|\s)(?:icon|shortcut|apple-touch-icon|mask-icon)(?:\s|$)", RegexOptions.IgnoreCase)) continue;
                var hrefValue = WebUtility.HtmlDecode(href.Groups[1].Value.Trim().Trim('"', '\''));
                if (Uri.TryCreate(siteUri, hrefValue, out var declared) && declared.Scheme is "http" or "https")
                {
                    var sizes = Regex.Matches(tag.Value, @"\b(\d+)\s*x\s*(\d+)\b", RegexOptions.IgnoreCase)
                        .Select(match => int.TryParse(match.Groups[1].Value, out var width) && int.TryParse(match.Groups[2].Value, out var height) ? Math.Min(width, height) : 0)
                        .DefaultIfEmpty(0).Max();
                    var appleTouch = relValue.Contains("apple-touch-icon", StringComparison.OrdinalIgnoreCase);
                    declaredCandidates.Add((sizes + (appleTouch ? 10000 : 0), declared));
                }
            }
        }
        catch { }

        var candidates = declaredCandidates.OrderByDescending(candidate => candidate.Quality).Select(candidate => candidate.Uri).Concat(new[]
        {
            new Uri(siteUri, "/apple-touch-icon.png"),
            new Uri(siteUri, "/apple-touch-icon-precomposed.png"),
            new Uri(siteUri, "/favicon-192x192.png"),
            new Uri(siteUri, "/android-chrome-192x192.png"),
            new Uri($"https://www.google.com/s2/favicons?domain_url={Uri.EscapeDataString(siteUri.GetLeftPart(UriPartial.Authority))}&sz=256"),
            new Uri(siteUri, "/favicon.ico")
        }).DistinctBy(uri => uri.AbsoluteUri, StringComparer.OrdinalIgnoreCase);

        BitmapSource? bestImage = null;
        var bestResolution = 0;
        foreach (var faviconUri in candidates.DistinctBy(uri => uri.AbsoluteUri, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var bytes = await FaviconHttpClient.GetByteArrayAsync(faviconUri);
                using var stream = new MemoryStream(bytes);
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count == 0) continue;
                var frame = decoder.Frames.OrderByDescending(candidate => Math.Min(candidate.PixelWidth, candidate.PixelHeight)).First();
                var resolution = Math.Min(frame.PixelWidth, frame.PixelHeight);
                if (resolution <= bestResolution) continue;
                if (frame.CanFreeze) frame.Freeze();
                bestImage = frame;
                bestResolution = resolution;
                if (bestResolution >= 128) return bestImage;
            }
            catch { }
        }
        return bestImage;
    }

    private void SaveAndRender()
    {
        RenderDesktop();
        SaveState();
    }
    private void SaveState()
    {
        var canvas = DesktopSurface.Children.OfType<Canvas>().FirstOrDefault();
        if (canvas is not null)
        {
            _state.LayoutCanvasWidth = canvas.Width;
            _state.LayoutCanvasHeight = canvas.Height;
            _state.CardZOrder = canvas.Children.OfType<Border>().Where(IsDesktopCard)
                .OrderBy(card => Panel.GetZIndex(card)).Select(card => (string)card.Tag).ToList();
        }
        try { _store.Save(_state); }
        catch (Exception ex) { MessageBox.Show($"保存本地数据失败：{ex.Message}", "桌面管家", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void ShowDesktopLayer()
    {
        if (!_shellHost.IsAttached && !_shellHost.Attach(this))
        {
            MessageBox.Show("无法重新连接 Windows 桌面层。请重新启动桌面管家。", "桌面管家", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _shellHost.ShowSurface();
    }

    private void ExitApplication()
    {
        _allowExit = true;
        ShutdownDesktopLayer();
        Application.Current.Shutdown();
    }

    public void ShutdownDesktopLayer()
    {
        _taskbarVisibility.EnsureVisible();
        _shellHost.RefreshWorkArea();
        _systemInfoTimer?.Stop();
        _weatherTimer?.Stop();
        _systemMetrics.Dispose();
        _shellHost.Dispose();
        if (_trayDisposed) return;
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trayDisposed = true;
    }

    private static Brush CreateDesktopWallpaperBrush(string? customWallpaperPath)
    {
        try
        {
            var wallpaperPath = customWallpaperPath;
            if (string.IsNullOrWhiteSpace(wallpaperPath) || !File.Exists(wallpaperPath))
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
                wallpaperPath = key?.GetValue("Wallpaper") as string;
            }
            if (!string.IsNullOrWhiteSpace(wallpaperPath) && File.Exists(wallpaperPath))
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(wallpaperPath, UriKind.Absolute);
                image.EndInit();
                image.Freeze();
                var brush = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
                brush.Freeze();
                return brush;
            }
        }
        catch { }

        return (Brush)Application.Current.Resources["AppBackground"];
    }

    private Brush SurfaceBrush() => (Brush)Application.Current.Resources["SurfaceBackground"];
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Runtime.InteropServices;
using DesktopManager.Models;
using DesktopManager.Services;

namespace DesktopManager;

public partial class BookmarkManagerWindow : Window
{
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmSystemBackdropAcrylic = 3;
    private const int DwmWindowCornerRound = 2;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    private readonly AppState _state;
    private readonly Action _save;

    public BookmarkManagerWindow(AppState state, Action save)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyAcrylicBackdrop();
        _state = state;
        _save = save;
        Closed += (_, _) => _save();
        SearchBox.ToolTip = "搜索名称、网址或文件夹";
        RenderRows();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RenderRows();

    private void RenderRows()
    {
        if (Rows is null) return;
        Rows.Children.Clear();
        var query = SearchBox.Text.Trim();
        var items = _state.Bookmarks.Where(bookmark => string.IsNullOrWhiteSpace(query) ||
            bookmark.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            bookmark.Url.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            bookmark.Folder.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(bookmark => bookmark.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();

        foreach (var bookmark in items) Rows.Children.Add(CreateRow(bookmark));
        CountText.Text = $"{_state.Bookmarks.Count(item => item.ShowOnHome)} 个显示在桌面 · {_state.Bookmarks.Count} 个收藏";
    }

    private UIElement CreateRow(Bookmark bookmark)
    {
        var grid = new Grid { MinHeight = 70, Margin = new Thickness(8, 0, 5, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(132) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        var visibility = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var check = new CheckBox { IsChecked = bookmark.ShowOnHome, Style = (Style)FindResource("MacSwitch"), ToolTip = "是否在桌面显示此网址卡片" };
        check.Click += (_, _) => { bookmark.ShowOnHome = check.IsChecked == true; _save(); RenderRows(); };
        visibility.Children.Add(check);
        visibility.Children.Add(new TextBlock { Text = "桌面", FontSize = 10, Foreground = Secondary(), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
        Grid.SetColumn(visibility, 0); grid.Children.Add(visibility);

        var meta = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 10, 0) };
        meta.Children.Add(new TextBlock { Text = bookmark.Name, FontSize = 12, FontWeight = FontWeights.Medium, Foreground = (Brush)FindResource("TextPrimary"), TextTrimming = TextTrimming.CharacterEllipsis });
        meta.Children.Add(new TextBlock { Text = bookmark.Url, FontSize = 10, Foreground = Secondary(), Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(meta, 1); grid.Children.Add(meta);

        var folder = new TextBlock { Text = bookmark.Folder, FontSize = 10, Foreground = Secondary(), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(folder, 2); grid.Children.Add(folder);
        var action = new Button { Content = "···", Width = 34, Height = 30, Style = (Style)FindResource("MacButton"), Padding = new Thickness(0), ToolTip = "编辑或删除" };
        action.ContextMenu = new ContextMenu();
        var edit = new MenuItem { Header = "编辑" }; edit.Click += (_, _) => Edit(bookmark);
        var remove = new MenuItem { Header = "删除收藏" }; remove.Click += (_, _) => Delete(bookmark);
        action.ContextMenu.Items.Add(edit); action.ContextMenu.Items.Add(remove);
        action.Click += (_, _) => action.ContextMenu.IsOpen = true;
        Grid.SetColumn(action, 3); grid.Children.Add(action);
        return new Border { Child = grid, BorderBrush = (Brush)FindResource("LineBrush"), BorderThickness = new Thickness(0, 0, 0, 1) };
    }

    private Brush Secondary() => (Brush)Application.Current.Resources["SecondaryText"];

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new BookmarkDialog { WindowStartupLocation = WindowStartupLocation.CenterScreen };
        if (dialog.ShowDialog() != true || dialog.Result is not { } item) return;
        _state.Bookmarks.Add(item); _save(); RenderRows();
    }

    private void ImportBrowser_Click(object sender, RoutedEventArgs e)
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
            _state.Bookmarks.Add(bookmark);
            imported++;
        }

        if (imported > 0) _save();
        RenderRows();
        MessageBox.Show(this, $"已导入 {imported} 个书签，跳过 {duplicates} 个重复网址。", "导入完成", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Edit(Bookmark bookmark)
    {
        var dialog = new BookmarkDialog(bookmark) { WindowStartupLocation = WindowStartupLocation.CenterScreen };
        if (dialog.ShowDialog() != true || dialog.Result is not { } item) return;
        bookmark.Name = item.Name; bookmark.Url = item.Url; bookmark.Folder = item.Folder; bookmark.ShowOnHome = item.ShowOnHome;
        _save(); RenderRows();
    }

    private void Delete(Bookmark bookmark)
    {
        if (MessageBox.Show(this, $"从收藏库删除“{bookmark.Name}”？", "删除收藏", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _state.Bookmarks.Remove(bookmark); _save(); RenderRows();
    }

    private void Done_Click(object sender, RoutedEventArgs e) => Close();

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

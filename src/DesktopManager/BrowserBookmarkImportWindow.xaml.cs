using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopManager.Models;
using DesktopManager.Services;

namespace DesktopManager;

public partial class BrowserBookmarkImportWindow : Window
{
    private readonly IReadOnlyList<BrowserBookmarkSource> _sources;
    private readonly HashSet<string> _selectedUrls = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<ImportedBrowserBookmark> _entries = [];

    public IReadOnlyList<Bookmark> ImportedBookmarks { get; private set; } = [];

    public BrowserBookmarkImportWindow(IReadOnlyList<BrowserBookmarkSource> sources)
    {
        InitializeComponent();
        _sources = sources;
        SourcePicker.ItemsSource = _sources;
        SourcePicker.DisplayMemberPath = nameof(BrowserBookmarkSource.DisplayName);
        if (_sources.Count > 0) SourcePicker.SelectedIndex = 0;
        else Rows.Children.Add(new TextBlock { Text = "没有发现可读取的浏览器书签。", Margin = new Thickness(12), Foreground = SecondaryBrush() });
        UpdateSelectionState();
    }

    private void SourcePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourcePicker.SelectedItem is not BrowserBookmarkSource source) return;
        try
        {
            _entries = BrowserBookmarkImporter.Read(source);
            _selectedUrls.Clear();
            RenderRows();
        }
        catch (Exception ex)
        {
            _entries = [];
            _selectedUrls.Clear();
            Rows.Children.Clear();
            Rows.Children.Add(new TextBlock
            {
                Text = $"读取 {source.DisplayName} 的书签失败：{ex.Message}",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12), Foreground = SecondaryBrush()
            });
            UpdateSelectionState();
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RenderRows();

    private void RenderRows()
    {
        if (Rows is null) return;
        Rows.Children.Clear();
        var query = SearchBox.Text.Trim();
        var visible = _entries.Where(item => string.IsNullOrWhiteSpace(query) ||
            item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            item.Url.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            item.Folder.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();

        string? currentFolder = null;
        foreach (var item in visible)
        {
            if (!string.Equals(currentFolder, item.Folder, StringComparison.Ordinal))
            {
                currentFolder = item.Folder;
                if (!string.IsNullOrWhiteSpace(currentFolder))
                    Rows.Children.Add(new TextBlock { Text = currentFolder, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = SecondaryBrush(), Margin = new Thickness(7, 8, 4, 4) });
            }

            var checkbox = new CheckBox
            {
                IsChecked = _selectedUrls.Contains(BrowserBookmarkImporter.NormalizeUrl(item.Url)),
                Margin = new Thickness(6, 5, 6, 5), Tag = item,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = item.Name, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = item.Url, FontSize = 10, Foreground = SecondaryBrush(), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) });
            checkbox.Content = text;
            checkbox.Checked += SelectionChanged;
            checkbox.Unchecked += SelectionChanged;
            Rows.Children.Add(checkbox);
        }

        if (visible.Length == 0)
            Rows.Children.Add(new TextBlock { Text = "没有匹配的书签。", Margin = new Thickness(12), Foreground = SecondaryBrush() });
        UpdateSelectionState(visible);
    }

    private void SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: ImportedBrowserBookmark item, IsChecked: { } isChecked })
        {
            var key = BrowserBookmarkImporter.NormalizeUrl(item.Url);
            if (isChecked) _selectedUrls.Add(key);
            else _selectedUrls.Remove(key);
        }
        UpdateSelectionState();
    }

    private void UpdateSelectionState(IReadOnlyCollection<ImportedBrowserBookmark>? visible = null)
    {
        var visibleItems = visible ?? _entries;
        var visibleSelected = visibleItems.Count(item => _selectedUrls.Contains(BrowserBookmarkImporter.NormalizeUrl(item.Url)));
        CountText.Text = $"已选 {_selectedUrls.Count} 个 · 当前列表 {visibleItems.Count} 个";
        ImportButton.IsEnabled = _selectedUrls.Count > 0;
        SelectAllButton.Content = visibleItems.Count > 0 && visibleSelected == visibleItems.Count ? "清空" : "全选";
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        var visible = _entries.Where(item => string.IsNullOrWhiteSpace(query) ||
            item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            item.Url.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            item.Folder.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        var select = visible.Any(item => !_selectedUrls.Contains(BrowserBookmarkImporter.NormalizeUrl(item.Url)));
        foreach (var item in visible)
        {
            var key = BrowserBookmarkImporter.NormalizeUrl(item.Url);
            if (select) _selectedUrls.Add(key);
            else _selectedUrls.Remove(key);
        }
        RenderRows();
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var showOnHome = ShowOnHomeCheck.IsChecked == true;
        ImportedBookmarks = _entries
            .Where(item => _selectedUrls.Contains(BrowserBookmarkImporter.NormalizeUrl(item.Url)))
            .Select(item => new Bookmark
            {
                Name = item.Name,
                Url = item.Url,
                Folder = string.IsNullOrWhiteSpace(item.Folder) ? "浏览器导入" : item.Folder,
                ShowOnHome = showOnHome
            }).ToArray();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private Brush SecondaryBrush() => (Brush)Application.Current.Resources["SecondaryText"];
}

using System.Windows;
using DesktopManager.Models;

namespace DesktopManager;

public partial class BookmarkDialog : Window
{
    public Bookmark? Result { get; private set; }

    public BookmarkDialog(Bookmark? existing = null)
    {
        InitializeComponent();
        if (existing is not null)
        {
            DialogHeading.Text = "编辑网址";
            NameBox.Text = existing.Name;
            UrlBox.Text = existing.Url;
            FolderBox.Text = existing.Folder;
            ShowOnHomeBox.IsChecked = existing.ShowOnHome;
            Result = new Bookmark { Id = existing.Id };
        }
        Loaded += (_, _) => NameBox.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var urlText = UrlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)) { ValidationText.Text = "请输入显示名称。"; NameBox.Focus(); return; }
        if (!Uri.TryCreate(urlText, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        { ValidationText.Text = "请输入有效的 http 或 https 网址。"; UrlBox.Focus(); return; }

        Result = new Bookmark
        {
            Id = Result?.Id ?? Guid.NewGuid(),
            Name = name,
            Url = uri.AbsoluteUri,
            Folder = string.IsNullOrWhiteSpace(FolderBox.Text) ? "未分类" : FolderBox.Text.Trim(),
            ShowOnHome = ShowOnHomeBox.IsChecked == true
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

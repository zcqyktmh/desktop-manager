using System.Text.Json;

namespace DesktopManager.Services;

public sealed record BrowserBookmarkSource(string Browser, string Profile, string FilePath)
{
    public string DisplayName => $"{Browser} · {Profile}";
}

public sealed record ImportedBrowserBookmark(string Name, string Url, string Folder);

public static class BrowserBookmarkImporter
{
    public static IReadOnlyList<BrowserBookmarkSource> DiscoverSources()
    {
        var sources = new List<BrowserBookmarkSource>();
        AddProfiles(sources, "Chrome", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "User Data"));
        AddProfiles(sources, "Edge", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Edge", "User Data"));
        AddProfiles(sources, "Brave", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BraveSoftware", "Brave-Browser", "User Data"));
        AddProfiles(sources, "Vivaldi", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vivaldi", "User Data"));
        AddSingleProfile(sources, "Opera", "Stable", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Opera Software", "Opera Stable", "Bookmarks"));
        return sources;
    }

    public static IReadOnlyList<ImportedBrowserBookmark> Read(BrowserBookmarkSource source)
    {
        using var stream = new FileStream(source.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var document = JsonDocument.Parse(stream);
        if (!document.RootElement.TryGetProperty("roots", out var roots)) return [];

        var result = new List<ImportedBrowserBookmark>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots.EnumerateObject())
        {
            if (root.Value.ValueKind != JsonValueKind.Object) continue;
            var rootName = root.Name switch
            {
                "bookmark_bar" => "书签栏",
                "other" => "其他书签",
                "synced" => "移动设备",
                _ => ""
            };
            if (root.Value.TryGetProperty("children", out var children))
                ReadNodes(children, rootName, result, seen);
        }
        return result;
    }

    private static void ReadNodes(JsonElement nodes, string folder, List<ImportedBrowserBookmark> result, HashSet<string> seen)
    {
        if (nodes.ValueKind != JsonValueKind.Array) return;
        foreach (var node in nodes.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object) continue;
            var type = ReadString(node, "type");
            var name = ReadString(node, "name");
            if (type == "folder")
            {
                var nextFolder = string.IsNullOrWhiteSpace(folder) ? name : string.IsNullOrWhiteSpace(name) ? folder : $"{folder}/{name}";
                if (node.TryGetProperty("children", out var children)) ReadNodes(children, nextFolder, result, seen);
                continue;
            }

            if (type != "url") continue;
            var url = ReadString(node, "url").Trim();
            if (!IsImportableUrl(url) || !seen.Add(NormalizeUrl(url))) continue;
            if (string.IsNullOrWhiteSpace(name)) name = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
            result.Add(new ImportedBrowserBookmark(name, url, folder));
        }
    }

    private static bool IsImportableUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeFile);

    public static string NormalizeUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)) return value.Trim().TrimEnd('/');
        var builder = new UriBuilder(uri) { Host = uri.Host.ToLowerInvariant() };
        if ((uri.Scheme == Uri.UriSchemeHttp && uri.Port == 80) || (uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443))
            builder.Port = -1;
        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }

    private static void AddProfiles(List<BrowserBookmarkSource> sources, string browser, string userDataDirectory)
    {
        if (!Directory.Exists(userDataDirectory)) return;
        foreach (var profile in Directory.EnumerateDirectories(userDataDirectory))
        {
            var bookmarksFile = Path.Combine(profile, "Bookmarks");
            if (!File.Exists(bookmarksFile)) continue;
            var profileName = Path.GetFileName(profile);
            if (profileName.Equals("System Profile", StringComparison.OrdinalIgnoreCase) ||
                profileName.Equals("Guest Profile", StringComparison.OrdinalIgnoreCase)) continue;
            sources.Add(new BrowserBookmarkSource(browser, profileName, bookmarksFile));
        }
    }

    private static void AddSingleProfile(List<BrowserBookmarkSource> sources, string browser, string profile, string bookmarksFile)
    {
        if (File.Exists(bookmarksFile)) sources.Add(new BrowserBookmarkSource(browser, profile, bookmarksFile));
    }

    private static string ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}

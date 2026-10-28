using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace DesktopManager.Services;

public sealed record WallpaperSearchResult(string Title, string ThumbnailUrl, string ImageUrl, string DescriptionUrl, int Width, int Height, string Author, string License)
{
    public BitmapSource? Thumbnail { get; init; }
}

public static class WallpaperLibraryService
{
    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DesktopManager/1.0 (Windows wallpaper library; https://commons.wikimedia.org/wiki/Commons:API)");
        client.DefaultRequestHeaders.Add("Api-User-Agent", "DesktopManager/1.0 (Windows wallpaper library; https://commons.wikimedia.org/wiki/Commons:API)");
        return client;
    }

    public static async Task<IReadOnlyList<WallpaperSearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var parameters = string.Join("&", new Dictionary<string, string>
        {
            ["action"] = "query",
            ["generator"] = "search",
            ["gsrsearch"] = query,
            ["gsrnamespace"] = "6",
            ["gsrlimit"] = "50",
            ["prop"] = "imageinfo",
            ["iiprop"] = "url|size|extmetadata",
            ["iiurlwidth"] = "500",
            ["format"] = "json",
            ["formatversion"] = "2"
        }.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        var uri = new Uri($"https://commons.wikimedia.org/w/api.php?{parameters}");
        using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("query", out var queryElement) || !queryElement.TryGetProperty("pages", out var pages))
            return [];

        var results = new List<WallpaperSearchResult>();
        foreach (var page in pages.EnumerateArray())
        {
            if (!page.TryGetProperty("imageinfo", out var imageInfos) || imageInfos.GetArrayLength() == 0) continue;
            var image = imageInfos[0];
            var width = image.TryGetProperty("width", out var widthElement) ? widthElement.GetInt32() : 0;
            var height = image.TryGetProperty("height", out var heightElement) ? heightElement.GetInt32() : 0;
            if (width < 1280 || height < 720 || width < height) continue;
            var thumbnailUrl = GetString(image, "thumburl");
            var imageUrl = GetString(image, "url");
            var descriptionUrl = GetString(image, "descriptionurl");
            if (!Uri.TryCreate(thumbnailUrl, UriKind.Absolute, out _) || !Uri.TryCreate(imageUrl, UriKind.Absolute, out _) || !Uri.TryCreate(descriptionUrl, UriKind.Absolute, out _)) continue;

            var metadata = image.TryGetProperty("extmetadata", out var metadataElement) ? metadataElement : default;
            var author = GetMetadata(metadata, "Artist");
            if (string.IsNullOrWhiteSpace(author)) author = "作者信息未提供";
            var license = GetMetadata(metadata, "LicenseShortName");
            if (string.IsNullOrWhiteSpace(license)) license = "请查看来源页许可";
            var title = page.TryGetProperty("title", out var titleElement) ? titleElement.GetString() ?? "" : "";
            title = Path.GetFileNameWithoutExtension(title.StartsWith("File:", StringComparison.OrdinalIgnoreCase) ? title[5..] : title);
            results.Add(new WallpaperSearchResult(title, thumbnailUrl, imageUrl, descriptionUrl, width, height, author, license));
        }
        return results;
    }

    public static string GetStyleQuery(string style, string? customQuery = null) => style switch
    {
        "自然" => "nature landscape panorama wallpaper",
        "星空" => "night sky stars landscape wallpaper",
        "海洋" => "ocean sea coast landscape wallpaper",
        "城市" => "city skyline landscape wallpaper",
        "自定义关键词" => string.IsNullOrWhiteSpace(customQuery) ? "landscape wallpaper panorama" : $"{customQuery.Trim()} wallpaper",
        _ => "landscape wallpaper panorama"
    };

    public static async Task<string> DownloadAsync(WallpaperSearchResult wallpaper, CancellationToken cancellationToken = default)
    {
        var uri = new Uri(wallpaper.ImageUrl);
        var extension = Path.GetExtension(uri.AbsolutePath);
        if (string.IsNullOrWhiteSpace(extension) || extension.Length > 8) extension = ".jpg";
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopManager", "Wallpapers");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, Guid.NewGuid().ToString("N") + extension.ToLowerInvariant());

        using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 100_000_000)
            throw new InvalidDataException("这张壁纸文件超过 100 MB，未下载。");

        try
        {
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long total = 0;
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken);
                    if (read == 0) break;
                    total += read;
                    if (total > 100_000_000) throw new InvalidDataException("这张壁纸文件超过 100 MB，未下载。");
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }
        }
        catch
        {
            if (File.Exists(destination)) File.Delete(destination);
            throw;
        }
        return destination;
    }

    public static async Task<BitmapSource?> LoadThumbnailAsync(string thumbnailUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await Client.GetAsync(thumbnailUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var memory = new MemoryStream();
            await source.CopyToAsync(memory, cancellationToken);
            memory.Position = 0;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 500;
            bitmap.StreamSource = memory;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch { return null; }
    }

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static string GetMetadata(JsonElement metadata, string name)
    {
        if (metadata.ValueKind != JsonValueKind.Object || !metadata.TryGetProperty(name, out var property)) return "";
        var value = GetString(property, "value");
        return WebUtility.HtmlDecode(Regex.Replace(value, "<[^>]+>", " ")).Trim();
    }
}

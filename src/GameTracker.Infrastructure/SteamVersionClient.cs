using System.Text.Json;
using System.Text.RegularExpressions;
using GameTracker.Core;

namespace GameTracker.Infrastructure;

public sealed record SteamVersionResult(VersionCheckState State, string? Version, string Message, string? Url = null);

public sealed partial class SteamVersionClient(HttpClient http)
{
    private sealed record StoreGame(string Id, string Name);

    public async Task<SteamVersionResult> CheckAsync(string title, string local, LocalVersionKind kind, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(local)) return new(VersionCheckState.None, null, "Yerel sürüm yok; kontrol atlandı.");
        var searchTitle = TitleMatcher.ArtworkTitle(title);
        using var search = await GetJsonAsync("https://store.steampowered.com/api/storesearch/?l=english&cc=us&term=" + Uri.EscapeDataString(searchTitle), ct);
        var matches = search.RootElement.GetProperty("items").EnumerateArray()
            .Where(x => x.GetProperty("type").GetString() == "app")
            .Select(x => new StoreGame(x.GetProperty("id").ToString(), x.GetProperty("name").GetString() ?? "")).ToArray();
        var game = TitleMatcher.Exact(searchTitle, matches, x => x.Name);
        if (game is null || !uint.TryParse(game.Id, out var appId))
            return new(VersionCheckState.Unavailable, null, "Steam'de tek ve kesin oyun/sürüm eşleşmesi bulunamadı.");
        // Store search also contains DLC and tools. Confirm the application type.
        using var details = await GetJsonAsync($"https://store.steampowered.com/api/appdetails?appids={appId}&l=english&cc=us", ct);
        var entry = details.RootElement.GetProperty(game.Id);
        if (!entry.GetProperty("success").GetBoolean() || entry.GetProperty("data").GetProperty("type").GetString() != "game" ||
            TitleMatcher.ArtworkKey(entry.GetProperty("data").GetProperty("name").GetString() ?? "") != TitleMatcher.ArtworkKey(title))
            return new(VersionCheckState.Unavailable, null, "Steam sonucu ana oyunla kesin olarak eşleşmedi.");
        using var news = await GetJsonAsync($"https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/?appid={appId}&count=100&maxlength=1&feeds=steam_community_announcements", ct);
        var appNews = news.RootElement.GetProperty("appnews");
        if (appNews.GetProperty("appid").GetUInt32() != appId) throw new IOException("Steam duyuru kimliği eşleşmedi.");
        var items = appNews.GetProperty("newsitems").EnumerateArray()
            .Where(n => n.GetProperty("feedname").GetString() == "steam_community_announcements" && n.GetProperty("date").GetInt64() <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            .OrderByDescending(n => n.GetProperty("date").GetInt64());
        foreach (var item in items)
        {
            var headline = item.GetProperty("title").GetString() ?? "";
            if (ReleasedVersion(headline) is not { } version) continue;
            var url = item.GetProperty("url").GetString();
            // Only offer a verified official Steam link in the UI.
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                uri.Host is not ("steamcommunity.com" or "store.steampowered.com")) url = $"https://store.steampowered.com/news/app/{appId}";
            var comparison = GameVersionDetector.Compare(local, version, kind);
            var evidence = $"Steam resmi duyurusu: {headline}. Duyuru sürümü esas alınır; Steam build numarasıyla exe sürümü aynı değildir.";
            return new(comparison switch { < 0 => VersionCheckState.Outdated, 0 => VersionCheckState.Matches, > 0 => VersionCheckState.Newer, _ => VersionCheckState.Unavailable }, version,
                comparison is null ? $"{evidence} Yerel sürümün türü/biçimi uyuşmuyor; oyun sürümünü elle girebilirsin." : evidence, url);
        }
        return new(VersionCheckState.Unavailable, null, "Steam'in son 100 resmi duyurusunda karşılaştırılabilir yayımlanmış sürüm bulunamadı.", $"https://store.steampowered.com/news/app/{appId}");
    }

    public static string? ReleasedVersion(string title)
    {
        if (Excluded().IsMatch(title)) return null;
        var values = Release().Matches(title).Select(m => m.Groups[1].Value).Distinct().ToArray();
        return values.Length == 1 ? values[0] : null;
    }
    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }
    [GeneratedRegex(@"\b(?:patch|update|hotfix|version|v)\s*(?:v(?:ersion)?\s*)?(\d{1,12}(?:\.\d{1,12}){1,4})(?![\d.])", RegexOptions.IgnoreCase)]
    private static partial Regex Release();
    [GeneratedRegex(@"\b(?:beta|alpha|preview|experimental|test|upcoming|coming|soon|tomorrow|planned|scheduled|will|overview|announcing|mac|linux|xbox|playstation|switch|dlc)\b|\d+(?:\.\d+)+\s*[-+](?:beta|rc|alpha)", RegexOptions.IgnoreCase)]
    private static partial Regex Excluded();
}

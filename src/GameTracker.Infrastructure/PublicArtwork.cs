using System.Net.Http.Json;
using System.Text.Json;
using GameTracker.Core;

namespace GameTracker.Infrastructure;

// Anonymous storefront/web search, independent of where the game was installed.
// These public website endpoints are not guaranteed APIs; failures must stay optional.
public sealed class PublicArtwork(HttpClient http)
{
    private sealed record CatalogGame(string Id, string Name);
    public async Task<IReadOnlyList<CoverMatch>> Steam(string title, CancellationToken ct)
    {
        title = TitleMatcher.ArtworkTitle(title);
        using var response = await http.GetAsync("https://store.steampowered.com/api/storesearch/?l=english&cc=us&term=" + Uri.EscapeDataString(TitleMatcher.Clean(title)), ct);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var games = json.RootElement.GetProperty("items").EnumerateArray()
            .Where(x => x.GetProperty("type").GetString() == "app")
            .Select(x => new CatalogGame(x.GetProperty("id").ToString(), x.GetProperty("name").GetString() ?? "")).ToArray();
        var game = TitleMatcher.Exact(title, games, x => x.Name);
        if (game is null) return [];
        return new[] { "library_600x900.jpg", "header.jpg" }.Select(file => new CoverMatch("Steam", game.Id, game.Name,
            $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{game.Id}/{file}", $"https://store.steampowered.com/app/{game.Id}/")).ToArray();
    }

    public async Task<IReadOnlyList<CoverMatch>> SteamGrid(string title, CancellationToken ct)
    {
        title = TitleMatcher.ArtworkTitle(title);
        using var response = await http.GetAsync("https://www.steamgriddb.com/api/public/search/autocomplete?term=" + Uri.EscapeDataString(TitleMatcher.Clean(title)), ct);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!json.RootElement.GetProperty("success").GetBoolean()) throw new IOException("SteamGridDB araması tamamlanamadı.");
        var games = json.RootElement.GetProperty("data").EnumerateArray()
            .Select(x => new CatalogGame(x.GetProperty("id").ToString(), x.GetProperty("name").GetString() ?? "")).ToArray();
        var game = TitleMatcher.Exact(title, games, x => x.Name);
        if (game is null || !int.TryParse(game.Id, out var id)) return [];
        using var assets = await http.PostAsJsonAsync("https://www.steamgriddb.com/api/public/search/assets", new
        {
            asset_type = "grid", game_id = new[] { id }, dimensions = new[] { "600x900" },
            styles = new[] { "all" }, languages = new[] { "all" }, formats = new[] { "image/jpeg", "image/png" },
            order = "score_desc", @static = true, animated = false, nsfw = false, epilepsy = false,
            humor = false, untagged = true, page = 0, limit = 10
        }, ct);
        assets.EnsureSuccessStatusCode();
        using var data = JsonDocument.Parse(await assets.Content.ReadAsStringAsync(ct));
        if (!data.RootElement.GetProperty("success").GetBoolean()) throw new IOException("SteamGridDB görselleri alınamadı.");
        var result = data.RootElement.GetProperty("data");
        // Verify the asset response still belongs to the unique matched game.
        if (result.GetProperty("game").GetProperty("id").ToString() != game.Id) return [];
        return result.GetProperty("assets").EnumerateArray().Where(x =>
            !Flag(x, "nsfw") && !Flag(x, "humor") && !Flag(x, "is_animated") && !Flag(x, "is_deleted") && !Flag(x, "processing")
            && x.GetProperty("width").GetInt32() == 600 && x.GetProperty("height").GetInt32() == 900)
            .Select(x => new CoverMatch("SteamGridDB", game.Id, game.Name, x.GetProperty("url").GetString() ?? "", "https://www.steamgriddb.com/game/" + game.Id)).ToArray();
    }
    private static bool Flag(JsonElement item, string key) => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;

    public static bool IsAllowedImage(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo)
        && new[] { "cdn2.steamgriddb.com", "cdn.steamgriddb.com", "images.igdb.com", "media.rawg.io", "shared.fastly.steamstatic.com", "shared.akamai.steamstatic.com", "cdn.akamai.steamstatic.com", "cdn.cloudflare.steamstatic.com", "encrypted-tbn0.gstatic.com", "encrypted-tbn1.gstatic.com", "encrypted-tbn2.gstatic.com", "encrypted-tbn3.gstatic.com" }.Contains(uri.Host.ToLowerInvariant());
}

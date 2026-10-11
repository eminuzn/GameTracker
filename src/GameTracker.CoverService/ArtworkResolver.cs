using System.Net.Http.Headers;
using System.Text.Json;
using System.Collections.Concurrent;
using GameTracker.Core;
using GameTracker.Infrastructure;

namespace GameTracker.CoverService;

public sealed class ArtworkResolver : IDisposable
{
    private sealed record Entry(CoverResponse Response, DateTimeOffset Expires);
    private sealed record CatalogGame(string Id, string Name);
    private readonly HttpClient http;
    private readonly IConfiguration config;
    private readonly ConcurrentDictionary<string, Entry> cache = new();
    private readonly SemaphoreSlim gate = new(1);
    private List<CatalogGame> steamCatalog = [];
    private DateTimeOffset catalogExpires;
    private string? twitchToken;
    private DateTimeOffset tokenExpires;
    public ArtworkResolver(IConfiguration configuration) : this(configuration, new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(12) }) { }
    public ArtworkResolver(IConfiguration configuration, HttpClient client) { config = configuration; http = client; }
    private string? Secret(string name) => config[name];
    public async Task<CoverResponse> Resolve(string title, CancellationToken ct, string? after = null)
    {
        title = TitleMatcher.ArtworkTitle(title); var key = TitleMatcher.Key(title);
        if (key.Length < 2) return new(null);
        key += ":" + after;
        if (cache.TryGetValue(key, out var hit) && hit.Expires > DateTimeOffset.UtcNow) return hit.Response;
        await gate.WaitAsync(ct);
        try
        {
            if (cache.TryGetValue(key, out hit) && hit.Expires > DateTimeOffset.UtcNow) return hit.Response;
            bool failed = false;
            var providers = new (string Name, bool Enabled, Func<string, CancellationToken, Task<CoverMatch?>> Resolve)[]
            {
                ("Steam", true, Steam),
                ("SteamGridDB", true, SteamGrid),
                ("IGDB", !string.IsNullOrEmpty(Secret("IGDB_CLIENT_ID")) && !string.IsNullOrEmpty(Secret("IGDB_CLIENT_SECRET")), Igdb),
                ("RAWG", !string.IsNullOrEmpty(Secret("RAWG_API_KEY")), Rawg),
                ("GoogleShopping", !string.IsNullOrEmpty(Secret("SERPAPI_API_KEY")), GoogleShopping)
            };
            var start = after is null ? 0 : Array.FindIndex(providers, p => p.Name == after) + 1;
            if (after is not null && start == 0) throw new ArgumentException("Unknown provider", nameof(after));
            foreach (var (_, enabled, resolve) in providers.Skip(start))
            {
                if (!enabled) continue;
                try
                {
                    if (await resolve(title, ct) is { } match && (match.Provider == "Steam" || await HasImage(match.ImageUrl, ct)))
                    {
                        var found = new CoverResponse(match); Put(key, found, TimeSpan.FromDays(1)); return found;
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or KeyNotFoundException || ex is TaskCanceledException && !ct.IsCancellationRequested)
                { failed = true; /* Do not log request URLs containing provider credentials. */ }
            }
            var result = new CoverResponse(null, failed, failed ? "ProviderUnavailable" : "NotFound");
            Put(key, result, result.Retryable ? TimeSpan.FromMinutes(5) : TimeSpan.FromDays(1)); return result;
        }
        finally { gate.Release(); }
    }
    private void Put(string key, CoverResponse response, TimeSpan duration)
    {
        if (cache.Count > 5000) cache.Clear(); cache[key] = new(response, DateTimeOffset.UtcNow.Add(duration));
    }
    private async Task<JsonDocument> GetJson(string url, CancellationToken ct, string? bearer = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var response = await http.SendAsync(request, ct); response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }
    private async Task<CoverMatch?> Steam(string title, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(Secret("STEAM_API_KEY")))
        {
            foreach (var image in await new PublicArtwork(http).Steam(title, ct))
                if (await HasImage(image.ImageUrl, ct)) return image;
            return null;
        }
        if (catalogExpires < DateTimeOffset.UtcNow)
        {
            var catalog = new List<CatalogGame>(); int last = 0;
            for (var page = 0; page < 20; page++)
            {
                var input = Uri.EscapeDataString(JsonSerializer.Serialize(new { include_games = true, include_dlc = false, include_software = false, max_results = 50000, last_appid = last }));
                using var json = await GetJson($"https://api.steampowered.com/IStoreService/GetAppList/v1/?key={Uri.EscapeDataString(Secret("STEAM_API_KEY")!)}&input_json={input}", ct);
                var response = json.RootElement.GetProperty("response");
                var apps = response.GetProperty("apps");
                foreach (var app in apps.EnumerateArray()) catalog.Add(new(app.GetProperty("appid").ToString(), app.GetProperty("name").GetString() ?? ""));
                if (!response.TryGetProperty("have_more_results", out var more) || !more.GetBoolean()) break;
                var next = response.GetProperty("last_appid").GetInt32(); if (next <= last) throw new InvalidOperationException("Catalog pagination"); last = next;
            }
            steamCatalog = catalog; catalogExpires = DateTimeOffset.UtcNow.AddDays(1);
        }
        var match = TitleMatcher.Exact(title, steamCatalog, x => x.Name);
        if (match is null) return null;
        // Store CDN convention is isolated here; 404 falls through to other providers.
        var url = $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{match.Id}/library_600x900.jpg";
        if (await HasImage(url, ct)) return new("Steam", match.Id, match.Name, url, $"https://store.steampowered.com/app/{match.Id}/");
        return null;
    }
    private async Task<bool> HasImage(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !PublicArtwork.IsAllowedImage(uri)) return false;
        using var request = new HttpRequestMessage(HttpMethod.Head, url);
        request.Headers.Accept.ParseAdd("image/jpeg,image/png");
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode is System.Net.HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500) throw new HttpRequestException("Artwork provider temporarily unavailable");
        return response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType is "image/jpeg" or "image/png";
    }
    private async Task<CoverMatch?> SteamGrid(string title, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(Secret("STEAMGRIDDB_API_KEY")))
        {
            foreach (var image in await new PublicArtwork(http).SteamGrid(title, ct))
                if (await HasImage(image.ImageUrl, ct)) return image;
            return null;
        }
        var token = Secret("STEAMGRIDDB_API_KEY")!;
        using var json = await GetJson("https://www.steamgriddb.com/api/v2/search/autocomplete/" + Uri.EscapeDataString(title), ct, token);
        var candidates = json.RootElement.GetProperty("data").EnumerateArray().Select(x => new CatalogGame(x.GetProperty("id").ToString(), x.GetProperty("name").GetString() ?? "")).ToList();
        var game = TitleMatcher.Exact(title, candidates, x => x.Name); if (game is null) return null;
        using var grids = await GetJson($"https://www.steamgriddb.com/api/v2/grids/game/{game.Id}?dimensions=600x900&types=static&mimes=image/jpeg,image/png&nsfw=false&humor=false", ct, token);
        var images = grids.RootElement.GetProperty("data").EnumerateArray().OrderByDescending(x => x.TryGetProperty("score", out var score) ? score.GetDouble() : 0).ToArray();
        foreach (var image in images)
            if (image.TryGetProperty("url", out var url) && url.GetString() is { } link && await HasImage(link, ct))
                return new("SteamGridDB", game.Id, game.Name, link, "https://www.steamgriddb.com/game/" + game.Id);
        return null;
    }
    private async Task<CoverMatch?> Igdb(string title, CancellationToken ct)
    {
        if (twitchToken is null || tokenExpires < DateTimeOffset.UtcNow)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://id.twitch.tv/oauth2/token")
            { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["client_id"] = Secret("IGDB_CLIENT_ID")!, ["client_secret"] = Secret("IGDB_CLIENT_SECRET")!, ["grant_type"] = "client_credentials" }) };
            using var response = await http.SendAsync(request, ct); response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            twitchToken = json.RootElement.GetProperty("access_token").GetString(); tokenExpires = DateTimeOffset.UtcNow.AddSeconds(json.RootElement.GetProperty("expires_in").GetInt32() - 60);
        }
        using var query = new HttpRequestMessage(HttpMethod.Post, "https://api.igdb.com/v4/games");
        query.Headers.Add("Client-ID", Secret("IGDB_CLIENT_ID")); query.Headers.Authorization = new AuthenticationHeaderValue("Bearer", twitchToken);
        query.Content = new StringContent($"search {JsonSerializer.Serialize(title)}; fields name,cover.image_id,url; limit 20;");
        using var answer = await http.SendAsync(query, ct); answer.EnsureSuccessStatusCode();
        using var data = JsonDocument.Parse(await answer.Content.ReadAsStringAsync(ct));
        var matches = data.RootElement.EnumerateArray().Where(x => TitleMatcher.Key(x.GetProperty("name").GetString() ?? "") == TitleMatcher.Key(title)).ToArray();
        if (matches.Length != 1 || !matches[0].TryGetProperty("cover", out var cover) || !cover.TryGetProperty("image_id", out var image)) return null;
        var game = matches[0];
        return new("IGDB", game.GetProperty("id").ToString(), game.GetProperty("name").GetString()!, $"https://images.igdb.com/igdb/image/upload/t_cover_big/{image.GetString()}.jpg", game.TryGetProperty("url", out var page) ? page.GetString() ?? "https://www.igdb.com" : "https://www.igdb.com");
    }
    private async Task<CoverMatch?> Rawg(string title, CancellationToken ct)
    {
        using var json = await GetJson($"https://api.rawg.io/api/games?key={Uri.EscapeDataString(Secret("RAWG_API_KEY")!)}&search={Uri.EscapeDataString(title)}&search_exact=true&page_size=20", ct);
        var matches = json.RootElement.GetProperty("results").EnumerateArray().Where(x => TitleMatcher.Key(x.GetProperty("name").GetString() ?? "") == TitleMatcher.Key(title)).ToArray();
        if (matches.Length != 1 || !matches[0].TryGetProperty("background_image", out var image) || image.GetString() is not { } url) return null;
        var game = matches[0];
        // RAWG may provide landscape art; WPF crops it consistently to the card frame.
        return new("RAWG", game.GetProperty("id").ToString(), game.GetProperty("name").GetString()!, url, "https://rawg.io/games/" + game.GetProperty("slug").GetString());
    }
    private async Task<CoverMatch?> GoogleShopping(string title, CancellationToken ct)
    {
        using var json = await GetJson("https://serpapi.com/search.json?engine=google_shopping&hl=en&gl=us&q="
            + Uri.EscapeDataString(title) + "&api_key=" + Uri.EscapeDataString(Secret("SERPAPI_API_KEY")!), ct);
        if (json.RootElement.TryGetProperty("error", out _)) throw new IOException("Shopping search unavailable");
        foreach (var product in ShoppingProducts(json.RootElement))
        {
            if (!product.TryGetProperty("title", out var productTitle) || !ShoppingMatcher.Matches(title, productTitle.GetString() ?? "")) continue;
            var images = new List<string>();
            if (product.TryGetProperty("thumbnails", out var thumbnails) && thumbnails.ValueKind == JsonValueKind.Array)
                images.AddRange(thumbnails.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!));
            if (product.TryGetProperty("thumbnail", out var thumbnail) && thumbnail.ValueKind == JsonValueKind.String) images.Add(thumbnail.GetString()!);
            foreach (var image in images.Distinct())
            {
                if (!Uri.TryCreate(image, UriKind.Absolute, out var uri) || !IsGoogleThumbnail(uri) || !await HasImage(image, ct)) continue;
                var id = product.TryGetProperty("product_id", out var productId) ? productId.ToString() : "shopping";
                return new("GoogleShopping", id, title, image, ShoppingMatcher.SearchUrl(title));
            }
        }
        return null;
    }
    private static IEnumerable<JsonElement> ShoppingProducts(JsonElement root)
    {
        if (root.TryGetProperty("shopping_results", out var products) && products.ValueKind == JsonValueKind.Array)
            foreach (var product in products.EnumerateArray()) yield return product;
        if (root.TryGetProperty("categorized_shopping_results", out var categories) && categories.ValueKind == JsonValueKind.Array)
            foreach (var category in categories.EnumerateArray())
                if (category.TryGetProperty("shopping_results", out products) && products.ValueKind == JsonValueKind.Array)
                    foreach (var product in products.EnumerateArray()) yield return product;
    }
    private static bool IsGoogleThumbnail(Uri uri) => PublicArtwork.IsAllowedImage(uri)
        && uri.AbsolutePath == "/shopping" && new[] { "encrypted-tbn0.gstatic.com", "encrypted-tbn1.gstatic.com", "encrypted-tbn2.gstatic.com", "encrypted-tbn3.gstatic.com" }.Contains(uri.Host);
    public void Dispose() { http.Dispose(); gate.Dispose(); }
}

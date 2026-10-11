using System.Text.RegularExpressions;

namespace GameTracker.Core;

public static partial class ShoppingMatcher
{
    private static readonly HashSet<string> MarketWords = new(StringComparer.Ordinal)
    { "pc", "windows", "steam", "epic", "xbox", "ps", "playstation", "nintendo", "switch", "series", "x", "s", "one", "digital", "download", "key", "code", "cd", "dvd", "game", "oyun", "oyunu", "rockstar", "games", "global", "new", "used", "eu", "tr", "us", "uk", "standard", "edition", "microsoft", "sony", "for" };
    private static readonly HashSet<string> NonGames = new(StringComparer.Ordinal)
    { "poster", "guide", "walkthrough", "soundtrack", "dlc", "shark", "account", "hesap", "figurine", "figure", "tshirt", "shirt", "artbook", "manual", "expansion", "bundle", "collection", "currency", "coins" };
    private static string[] Tokens(string title)
    {
        var text = TitleMatcher.Clean(title);
        text = Gta().Replace(text, m => TitleMatcher.ArtworkTitle(m.Value));
        return Words().Matches(text).Select(m => TitleMatcher.Key(m.Value)).ToArray();
    }
    public static bool Matches(string gameTitle, string productTitle)
    {
        var expected = Tokens(TitleMatcher.ArtworkTitle(gameTitle));
        var actual = Tokens(productTitle);
        if (expected.Length == 0 || actual.Any(NonGames.Contains)) return false;
        for (var start = 0; start <= actual.Length - expected.Length; start++)
        {
            if (!actual.AsSpan(start, expected.Length).SequenceEqual(expected)) continue;
            // Only known retail/platform labels may surround the exact game title.
            // This rejects another sequel, Enhanced/Legacy mismatch and merch.
            bool RetailLabel(int i) => MarketWords.Contains(actual[i])
                || i > 0 && actual[i].All(char.IsDigit) && actual[i - 1] is "ps" or "playstation";
            if (Enumerable.Range(0, start).All(RetailLabel)
                && Enumerable.Range(start + expected.Length, actual.Length - start - expected.Length).All(RetailLabel)) return true;
        }
        return false;
    }
    public static string SearchUrl(string title) => "https://www.google.com/search?udm=28&q=" + Uri.EscapeDataString(TitleMatcher.ArtworkTitle(title));
    [GeneratedRegex(@"\bGTA\s*(?:III|IV|V|3|4|5)(?:\s+(?:Enhanced|Legacy))?\b", RegexOptions.IgnoreCase)]
    private static partial Regex Gta();
    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Words();
}

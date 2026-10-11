using System.Text;
using System.Text.RegularExpressions;

namespace GameTracker.Core;

public static partial class TitleMatcher
{
    public static string Clean(string value)
    {
        value = Version().Replace(value, " ");
        value = ReleaseTag().Replace(value, " ");
        value = WordBoundary().Replace(value, " ");
        return Spaces().Replace(value.Replace('.', ' ').Replace('_', ' ').Replace('-', ' '), " ").Trim();
    }
    public static string Key(string title)
    {
        var normalized = Clean(title).Normalize(NormalizationForm.FormD);
        return string.Concat(normalized.Where(char.IsLetterOrDigit)).ToLowerInvariant();
    }
    public static string ArtworkTitle(string title)
    {
        var cleaned = Clean(title);
        // Explicit aliases preserve sequels and editions. Keep filename scoring
        // separate: changing GTA5 to the full title must not alter exe discovery.
        var gta = GtaAlias().Match(cleaned);
        if (!gta.Success) return cleaned;
        var number = gta.Groups["number"].Value.ToUpperInvariant() switch
        { "3" => "III", "4" => "IV", "5" => "V", var roman => roman };
        var edition = gta.Groups["edition"].Value;
        return "Grand Theft Auto " + number + (edition.Length == 0 ? "" : " " + char.ToUpperInvariant(edition[0]) + edition[1..].ToLowerInvariant());
    }
    public static string ArtworkKey(string title) => Key(ArtworkTitle(title));
    public static T? Exact<T>(string title, IEnumerable<T> candidates, Func<T, string> name) where T : class
    {
        var matches = candidates.Where(x => Key(name(x)) == Key(title)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
    [GeneratedRegex(@"(?i)\b(?:v(?:ersion)?\s*\d+(?:\.\d+)*|build\s*\d+)\b")] private static partial Regex Version();
    [GeneratedRegex(@"(?i)\b(?:fitgirl|dodi|repack|gog|codex|rune|elamigos|tenoke|portable)\b")] private static partial Regex ReleaseTag();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
    // Keep acronyms intact (GTA), while splitting AlanWake2 and Cyberpunk2077.
    [GeneratedRegex(@"(?<=[\p{Ll}])(?=[\p{Lu}])|(?<=[\p{Lu}])(?=[\p{Lu}][\p{Ll}])|(?<=\p{L})(?=\d)|(?<=\d)(?=\p{L})")]
    private static partial Regex WordBoundary();
    [GeneratedRegex(@"^(?:GTA\s*|Grand\s+Theft\s+Auto\s+)(?<number>III|IV|V|3|4|5)(?:\s+(?<edition>Enhanced|Legacy))?$", RegexOptions.IgnoreCase)]
    private static partial Regex GtaAlias();
}

using System.Collections;

namespace GameTracker.Core;

public enum GameSortOrder { Name, LastPlayed, Size }
public enum LibraryLayout { List, Grid }

public sealed class LibraryViewSettings
{
    public GameSortOrder SortBy { get; set; } = GameSortOrder.Name;
    public bool Descending { get; set; }
    public bool FavoritesOnly { get; set; }
    public bool ExeSelectionOnly { get; set; }
    public LibraryLayout Layout { get; set; } = LibraryLayout.List;
    public int GridColumns { get; set; } = 3;
}

public sealed class GameListComparer(GameSortOrder sortBy, bool descending) : IComparer, IComparer<Game>
{
    public int Compare(object? x, object? y) => Compare(x as Game, y as Game);
    public int Compare(Game? x, Game? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return 1;
        if (y is null) return -1;
        // Unplayed games stay last in both directions; absence is not an old date.
        if (sortBy == GameSortOrder.LastPlayed && x.LastPlayedAt.HasValue != y.LastPlayedAt.HasValue)
            return x.LastPlayedAt.HasValue ? -1 : 1;
        var comparison = sortBy switch
        {
            GameSortOrder.LastPlayed => Nullable.Compare(x.LastPlayedAt, y.LastPlayedAt),
            GameSortOrder.Size => x.SizeBytes.CompareTo(y.SizeBytes),
            _ => StringComparer.CurrentCultureIgnoreCase.Compare(x.Name, y.Name)
        };
        if (comparison != 0) return descending ? -Math.Sign(comparison) : Math.Sign(comparison);
        comparison = StringComparer.CurrentCultureIgnoreCase.Compare(x.Name, y.Name);
        return comparison != 0 ? comparison : x.Id.CompareTo(y.Id);
    }
    // Suppress leftover folders without marking their records deleted. A later
    // scan can show them again when game files or a launch executable return.
    public static bool IsVisible(Game game) => !game.Hidden && !game.Deleted
        && (!game.NeedsExeSelection || game.SizeBytes >= 50_000_000);
    public static bool Matches(Game game, string? search, bool favoritesOnly, bool exeSelectionOnly = false) => IsVisible(game)
        && (!favoritesOnly || game.IsFavorite)
        && (!exeSelectionOnly || game.NeedsExeSelection)
        && (string.IsNullOrWhiteSpace(search) || game.Name.Contains(search, StringComparison.OrdinalIgnoreCase));
}

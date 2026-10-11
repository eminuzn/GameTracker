using GameTracker.Core;

namespace GameTracker.Infrastructure;

public sealed class InitialPlayHistory(LocalStore store,
    Func<IReadOnlyList<string>, DateTimeOffset, CancellationToken, DateTimeOffset?>? lookup = null)
{
    public async Task ImportAsync(Game game, CancellationToken ct)
    {
        // Existing records default to false. Only discovery opts new games in.
        if (!game.InitialPlayHistoryPending) return;
        game.InitialPlayHistoryPending = false;
        store.SaveGame(game); // Commit the one-time attempt before reading Windows.
        if (HasPlayHistory(game)) return;
        var paths = new List<string>();
        foreach (var relative in game.Candidates.Where(c => !c.IsHelper).Select(c => c.RelativePath)
            .Concat(game.RuntimeExes).Append(game.LaunchExe).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var path = PathPolicy.ResolveExe(game, relative!);
                if (!GameScanner.IsTool(path)) paths.Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        if (paths.Count == 0) return;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var date = await Task.Run(() => (lookup ?? WindowsPrefetch.FindLatest)(paths, now, ct), ct);
            // The process monitor may record an actual session while IO is pending.
            if (date is { } value && value >= DateTimeOffset.UnixEpoch && value <= now && !HasPlayHistory(game))
            {
                game.LastPlayedAt = value; game.LastPlayedFromPrefetch = true;
                store.SaveGame(game); game.Refresh();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { store.Log("İlk oynama geçmişi (Prefetch)", ex); }
    }

    private bool HasPlayHistory(Game game) => game.LastPlayedAt.HasValue || game.IsRunning
        || game.PlayedMilliseconds > 0 || store.Sessions(game.Id).Count > 0;
}

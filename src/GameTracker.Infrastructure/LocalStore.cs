using System.Text.Json;
using GameTracker.Core;
using Microsoft.Data.Sqlite;

namespace GameTracker.Infrastructure;

public sealed class LocalStore
{
    public string DataDirectory { get; }
    private readonly string connectionString;
    public LocalStore(string? directory = null)
    {
        DataDirectory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameTracker");
        Directory.CreateDirectory(DataDirectory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(DataDirectory, "gametracker.db") }.ToString();
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);
            INSERT INTO schema_version SELECT 1 WHERE NOT EXISTS (SELECT 1 FROM schema_version);
            CREATE TABLE IF NOT EXISTS roots (id TEXT PRIMARY KEY, path TEXT NOT NULL UNIQUE COLLATE NOCASE, payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS games (id TEXT PRIMARY KEY, root_id TEXT NOT NULL REFERENCES roots(id), path TEXT NOT NULL UNIQUE COLLATE NOCASE, payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS sessions (id TEXT PRIMARY KEY, game_id TEXT NOT NULL REFERENCES games(id), started TEXT NOT NULL, status TEXT NOT NULL, payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS preferences (key TEXT PRIMARY KEY, payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS sessions_game ON sessions(game_id, started);
            """;
        command.ExecuteNonQuery();
        // A previous process cannot have kept tracking after it terminated.
        foreach (var session in ReadAll<PlaySession>("sessions").Where(x => x.Status == "Running"))
        {
            session.Status = "Interrupted"; session.EndedAt = session.LastSeenAt; SaveSession(session);
        }
        var totals = ReadAll<PlaySession>("sessions").GroupBy(x => x.GameId).ToDictionary(x => x.Key, x => x.Sum(s => s.DurationMs));
        foreach (var game in Games()) { game.PlayedMilliseconds = totals.GetValueOrDefault(game.Id); SaveGame(game); }
    }
    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString); db.Open();
        using var cmd = db.CreateCommand(); cmd.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;"; cmd.ExecuteNonQuery();
        return db;
    }
    private List<T> ReadAll<T>(string table)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT payload FROM " + table;
        using var reader = cmd.ExecuteReader(); var list = new List<T>();
        while (reader.Read()) { var item = JsonSerializer.Deserialize<T>(reader.GetString(0)); if (item is not null) list.Add(item); }
        return list;
    }
    public List<Game> Games() => ReadAll<Game>("games");
    public List<LibraryRoot> Roots() => ReadAll<LibraryRoot>("roots");
    public LibraryViewSettings LoadViewSettings()
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT payload FROM preferences WHERE key='library_view'";
        if (cmd.ExecuteScalar() is not string payload) return new();
        try
        {
            var settings = JsonSerializer.Deserialize<LibraryViewSettings>(payload) ?? new();
            if (!Enum.IsDefined(settings.SortBy)) settings.SortBy = GameSortOrder.Name;
            if (!Enum.IsDefined(settings.Layout)) settings.Layout = LibraryLayout.List;
            if (settings.GridColumns is not (3 or 4)) settings.GridColumns = 3;
            return settings;
        }
        catch (JsonException ex) { Log("Liste ayarları", ex); return new(); }
    }
    public void SaveViewSettings(LibraryViewSettings settings)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO preferences VALUES ('library_view',$payload) ON CONFLICT(key) DO UPDATE SET payload=$payload";
        cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(settings)); cmd.ExecuteNonQuery();
    }
    public List<PlaySession> Sessions(Guid gameId) => ReadAll<PlaySession>("sessions").Where(x => x.GameId == gameId).OrderByDescending(x => x.StartedAt).Take(20).ToList();
    public void SaveRoot(LibraryRoot root)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO roots VALUES ($id,$path,$payload) ON CONFLICT(id) DO UPDATE SET path=$path,payload=$payload";
        cmd.Parameters.AddWithValue("$id", root.Id.ToString()); cmd.Parameters.AddWithValue("$path", root.Path); cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(root)); cmd.ExecuteNonQuery();
    }
    public void SaveGame(Game game)
    {
        using var db = Open(); SaveGame(db, null, game);
    }
    private static void SaveGame(SqliteConnection db, SqliteTransaction? tx, Game game)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO games VALUES ($id,$root,$path,$payload) ON CONFLICT(id) DO UPDATE SET root_id=$root,path=$path,payload=$payload";
        cmd.Parameters.AddWithValue("$id", game.Id.ToString()); cmd.Parameters.AddWithValue("$root", game.RootId.ToString()); cmd.Parameters.AddWithValue("$path", game.Deleted ? game.FolderPath + "|archived|" + game.Id : game.FolderPath); cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(game)); cmd.ExecuteNonQuery();
    }
    public void SaveSession(PlaySession session, Game? game = null)
    {
        using var db = Open(); using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO sessions VALUES ($id,$game,$started,$status,$payload) ON CONFLICT(id) DO UPDATE SET status=$status,payload=$payload";
        cmd.Parameters.AddWithValue("$id", session.Id.ToString()); cmd.Parameters.AddWithValue("$game", session.GameId.ToString()); cmd.Parameters.AddWithValue("$started", session.StartedAt.ToString("O")); cmd.Parameters.AddWithValue("$status", session.Status); cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(session)); cmd.ExecuteNonQuery();
        if (game is not null) SaveGame(db, tx, game);
        tx.Commit();
    }
    public void Log(string context, Exception ex)
    {
        try
        {
            var file = Path.Combine(DataDirectory, "errors.log");
            if (File.Exists(file) && new FileInfo(file).Length > 2_000_000) File.Move(file, file + ".old", true);
            File.AppendAllText(file, $"{DateTimeOffset.Now:O} {context}: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}");
        }
        catch (IOException) { }
    }
}

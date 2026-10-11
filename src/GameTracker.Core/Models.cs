using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace GameTracker.Core;

public sealed class LibraryRoot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Path { get; set; } = "";
}

public sealed record ExeCandidate(string RelativePath, int Score, string Reason, bool IsHelper = false);

public sealed class Game : INotifyPropertyChanged
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RootId { get; set; }
    public string RootPath { get; set; } = "";
    public string FolderPath { get; set; } = "";
    public string? RootIdentity { get; set; }
    public string? FolderIdentity { get; set; }
    public string Name { get; set; } = "";
    public string? LaunchExe { get; set; }
    public string? WorkingDirectory { get; set; }
    public string Arguments { get; set; } = "";
    public bool ManualExe { get; set; }
    public List<string> RuntimeExes { get; set; } = [];
    public List<ExeCandidate> Candidates { get; set; } = [];
    public long SizeBytes { get; set; }
    public int SkippedEntries { get; set; }
    public DateTimeOffset? SizeUpdatedAt { get; set; }
    public DateTimeOffset? LastPlayedAt { get; set; }
    public bool InitialPlayHistoryPending { get; set; }
    public bool LastPlayedFromPrefetch { get; set; }
    public long PlayedMilliseconds { get; set; }
    public bool Hidden { get; set; }
    public bool IsFavorite { get; set; }
    public bool Deleted { get; set; }
    public bool Available { get; set; } = true;
    public string? CoverPath { get; set; }
    public bool ManualCover { get; set; }
    public string? CoverProvider { get; set; }
    public string? CoverAttribution { get; set; }
    public DateTimeOffset? CoverRetryAt { get; set; }
    public string? CoverLookupKey { get; set; }
    public string CoverStatus { get; set; } = "Kapak bekleniyor";
    [JsonIgnore] public bool IsRunning { get; set; }
    [JsonIgnore] public bool NeedsExeSelection => string.IsNullOrWhiteSpace(LaunchExe);
    [JsonIgnore] public string FavoriteGlyph => IsFavorite ? "★" : "☆";
    [JsonIgnore] public string FavoriteActionText => IsFavorite ? "Favorilerden çıkar" : "Favorilere ekle";
    [JsonIgnore] public string SizeText => $"{(SkippedEntries > 0 ? "En az " : "")}{SizeBytes / 1e9:N1} GB";
    [JsonIgnore] public string LastPlayedText => LastPlayedAt?.ToLocalTime().ToString("dd.MM.yyyy HH:mm") ?? "Henüz takip edilmedi";
    [JsonIgnore] public string TimeText => $"{TimeSpan.FromMilliseconds(PlayedMilliseconds).TotalHours:N1} saat";
    [JsonIgnore] public string StatusText => Deleted ? "Silindi" : !Available ? "Klasör erişilemiyor" : IsRunning ? "Şimdi çalışıyor" : NeedsExeSelection ? "Exe seçilmeli" : "Oynamaya hazır";
    [JsonIgnore] public string Initials => string.Concat(Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(x => x[0])).ToUpperInvariant();
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}

public sealed class PlaySession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GameId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public long DurationMs { get; set; }
    public string Status { get; set; } = "Running";
}

public sealed record CoverMatch(string Provider, string GameId, string Title, string ImageUrl, string Attribution);
public sealed record CoverResponse(CoverMatch? Match, bool Retryable = false, string? Message = null);

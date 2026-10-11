using System.Net;
using System.Text;
using GameTracker.Core;
using GameTracker.Infrastructure;
using GameTracker.CoverService;
using Microsoft.Extensions.Configuration;
using System.Buffers.Binary;

// Optional live, read-only Steam check; never writes to a library or database.
if (args is ["--check-version", var versionTitle, var localVersion])
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("GameTracker/1.0");
    var result = await new SteamVersionClient(http).CheckAsync(versionTitle, localVersion, LocalVersionKind.Release, default);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    return;
}

// Explicit, read-only diagnostics for a real game. Database and covers go to a
// separate temp directory; no launch, deletion or writes to the user's library.
if (args is ["--inspect-game", var realFolder])
{
    var diagnosticDirectory = Path.Combine(Path.GetTempPath(), "GameTracker-diagnostic-" + Guid.NewGuid());
    var diagnosticStore = new LocalStore(diagnosticDirectory);
    var diagnosticRoot = new LibraryRoot { Path = Path.GetDirectoryName(Path.GetFullPath(realFolder))! };
    diagnosticStore.SaveRoot(diagnosticRoot);
    var diagnosticGame = new Game { RootId = diagnosticRoot.Id, RootPath = diagnosticRoot.Path, FolderPath = realFolder, Name = TitleMatcher.Clean(Path.GetFileName(realFolder)), CoverRetryAt = DateTimeOffset.UtcNow.AddDays(7) };
    var scanResult = GameScanner.Scan(realFolder, diagnosticGame.Name, default);
    diagnosticGame.Candidates = scanResult.Candidates; diagnosticGame.SizeBytes = scanResult.Bytes;
    diagnosticGame.LaunchExe = GameScanner.Suggest(scanResult.Candidates)?.RelativePath;
    diagnosticStore.SaveGame(diagnosticGame);
    using var diagnosticClient = new CoverClient(diagnosticStore, new DesktopSettings());
    await diagnosticClient.ResolveAsync(diagnosticGame, default);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { diagnosticGame.Name, diagnosticGame.LaunchExe, diagnosticGame.Candidates, diagnosticGame.SizeBytes, scanResult.Skipped, diagnosticGame.CoverProvider, diagnosticGame.CoverPath, diagnosticGame.CoverStatus, diagnosticGame.CoverRetryAt, DiagnosticDirectory = diagnosticDirectory }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    return;
}

var passed = 0;
void Check(string name, bool result) { if (!result) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); passed++; }
void Throws(string name, Action action) { try { action(); } catch (IOException) { Check(name, true); return; } throw new Exception("Expected IOException: " + name); }
HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
HttpResponseMessage Png() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=")) };
const string publicGame = "{\"success\":true,\"data\":[{\"id\":42,\"name\":\"Demo Game\"}]}";
const string publicAssets = "{\"success\":true,\"data\":{\"game\":{\"id\":42},\"assets\":[{\"width\":600,\"height\":900,\"url\":\"https://cdn2.steamgriddb.com/grid/demo.png\"}]}}";
Check("Title cleanup preserves sequel", TitleMatcher.Clean("Cyberpunk.2077.v2.1-GOG") == "Cyberpunk 2077");
Check("Different sequel does not match", TitleMatcher.Key("Remnant 2") != TitleMatcher.Key("Remnant"));
Check("Edition remains distinct", TitleMatcher.Key("Dead Space Remastered") != TitleMatcher.Key("Dead Space"));
Check("Ambiguous exact titles rejected", TitleMatcher.Exact("Remnant 2", new[] { "Remnant 2", "Remnant 2" }, x => x) is null);
Check("Unique normalized match", TitleMatcher.Exact("Half-Life 2", new[] { "Half Life 2", "Half Life" }, x => x) == "Half Life 2");
Check("Compact folder title split", TitleMatcher.Clean("AlanWake2") == "Alan Wake 2");
Check("Letters and numbers split without altering identity", TitleMatcher.Clean("Cyberpunk2077") == "Cyberpunk 2077" && TitleMatcher.Key("AlanWake2") == TitleMatcher.Key("Alan Wake 2"));
Check("Acronym and following word split", TitleMatcher.Clean("GTAVEnhanced") == "GTAV Enhanced");
Check("GTA abbreviation expands for artwork search", TitleMatcher.ArtworkTitle("GTAVEnhanced") == "Grand Theft Auto V Enhanced");
Check("GTA numeric and Roman aliases share cover identity", TitleMatcher.ArtworkKey("GTA5 Enhanced") == TitleMatcher.ArtworkKey("Grand Theft Auto V Enhanced"));
Check("GTA base title alias supported", TitleMatcher.ArtworkTitle("GTA5") == "Grand Theft Auto V");
Check("GTA edition is preserved", TitleMatcher.ArtworkKey("GTAV Enhanced") != TitleMatcher.ArtworkKey("GTAV Legacy") && TitleMatcher.ArtworkKey("GTA5") != TitleMatcher.ArtworkKey("GTA5 Enhanced"));
Check("GTA different sequel remains distinct", TitleMatcher.ArtworkKey("GTA4") != TitleMatcher.ArtworkKey("GTA5") && TitleMatcher.ArtworkTitle("GTAVI") == "GTAVI");
Check("Cover alias leaves executable identity unchanged", TitleMatcher.Key("GTA5_Enhanced") == "gta5enhanced");
Check("Shopping title accepts platform labels", ShoppingMatcher.Matches("GTAV Enhanced", "Rockstar Games GTA5 Enhanced - Xbox Series X S") && ShoppingMatcher.Matches("GTAV Enhanced", "Grand Theft Auto V Enhanced PS5"));
Check("Shopping keeps game edition distinct", !ShoppingMatcher.Matches("GTAV Enhanced", "Grand Theft Auto V Legacy PC") && !ShoppingMatcher.Matches("GTA5", "Grand Theft Auto V Enhanced PC"));
Check("Shopping rejects sequel and merchandising", !ShoppingMatcher.Matches("Demo Game", "Demo Game 2 PC") && !ShoppingMatcher.Matches("Demo Game", "Demo Game Poster") && !ShoppingMatcher.Matches("Demo Game", "Demo Game Soundtrack"));
Check("Shopping search uses canonical title", ShoppingMatcher.SearchUrl("GTAV Enhanced").EndsWith("Grand%20Theft%20Auto%20V%20Enhanced"));

Check("Version files accept plain and labelled release", GameVersionDetector.ParseText("v2.31") == "2.31" && GameVersionDetector.ParseText("GameVersion=1.2.3\r\nName=Demo") == "1.2.3");
Check("Conflicting version labels stay unknown", GameVersionDetector.ParseText("version=1.2\nversion=1.3") is null);
Check("Arbitrary dates and text are not versions", GameVersionDetector.ParseText("Released 2025-01-01") is null);
Check("Zero placeholder is not a game version", GameVersionDetector.ParseText("0.0.0.0") is null);
Check("Version comparison is numeric", GameVersionDetector.Compare("1.9.0", "1.10.0", LocalVersionKind.Release) < 0);
Check("Version comparison accepts equal and newer", GameVersionDetector.Compare("v2.31", "2.31", LocalVersionKind.Release) == 0 && GameVersionDetector.Compare("2.32", "2.31", LocalVersionKind.Release) > 0);
Check("Version precision is never guessed", GameVersionDetector.Compare("1.0.3095.0", "1.72", LocalVersionKind.Release) is null && GameVersionDetector.Compare("1.2.0", "1.2", LocalVersionKind.ProductMetadata) is null);
Check("Build IDs and file metadata cannot masquerade as release numbers", GameVersionDetector.Compare("123456", "2.31", LocalVersionKind.Build) is null && GameVersionDetector.Compare("1.2", "1.3", LocalVersionKind.FileMetadata) is null);
Check("Pre-release strings are not stable versions", GameVersionDetector.Compare("1.2-beta", "1.3", LocalVersionKind.Release) is null);
Check("Steam released patch and update titles parsed", SteamVersionClient.ReleasedVersion("Patch 2.31") == "2.31" && SteamVersionClient.ReleasedVersion("Update v1.10.2 is live!") == "1.10.2");
Check("Steam preview future platform and ambiguous titles rejected", new[] { "Update 2.3 is coming!", "Patch 1.5 beta", "Update 1.5 Linux", "Patch 1.4 and Update 1.5", "Photo Mode Challenge 3.0", "Hotfix #12" }.All(s => SteamVersionClient.ReleasedVersion(s) is null));
string steamNews = """
{"appnews":{"appid":42,"newsitems":[
{"title":"Update 2.32 is coming!","feedname":"steam_community_announcements","date":1700000005,"url":"https://steamcommunity.com/announcements/preview"},
{"title":"Patch 8.99","feedname":"external_press","date":1700000004,"url":"https://example.com/press"},
{"title":"Patch 2.31","feedname":"steam_community_announcements","date":1700000003,"url":"https://steamcommunity.com/announcements/latest"},
{"title":"Patch 2.30","feedname":"steam_community_announcements","date":1700000002,"url":"https://steamcommunity.com/announcements/older"}]}}
""";
string steamSearch = """{"items":[{"type":"app","id":42,"name":"Demo Game"}]}""";
string steamDetails = """{"42":{"success":true,"data":{"type":"game","name":"Demo Game"}}}""";
var versionCalls = 0;
using (var versionHttp = new HttpClient(new FakeHandler(request =>
{
    versionCalls++;
    return Json(request.RequestUri!.AbsolutePath.Contains("storesearch") ? steamSearch : request.RequestUri.AbsolutePath.Contains("appdetails") ? steamDetails : steamNews);
})))
{
    var checker = new SteamVersionClient(versionHttp);
    var skipped = await checker.CheckAsync("Demo Game", "", LocalVersionKind.Release, default);
    Check("Missing local version makes no HTTP requests", skipped.State == VersionCheckState.None && versionCalls == 0);
    var oldVersion = await checker.CheckAsync("Demo Game", "2.30", LocalVersionKind.Release, default);
    Check("Older local version flags official released patch only", oldVersion.State == VersionCheckState.Outdated && oldVersion.Version == "2.31" && oldVersion.Url == "https://steamcommunity.com/announcements/latest");
    Check("Matching Steam announcement version detected", (await checker.CheckAsync("Demo Game", "2.31", LocalVersionKind.Release, default)).State == VersionCheckState.Matches);
    Check("Newer local version is not marked outdated", (await checker.CheckAsync("Demo Game", "2.32", LocalVersionKind.Release, default)).State == VersionCheckState.Newer);
    Check("Mismatched PE version stays uncomparable", (await checker.CheckAsync("Demo Game", "1.0.3095.0", LocalVersionKind.FileMetadata, default)).State == VersionCheckState.Unavailable);
    steamNews = """{"appnews":{"appid":42,"newsitems":[{"title":"Update 2.32 is coming!","feedname":"steam_community_announcements","date":1700000005,"url":"https://steamcommunity.com/news"}]}}""";
    Check("No released version does not imply current", (await checker.CheckAsync("Demo Game", "2.31", LocalVersionKind.Release, default)).State == VersionCheckState.Unavailable);
    steamSearch = """{"items":[{"type":"app","id":42,"name":"Demo Game"},{"type":"app","id":43,"name":"Demo Game"}]}""";
    var calls = versionCalls;
    Check("Ambiguous Steam game rejected before news request", (await checker.CheckAsync("Demo Game", "2.31", LocalVersionKind.Release, default)).State == VersionCheckState.Unavailable && versionCalls == calls + 1);
    steamSearch = """{"items":[{"type":"app","id":42,"name":"Demo Game"}]}""";
    steamDetails = """{"42":{"success":true,"data":{"type":"dlc","name":"Demo Game"}}}""";
    Check("DLC is not compared with main game version", (await checker.CheckAsync("Demo Game", "2.31", LocalVersionKind.Release, default)).State == VersionCheckState.Unavailable);
}

var recent = new Game { Name = "Zulu", LastPlayedAt = DateTimeOffset.UtcNow, SizeBytes = 11_000_000_000 };
var older = new Game { Name = "Alpha", LastPlayedAt = recent.LastPlayedAt.Value.AddDays(-1), SizeBytes = 2_000_000_000 };
var unplayed = new Game { Name = "Never", SizeBytes = 3_000_000_000 };
List<Game> Sorted(GameSortOrder by, bool descending) { var list = new List<Game> { unplayed, recent, older }; list.Sort(new GameListComparer(by, descending)); return list; }
Check("Recent play sorted newest first", Sorted(GameSortOrder.LastPlayed, true).SequenceEqual(new[] { recent, older, unplayed }));
Check("Recent play reverses without promoting unplayed games", Sorted(GameSortOrder.LastPlayed, false).SequenceEqual(new[] { older, recent, unplayed }));
Check("Size sorted using numeric bytes largest first", Sorted(GameSortOrder.Size, true).SequenceEqual(new[] { recent, unplayed, older }));
Check("Size sorting reverses", Sorted(GameSortOrder.Size, false).SequenceEqual(new[] { older, unplayed, recent }));
Check("Name sorting works in both directions", Sorted(GameSortOrder.Name, false).SequenceEqual(new[] { older, unplayed, recent }) && Sorted(GameSortOrder.Name, true).SequenceEqual(new[] { recent, unplayed, older }));
older.IsFavorite = true;
Check("Favorites filter combines with search", GameListComparer.Matches(older, "ALP", true) && !GameListComparer.Matches(recent, "", true) && !GameListComparer.Matches(older, "Zulu", true));
older.Hidden = true;
Check("Favorites filter respects removed games", !GameListComparer.Matches(older, "", true));
older.Hidden = false; older.Deleted = true;
Check("Favorites filter respects deleted games", !GameListComparer.Matches(older, "", true));
var leftover = new Game { Name = "Leftover", SizeBytes = 49_999_999, IsFavorite = true };
Check("Small folders without launch exe are suppressed even in favorites", !GameListComparer.Matches(leftover, "", false) && !GameListComparer.Matches(leftover, "", true));
leftover.SizeBytes = 50_000_000;
Check("50 MB boundary stays visible with exe warning", GameListComparer.Matches(leftover, "", false) && leftover.NeedsExeSelection);
leftover.SizeBytes = 100; leftover.LaunchExe = "game.exe";
Check("Small playable games remain visible", GameListComparer.Matches(leftover, "", false) && !leftover.NeedsExeSelection);
leftover.LaunchExe = " ";
Check("Blank executable counts as missing", !GameListComparer.IsVisible(leftover) && leftover.NeedsExeSelection);
leftover.LaunchExe = null; leftover.SizeBytes = 80_000_000;
Check("Restored game files become visible without losing favorite or identity", GameListComparer.Matches(leftover, "Left", true) && !leftover.Deleted && !leftover.Hidden);
Check("Exe selection filter combines name and favorite conditions", GameListComparer.Matches(leftover, "Left", true, true) && !GameListComparer.Matches(leftover, "Other", true, true) && !GameListComparer.Matches(recent, "", true, true));
leftover.LaunchExe = "game.exe";
Check("Exe selection filter excludes playable games but normal view keeps them", !GameListComparer.Matches(leftover, "", false, true) && GameListComparer.Matches(leftover, "", false));
leftover.LaunchExe = null; leftover.SizeBytes = 49_999_999;
Check("Exe selection filter still excludes small empty leftovers", !GameListComparer.Matches(leftover, "", false, true));
leftover.SizeBytes = 50_000_000; leftover.Hidden = true;
Check("Exe selection filter respects hidden games", !GameListComparer.Matches(leftover, "", false, true));
leftover.Hidden = false; leftover.Deleted = true;
Check("Exe selection filter respects deleted games", !GameListComparer.Matches(leftover, "", false, true));

byte[] PrefetchBytes(uint version, string executablePath, params DateTimeOffset[] times)
{
    var minimum = version switch { 17 => 152, 23 => 240, 26 => 304, _ => 296 };
    var strings = Encoding.Unicode.GetBytes(executablePath + "\0");
    var data = new byte[minimum + strings.Length];
    void U32(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), value);
    U32(0, version); "SCCA"u8.CopyTo(data.AsSpan(4)); U32(12, (uint)data.Length);
    var name = executablePath.Replace('\\', '/').Split('/').Last(); name = name[..Math.Min(29, name.Length)];
    Encoding.Unicode.GetBytes(name).CopyTo(data, 16);
    U32(100, (uint)minimum); U32(104, (uint)strings.Length); strings.CopyTo(data, minimum);
    for (var i = 0; i < Math.Min(times.Length, version is 17 or 23 ? 1 : 8); i++)
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan((version == 17 ? 120 : 128) + i * 8, 8), times[i].ToFileTime());
    return data;
}
var historicalTime = DateTimeOffset.UtcNow.AddMonths(-3);
foreach (var version in new uint[] { 17, 23, 26, 30, 31 })
    Check($"Prefetch version {version} reads embedded UTC execution", WindowsPrefetch.Parse(PrefetchBytes(version, @"D:\Games\Demo\game.exe", historicalTime), DateTimeOffset.UtcNow)?.LastRunAt == historicalTime);
Check("Prefetch takes latest valid embedded execution", WindowsPrefetch.Parse(PrefetchBytes(31, @"D:\Games\Demo\game.exe", historicalTime.AddDays(-1), historicalTime), DateTimeOffset.UtcNow)?.LastRunAt == historicalTime);
Check("Prefetch rejects future-only execution dates", WindowsPrefetch.Parse(PrefetchBytes(31, @"D:\Games\Demo\game.exe", DateTimeOffset.UtcNow.AddDays(1)), DateTimeOffset.UtcNow) is null);
Check("Prefetch rejects zero timestamps", WindowsPrefetch.Parse(PrefetchBytes(31, @"D:\Games\Demo\game.exe"), DateTimeOffset.UtcNow) is null);
Check("Prefetch long executable names retain full path identity", WindowsPrefetch.Parse(PrefetchBytes(31, @"D:\Games\Demo\VeryLongExecutableNameBeyond29Characters.exe", historicalTime), DateTimeOffset.UtcNow)?.ExecutablePath.EndsWith("Characters.exe") == true);
var damagedPrefetch = PrefetchBytes(31, @"D:\Games\Demo\game.exe", historicalTime); damagedPrefetch[4] = 0;
Check("Prefetch rejects invalid magic", WindowsPrefetch.Parse(damagedPrefetch, DateTimeOffset.UtcNow) is null);
damagedPrefetch = PrefetchBytes(31, @"D:\Games\Demo\game.exe", historicalTime);
BinaryPrimitives.WriteUInt32LittleEndian(damagedPrefetch.AsSpan(100, 4), uint.MaxValue);
Check("Prefetch bounds checks filename offsets", WindowsPrefetch.Parse(damagedPrefetch, DateTimeOffset.UtcNow) is null);
Check("Prefetch rejects unknown layouts", WindowsPrefetch.Parse(PrefetchBytes(99, @"D:\Games\Demo\game.exe", historicalTime), DateTimeOffset.UtcNow) is null);
Check("Prefetch compressed size limit rejects oversized header", WindowsPrefetch.Parse(new byte[] { 77, 65, 77, 4, 255, 255, 255, 127 }, DateTimeOffset.UtcNow) is null);
Check("Prefetch volume matching requires serial and full path", WindowsPrefetch.MatchesVolumePath(@"\VOLUME{01d1217a9c4c6779-8c9f49ec}\GAMES\DEMO\GAME.EXE", @"\Games\Demo\game.exe", 0x8c9f49ec)
    && !WindowsPrefetch.MatchesVolumePath(@"\VOLUME{01d1217a9c4c6779-8c9f49ec}\GAMES\DEMO\GAME.EXE", @"\Games\Other\game.exe", 0x8c9f49ec)
    && !WindowsPrefetch.MatchesVolumePath(@"\VOLUME{01d1217a9c4c6779-8c9f49ec}\GAMES\DEMO\GAME.EXE", @"\Games\Demo\game.exe", 0x12345678));
if (OperatingSystem.IsWindows())
{
    var realPrefetch = WindowsPrefetch.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "CHROME.EXE-B3BA7868.pf")), DateTimeOffset.UtcNow);
    Check("Real Windows 10 compressed Prefetch matches independent timestamp", realPrefetch?.LastRunAt == DateTimeOffset.Parse("2016-01-13T18:06:55.3344577Z") && realPrefetch.ExecutableName == "CHROME.EXE");
}

var temp = Path.Combine(OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : Path.GetTempPath(), "gametracker-tests-" + Guid.NewGuid()); Directory.CreateDirectory(temp);
try
{
    var root = Path.Combine(temp, "Games"); var gameDir = Path.Combine(root, "Demo Game"); var sibling = Path.Combine(temp, "Games2");
    Directory.CreateDirectory(gameDir); Directory.CreateDirectory(sibling);
    Check("Sibling prefix is not descendant", !PathPolicy.Within(root, sibling));
    Check("Direct child accepted", PathPolicy.DirectChild(root, gameDir));
    Check("Grandchild rejected as game root", !PathPolicy.DirectChild(root, Path.Combine(gameDir, "bin")));
    Throws("Duplicate root rejected", () => PathPolicy.ValidateRoot(root, [new LibraryRoot { Path = root }]));
    Throws("Nested root rejected", () => PathPolicy.ValidateRoot(gameDir, [new LibraryRoot { Path = root }]));
    var bin = Path.Combine(gameDir, "bin", "x64"); Directory.CreateDirectory(bin);
    var exe = Path.Combine(bin, "DemoGame.exe"); File.WriteAllBytes(exe, new byte[100]);
    File.WriteAllBytes(Path.Combine(gameDir, "uninstall.exe"), new byte[10]); File.WriteAllBytes(Path.Combine(gameDir, "data.dat"), new byte[200]);
    var game = new Game { RootPath = root, FolderPath = gameDir, Name = "Demo Game" };
    var versionProbe = new Game { RootPath = root, FolderPath = gameDir, LaunchExe = Path.GetRelativePath(gameDir, exe) };
    var versionFile = Path.Combine(gameDir, "version.txt"); File.WriteAllText(versionFile, "version=1.2.3");
    Check("Version file found for executable in deep folder", GameVersionDetector.Detect(versionProbe, default) is { Value: "1.2.3", Kind: LocalVersionKind.Release, Source: "version.txt" });
    Check("Root version can be read before executable selection", GameVersionDetector.Detect(game, default) is { Value: "1.2.3" });
    File.WriteAllText(versionFile, "build=1.2.3");
    Check("Dotted build labels stay in build namespace", GameVersionDetector.Detect(versionProbe, default) is { Value: "1.2.3", Kind: LocalVersionKind.Build });
    File.WriteAllText(versionFile, new string('x', 20_000));
    Check("Oversized version files ignored", GameVersionDetector.Detect(versionProbe, default) is null);
    File.Delete(versionFile);
    var binVersion = Path.Combine(bin, "version.json"); File.WriteAllText(binVersion, "{\"gameVersion\":\"2.31\"}");
    var buildFile = Path.Combine(gameDir, "build.txt"); File.WriteAllText(buildFile, "123456");
    Check("Version JSON near selected executable detected", GameVersionDetector.Detect(versionProbe, default) is { Value: "2.31", Kind: LocalVersionKind.Release });
    File.Delete(binVersion);
    Check("Build ID retained without comparing it to release version", GameVersionDetector.Detect(versionProbe, default) is { Value: "123456", Kind: LocalVersionKind.Build });
    File.Delete(buildFile);
    using (var cancelledVersion = new CancellationTokenSource())
    {
        cancelledVersion.Cancel();
        try { GameVersionDetector.Detect(versionProbe, cancelledVersion.Token); throw new Exception("Version cancellation ignored"); }
        catch (OperationCanceledException) { Check("Local version detection supports cancellation", true); }
    }
    var versionRecord = new Game { LocalVersion = "2.30", ManualVersion = true, VersionKind = LocalVersionKind.Release, VersionCheck = VersionCheckState.Outdated, SteamVersion = "2.31" };
    var roundtripVersion = System.Text.Json.JsonSerializer.Deserialize<Game>(System.Text.Json.JsonSerializer.Serialize(versionRecord))!;
    Check("Manual version and warning survive JSON persistence", roundtripVersion.ManualVersion && roundtripVersion.LocalVersion == "2.30" && roundtripVersion.IsOutdated);
    versionRecord.ResetVersionCheck();
    Check("Changed version clears outdated state", !versionRecord.IsOutdated && versionRecord.SteamVersion is null);
    versionRecord.LocalVersion = null; versionRecord.VersionCheck = VersionCheckState.Outdated;
    Check("Unknown local version never shows outdated badge", !versionRecord.IsOutdated && !versionRecord.HasVersion);
    var result = GameScanner.Scan(gameDir, game.Name, default);
    Check("Deep scan sums files exactly", result.Bytes == 310);
    Check("Deep executable discovered", result.Candidates.Any(x => x.RelativePath == Path.GetRelativePath(gameDir, exe)));
    Check("Uninstaller penalized", result.Candidates.Single(x => x.RelativePath == "uninstall.exe").Score < 0);
    Check("Exact deep filename selected without version metadata", GameScanner.Suggest(result.Candidates)?.RelativePath == Path.GetRelativePath(gameDir, exe));
    Check("Low evidence still requires selection", GameScanner.Suggest([new("Unknown.exe", 40, "")]) is null);
    var alanFolder = Path.Combine(root, "AlanWake2"); Directory.CreateDirectory(alanFolder);
    File.WriteAllBytes(Path.Combine(alanFolder, "AlanWake2.exe"), []);
    File.WriteAllBytes(Path.Combine(alanFolder, "AlanWake2_YamaKaldır.exe"), []);
    var alan = GameScanner.Scan(alanFolder, "Alan Wake 2", default);
    Check("Exact root filename wins over patch uninstaller", GameScanner.Suggest(alan.Candidates)?.RelativePath == "AlanWake2.exe" && alan.Candidates.Single(x => x.RelativePath.Contains("Yama")).IsHelper);
    var duplicateDir = Path.Combine(alanFolder, "backup"); Directory.CreateDirectory(duplicateDir);
    File.WriteAllBytes(Path.Combine(duplicateDir, "AlanWake2.exe"), []);
    Check("Duplicate plausible executables require manual selection", GameScanner.Suggest(GameScanner.Scan(alanFolder, "Alan Wake 2", default).Candidates) is null);
    Check("Helper remains excluded despite high score", GameScanner.Suggest([new("setup.exe", 200, "", true)]) is null);
    Check("Crash Bandicoot is not mistaken for crash reporter", !GameScanner.IsTool("CrashBandicoot.exe") && GameScanner.IsTool("CrashReporter.exe"));
    var shipping = GameScanner.Score(gameDir, "Demo Game", Path.Combine(bin, "DemoGame-Win64-Shipping.exe"));
    Check("Unreal suffix preserves exact title evidence", shipping.Score >= 70);
    Check("Tools directory excluded even with game metadata", GameScanner.Score(gameDir, "Demo Game", Path.Combine(gameDir, "tools", "DemoGame.exe")).IsHelper);
    Throws("Exe escape blocked", () => PathPolicy.ResolveExe(game, "../../Games2/other.exe"));
    Throws("Main root cannot be game deletion target", () => PathPolicy.ValidateGame(new Game { RootPath = root, FolderPath = root }));
    var link = Path.Combine(gameDir, "linked");
    try { Directory.CreateSymbolicLink(link, sibling); }
    catch (IOException) when (OperatingSystem.IsWindows())
    {
        // Junctions exercise the same reparse-point boundary without requiring
        // Windows Developer Mode or SeCreateSymbolicLinkPrivilege.
        var junction = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, sibling }) junction.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(junction)!; process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException(process.StandardError.ReadToEnd());
    }
    File.WriteAllBytes(Path.Combine(sibling, "sentinel"), new byte[700]);
    result = GameScanner.Scan(gameDir, game.Name, default);
    Check("Link target excluded from size", result.Bytes == 310 && result.Skipped == 1);
    Throws("Link traversal rejected", () => PathPolicy.EnsureNoLinks(link));
    Directory.Delete(link);
    using var cts = new CancellationTokenSource(); cts.Cancel();
    try { GameScanner.Scan(gameDir, game.Name, cts.Token); throw new Exception("Cancellation ignored"); }
    catch (OperationCanceledException) { Check("Scan cancellation respected", true); }

    var clock = new SessionClock(); clock.Observe(1000, true); clock.Observe(3000, true); clock.Observe(5000, false); clock.Observe(7000, true); clock.Observe(9000, true);
    Check("Handoff absence not counted", clock.DurationMs == 4000);
    clock.Observe(100000, true); Check("Sleep gap not counted", clock.DurationMs == 4000);
    clock.Suspend(); clock.Observe(102000, true); Check("Suspend resets monotonic baseline", clock.DurationMs == 4000);

    var data = Path.Combine(temp, "db"); var store = new LocalStore(data);
    var library = new LibraryRoot { Path = root }; store.SaveRoot(library); game.RootId = library.Id; store.SaveGame(game);
    var session = new PlaySession { GameId = game.Id, StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1), LastSeenAt = DateTimeOffset.UtcNow, DurationMs = 12345 };
    game.LastPlayedAt = session.StartedAt; game.PlayedMilliseconds = 12345; store.SaveSession(session, game);
    store = new LocalStore(data);
    Check("Interrupted session recovered at last observation", store.Sessions(game.Id).Single().Status == "Interrupted" && store.Sessions(game.Id).Single().EndedAt == session.LastSeenAt);
    Check("SQLite preserves last played and duration", store.Games().Single().PlayedMilliseconds == 12345 && store.Games().Single().LastPlayedAt == session.StartedAt);
    var favoriteGame = store.Games().Single(); favoriteGame.IsFavorite = true; store.SaveGame(favoriteGame);
    Check("New library starts with list layout and three columns", store.LoadViewSettings().Layout == LibraryLayout.List && store.LoadViewSettings().GridColumns == 3 && !store.LoadViewSettings().ExeSelectionOnly);
    store.SaveViewSettings(new() { SortBy = GameSortOrder.Size, Descending = true, FavoritesOnly = true, ExeSelectionOnly = true, Layout = LibraryLayout.Grid, GridColumns = 4 });
    store = new LocalStore(data);
    Check("Favorite persists across reopening database", store.Games().Single().IsFavorite);
    var viewOptions = store.LoadViewSettings();
    Check("Sort direction and favorites filter persist", viewOptions.SortBy == GameSortOrder.Size && viewOptions.Descending && viewOptions.FavoritesOnly);
    Check("Layout columns and exe filter persist across reopening", viewOptions.Layout == LibraryLayout.Grid && viewOptions.GridColumns == 4 && viewOptions.ExeSelectionOnly);
    store.SaveViewSettings(new() { Layout = (LibraryLayout)99, GridColumns = -1 });
    Check("Invalid gallery settings recover to supported defaults", store.LoadViewSettings().Layout == LibraryLayout.List && store.LoadViewSettings().GridColumns == 3);
    using (var preferencesDb = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(data, "gametracker.db") }.ToString()))
    {
        preferencesDb.Open(); using var preferenceCommand = preferencesDb.CreateCommand();
        preferenceCommand.CommandText = "UPDATE preferences SET payload = $payload";
        preferenceCommand.Parameters.AddWithValue("$payload", "{\"SortBy\":2,\"Descending\":true,\"FavoritesOnly\":true}");
        preferenceCommand.ExecuteNonQuery();
    }
    var oldView = store.LoadViewSettings();
    Check("Old preferences retain sorting with default list and no exe filter", oldView.SortBy == GameSortOrder.Size && oldView.Descending && oldView.FavoritesOnly && oldView.Layout == LibraryLayout.List && oldView.GridColumns == 3 && !oldView.ExeSelectionOnly);
    favoriteGame.IsFavorite = false; store.SaveGame(favoriteGame);
    Check("Favorite can be removed without losing history", !store.Games().Single().IsFavorite && store.Sessions(game.Id).Count == 1);
    Check("Fixture sibling remains untouched", File.Exists(Path.Combine(sibling, "sentinel")));

    var trackingGame = store.Games().Single(); trackingGame.LaunchExe = Path.GetRelativePath(gameDir, exe);
    var tracker = new SessionTracker(store); var now = DateTimeOffset.UtcNow;
    tracker.Observe([trackingGame], [new ProcessInfo(12, 1, now, exe)], 1000, now);
    var child = Path.Combine(bin, "DemoRuntime.exe"); File.WriteAllBytes(child, []);
    tracker.Observe([trackingGame], [new ProcessInfo(15, 12, now.AddSeconds(1), child)], 3000, now.AddSeconds(2));
    Check("Child continues after launcher exits", trackingGame.IsRunning);
    tracker.Observe([trackingGame], [], 5000, now.AddSeconds(4));
    tracker.Observe([trackingGame], [], 15000, now.AddSeconds(14));
    Check("Handoff grace closes at last sighting", !trackingGame.IsRunning && store.Sessions(game.Id).First().EndedAt == now.AddSeconds(2));

    var prefetchDirectory = Path.Combine(temp, "Prefetch"); Directory.CreateDirectory(prefetchDirectory);
    var desiredPath = @"D:\Games\Demo\game.exe";
    var correctPf = Path.Combine(prefetchDirectory, "GAME.EXE-11111111.pf");
    File.WriteAllBytes(correctPf, PrefetchBytes(31, desiredPath, historicalTime));
    File.WriteAllBytes(Path.Combine(prefetchDirectory, "GAME.EXE-22222222.pf"), PrefetchBytes(31, @"D:\Games\Other\game.exe", historicalTime.AddDays(1)));
    File.WriteAllBytes(Path.Combine(prefetchDirectory, "GAME.EXE-33333333.pf"), new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
    File.SetLastWriteTimeUtc(correctPf, DateTime.UtcNow);
    Check("Prefetch ignores other installation and corrupt record", WindowsPrefetch.FindLatestInDirectory(prefetchDirectory, [desiredPath], now, default) == historicalTime);
    Check("Prefetch does not infer execution from file modification date", WindowsPrefetch.FindLatestInDirectory(prefetchDirectory, [desiredPath], now, default) != new DateTimeOffset(File.GetLastWriteTimeUtc(correctPf)));
    using var cancelledPrefetch = new CancellationTokenSource(); cancelledPrefetch.Cancel();
    try { WindowsPrefetch.FindLatestInDirectory(prefetchDirectory, [desiredPath], now, cancelledPrefetch.Token); throw new Exception("Prefetch cancellation ignored"); }
    catch (OperationCanceledException) { Check("Prefetch directory reading respects cancellation", true); }

    var historyStore = new LocalStore(Path.Combine(temp, "HistoryDb"));
    var historyRoot = new LibraryRoot { Path = Path.Combine(temp, "HistoryGames") }; Directory.CreateDirectory(historyRoot.Path); historyStore.SaveRoot(historyRoot);
    Game HistoryGame(bool pending = true)
    {
        var item = new Game { RootId = historyRoot.Id, RootPath = historyRoot.Path, Name = "History Game", InitialPlayHistoryPending = pending,
            LaunchExe = "DemoGame.exe", Candidates = [new("DemoGame.exe", 75, ""), new("uninstall.exe", -150, "", true)], RuntimeExes = ["Runtime.exe"] };
        item.FolderPath = Path.Combine(historyRoot.Path, item.Id.ToString()); Directory.CreateDirectory(item.FolderPath);
        foreach (var filename in new[] { "DemoGame.exe", "Runtime.exe", "uninstall.exe" }) File.WriteAllBytes(Path.Combine(item.FolderPath, filename), []);
        historyStore.SaveGame(item); return item;
    }
    var historyCalls = 0; IReadOnlyList<string>? historyTargets = null;
    var importer = new InitialPlayHistory(historyStore, (targets, _, _) => { historyCalls++; historyTargets = targets; return historicalTime; });
    var newGame = HistoryGame(); await importer.ImportAsync(newGame, default);
    Check("New discovery directly imports last played from Prefetch", newGame.LastPlayedAt == historicalTime && newGame.LastPlayedFromPrefetch && !newGame.InitialPlayHistoryPending);
    Check("History import creates no invented sessions or play duration", newGame.PlayedMilliseconds == 0 && historyStore.Sessions(newGame.Id).Count == 0);
    Check("History targets include game and runtime but exclude helper", historyTargets?.Count == 2 && historyTargets.Contains(Path.Combine(newGame.FolderPath, "DemoGame.exe")) && historyTargets.Contains(Path.Combine(newGame.FolderPath, "Runtime.exe")));
    var reopenedHistoryStore = new LocalStore(historyStore.DataDirectory);
    var reopenedHistoryGame = reopenedHistoryStore.Games().Single(g => g.Id == newGame.Id);
    await new InitialPlayHistory(reopenedHistoryStore, (_, _, _) => { historyCalls++; return now; }).ImportAsync(reopenedHistoryGame, default);
    Check("History and one-time decision persist across reopening", reopenedHistoryGame.LastPlayedAt == historicalTime && reopenedHistoryGame.LastPlayedFromPrefetch && historyCalls == 1);
    var existingGame = HistoryGame(false); await importer.ImportAsync(existingGame, default);
    Check("Existing game without play date is not retroactively fetched", existingGame.LastPlayedAt is null && historyCalls == 1);
    var knownGame = HistoryGame(); knownGame.LastPlayedAt = now; await importer.ImportAsync(knownGame, default);
    Check("Launcher last played is never overwritten", knownGame.LastPlayedAt == now && !knownGame.LastPlayedFromPrefetch && historyCalls == 1);
    var sessionOnly = HistoryGame(); historyStore.SaveSession(new PlaySession { GameId = sessionOnly.Id, StartedAt = now, LastSeenAt = now, Status = "Completed" });
    await importer.ImportAsync(sessionOnly, default);
    Check("Existing session blocks history import even without date", sessionOnly.LastPlayedAt is null && historyCalls == 1);
    var unresolvedGame = HistoryGame(); unresolvedGame.LaunchExe = null;
    await importer.ImportAsync(unresolvedGame, default);
    Check("Unresolved launch exe can use exact candidate execution history", unresolvedGame.LastPlayedAt == historicalTime && unresolvedGame.LaunchExe is null);
    var absentGame = HistoryGame(); var absentCalls = 0;
    var absentHistory = new InitialPlayHistory(historyStore, (_, _, _) => { absentCalls++; return null; });
    await absentHistory.ImportAsync(absentGame, default); await absentHistory.ImportAsync(absentGame, default);
    Check("Missing Prefetch is tried once and leaves last played empty", absentCalls == 1 && absentGame.LastPlayedAt is null && !absentGame.InitialPlayHistoryPending);
    var deniedGame = HistoryGame(); var deniedCalls = 0;
    var deniedHistory = new InitialPlayHistory(historyStore, (_, _, _) => { deniedCalls++; throw new UnauthorizedAccessException("fixture denied"); });
    await deniedHistory.ImportAsync(deniedGame, default); await deniedHistory.ImportAsync(deniedGame, default);
    Check("Inaccessible Prefetch is optional and not retried", deniedCalls == 1 && deniedGame.LastPlayedAt is null && !deniedGame.InitialPlayHistoryPending);
    var concurrentGame = HistoryGame();
    await new InitialPlayHistory(historyStore, (_, _, _) => { concurrentGame.LastPlayedAt = now; return historicalTime; }).ImportAsync(concurrentGame, default);
    Check("Live observed play wins over in-flight history import", concurrentGame.LastPlayedAt == now && !concurrentGame.LastPlayedFromPrefetch);
    var cancelledHistoryGame = HistoryGame();
    try { await importer.ImportAsync(cancelledHistoryGame, cancelledPrefetch.Token); throw new Exception("Import cancellation ignored"); }
    catch (OperationCanceledException) { Check("Cancelled initial history attempt remains consumed", !historyStore.Games().Single(g => g.Id == cancelledHistoryGame.Id).InitialPlayHistoryPending); }
    var importedTracker = new SessionTracker(historyStore);
    importedTracker.Observe([newGame], [new ProcessInfo(100, 1, now, Path.Combine(newGame.FolderPath, "DemoGame.exe"))], 1000, now);
    Check("Actual launcher observation replaces imported date and provenance", newGame.LastPlayedAt == now && !newGame.LastPlayedFromPrefetch && historyStore.Sessions(newGame.Id).Count == 1);
    importedTracker.Stop();

    Check("Untrusted cover host rejected", !CoverClient.IsAllowedImage(new Uri("https://example.com/a.jpg")));
    Check("HTTP image rejected", !CoverClient.IsAllowedImage(new Uri("http://images.igdb.com/a.jpg")));
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["STEAMGRIDDB_API_KEY"] = "test", ["RAWG_API_KEY"] = "test" }).Build();
    var calls = new List<string>();
    using var resolver = new ArtworkResolver(config, new HttpClient(new FakeHandler(request =>
    {
        if (request.RequestUri!.Host == "store.steampowered.com") return Json("{\"items\":[]}");
        if (request.Method == HttpMethod.Head) { var image = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) }; image.Content.Headers.ContentType = new("image/jpeg"); return image; }
        calls.Add(request.RequestUri!.Host);
        var body = request.RequestUri.Host == "www.steamgriddb.com" ? "{\"data\":[]}" : "{\"results\":[{\"id\":5,\"name\":\"Demo Game\",\"slug\":\"demo-game\",\"background_image\":\"https://media.rawg.io/demo.jpg\"}]}";
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    })));
    var cover = await resolver.Resolve("Demo Game", default);
    Check("Cover provider falls back in order", cover.Match?.Provider == "RAWG" && calls.SequenceEqual(new[] { "www.steamgriddb.com", "api.rawg.io" }));
    await resolver.Resolve("Demo Game", default); Check("Cover result cached", calls.Count == 2);
    using var empty = new ArtworkResolver(new ConfigurationBuilder().Build(), new HttpClient(new FakeHandler(request => Json(request.RequestUri!.Host == "store.steampowered.com" ? "{\"items\":[]}" : "{\"success\":true,\"data\":[]}"))));
    var absent = await empty.Resolve("Demo Game", default); Check("Anonymous providers missing produce final placeholder", absent.Match is null && !absent.Retryable);

    var steamConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["STEAM_API_KEY"] = "test", ["STEAMGRIDDB_API_KEY"] = "test" }).Build();
    var steamCalls = new List<string>();
    using var steamResolver = new ArtworkResolver(steamConfig, new HttpClient(new FakeHandler(request =>
    {
        steamCalls.Add(request.RequestUri!.Host);
        if (request.Method == HttpMethod.Head) { var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) }; response.Content.Headers.ContentType = new("image/jpeg"); return response; }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"response\":{\"apps\":[{\"appid\":10,\"name\":\"Demo Game\"}],\"have_more_results\":false}}") };
    })));
    var steamCover = await steamResolver.Resolve("Demo Game", default);
    Check("Steam is first and short circuits later providers", steamCover.Match?.Provider == "Steam" && steamCalls.SequenceEqual(new[] { "api.steampowered.com", "shared.fastly.steamstatic.com" }));

    var igdbConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["STEAMGRIDDB_API_KEY"] = "test", ["IGDB_CLIENT_ID"] = "test", ["IGDB_CLIENT_SECRET"] = "test" }).Build();
    using var igdbResolver = new ArtworkResolver(igdbConfig, new HttpClient(new FakeHandler(request =>
    {
        if (request.RequestUri!.Host == "store.steampowered.com") return Json("{\"items\":[]}");
        if (request.Method == HttpMethod.Head) { var image = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) }; image.Content.Headers.ContentType = new("image/jpeg"); return image; }
        var body = request.RequestUri!.Host switch
        {
            "www.steamgriddb.com" => "{\"data\":[]}",
            "id.twitch.tv" => "{\"access_token\":\"test\",\"expires_in\":3600}",
            _ => "[{\"id\":25,\"name\":\"Demo Game\",\"url\":\"https://www.igdb.com/games/demo\",\"cover\":{\"image_id\":\"abc\"}}]"
        };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
    })));
    var igdbCover = await igdbResolver.Resolve("Demo Game", default);
    Check("IGDB fallback obtains application token and cover", igdbCover.Match?.Provider == "IGDB" && igdbCover.Match.ImageUrl.EndsWith("/abc.jpg"));
    using var missingResolver = new ArtworkResolver(config, new HttpClient(new FakeHandler(request => Json(request.RequestUri!.Host switch { "store.steampowered.com" => "{\"items\":[]}", "www.steamgriddb.com" => "{\"data\":[]}", _ => "{\"results\":[]}" }))));
    var missing = await missingResolver.Resolve("Missing Game", default);
    Check("All providers missing yields final placeholder", missing.Match is null && !missing.Retryable);
    using var failedResolver = new ArtworkResolver(config, new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests))));
    var failed = await failedResolver.Resolve("Demo Game", default);
    Check("Rate limited providers yield retryable placeholder", failed.Match is null && failed.Retryable);

    var coverStore = new LocalStore(Path.Combine(temp, "cover-db")); coverStore.SaveRoot(library);
    Game CoverGame() => new() { RootId = library.Id, RootPath = root, FolderPath = Path.Combine(root, Guid.NewGuid().ToString()), Name = "DemoGame" };
    var anonymousCalls = new List<string>();
    HttpResponseMessage Anonymous(HttpRequestMessage request)
    {
        anonymousCalls.Add(request.RequestUri!.AbsoluteUri);
        if (request.RequestUri.Host == "store.steampowered.com") return Json("{\"items\":[]}");
        if (request.RequestUri.AbsolutePath.EndsWith("autocomplete")) return Json(publicGame);
        if (request.RequestUri.AbsolutePath.EndsWith("assets")) return Json(publicAssets);
        return Png();
    }
    var anonymousGame = CoverGame(); anonymousGame.CoverRetryAt = DateTimeOffset.UtcNow.AddDays(7);
    using var anonymousClient = new CoverClient(coverStore, new(), new HttpClient(new FakeHandler(Anonymous)));
    await anonymousClient.ResolveAsync(anonymousGame, default);
    Check("Anonymous SteamGridDB downloads without service or credentials", anonymousGame.CoverProvider == "SteamGridDB" && File.Exists(anonymousGame.CoverPath));
    Check("Old failed cover cooldown bypassed once", anonymousGame.CoverRetryAt is null && anonymousGame.CoverLookupKey is not null);
    Check("Search uses cleaned title and Steam precedes SteamGridDB", anonymousCalls[0].Contains("term=Demo%20Game") && anonymousCalls[1].Contains("steamgriddb.com"));
    var callCount = anonymousCalls.Count;
    await anonymousClient.ResolveAsync(anonymousGame, default);
    Check("Cached local image avoids all network requests", anonymousCalls.Count == callCount);
    File.Delete(anonymousGame.CoverPath!);
    await anonymousClient.ResolveAsync(anonymousGame, default);
    Check("Missing cached image is downloaded again", File.Exists(anonymousGame.CoverPath) && anonymousCalls.Count > callCount);

    using var anonymousResolver = new ArtworkResolver(new ConfigurationBuilder().Build(), new HttpClient(new FakeHandler(request =>
    {
        if (request.Method == HttpMethod.Head) { var image = Png(); image.Content.Headers.ContentType = new("image/png"); return image; }
        return Anonymous(request);
    })));
    Check("Service also supports anonymous provider chain", (await anonymousResolver.Resolve("DemoGame", default)).Match?.Provider == "SteamGridDB");

    var brokenSteamGame = CoverGame();
    using var brokenSteamClient = new CoverClient(coverStore, new(), new HttpClient(new FakeHandler(request =>
    {
        if (request.RequestUri!.Host == "store.steampowered.com") return Json("{\"items\":[{\"type\":\"app\",\"id\":7,\"name\":\"Demo Game\"}]}");
        if (request.RequestUri.Host == "shared.fastly.steamstatic.com") return new(HttpStatusCode.NotFound);
        return Anonymous(request);
    })));
    await brokenSteamClient.ResolveAsync(brokenSteamGame, default);
    Check("Missing Steam artwork falls through to SteamGridDB download", brokenSteamGame.CoverProvider == "SteamGridDB");

    var offlineGame = CoverGame(); offlineGame.CoverRetryAt = DateTimeOffset.UtcNow.AddDays(7);
    var offlineCalls = 0;
    using var offlineClient = new CoverClient(coverStore, new(), new HttpClient(new FakeHandler(_ => { offlineCalls++; throw new HttpRequestException("Offline fixture"); })));
    await offlineClient.ResolveAsync(offlineGame, default);
    Check("Offline failure preserves game and schedules short retry", offlineGame.CoverPath is null && offlineGame.CoverRetryAt < DateTimeOffset.UtcNow.AddMinutes(16) && coverStore.Games().Any(x => x.Id == offlineGame.Id));
    await offlineClient.ResolveAsync(offlineGame, default);
    Check("Current failure cooldown prevents repeated requests", offlineCalls == 2);
    offlineGame.Name = "ChangedTitle";
    await offlineClient.ResolveAsync(offlineGame, default);
    Check("Title change invalidates failed lookup cooldown", offlineCalls == 4);

    var ambiguityRequests = 0;
    using var ambiguousPublic = new HttpClient(new FakeHandler(_ => { ambiguityRequests++; return Json("{\"success\":true,\"data\":[{\"id\":1,\"name\":\"Demo Game\"},{\"id\":2,\"name\":\"Demo Game\"}]}"); }));
    Check("Ambiguous public search never requests assets", (await new PublicArtwork(ambiguousPublic).SteamGrid("DemoGame", default)).Count == 0 && ambiguityRequests == 1);

    var remoteGame = CoverGame(); var remoteAfter = new List<string>();
    using var remoteClient = new CoverClient(coverStore, new() { CoverServiceUrl = "https://covers.example.com" }, new HttpClient(new FakeHandler(request =>
    {
        var host = request.RequestUri!.Host;
        if (host == "store.steampowered.com") return Json("{\"items\":[]}");
        if (host == "www.steamgriddb.com") return Json("{\"success\":true,\"data\":[]}");
        if (host == "covers.example.com")
        {
            remoteAfter.Add(request.RequestUri.Query);
            var provider = request.RequestUri.Query.Contains("after=SteamGridDB") ? "IGDB" : request.RequestUri.Query.Contains("after=IGDB") ? "RAWG" : "SteamGridDB";
            var image = provider switch { "SteamGridDB" => "https://cdn2.steamgriddb.com/missing.png", "IGDB" => "https://images.igdb.com/broken.jpg", _ => "https://media.rawg.io/valid.png" };
            return Json(System.Text.Json.JsonSerializer.Serialize(new CoverResponse(new(provider, "1", "Demo Game", image, "https://example.com/game"))));
        }
        if (host == "media.rawg.io") return Png();
        return Json("not an image");
    })));
    await remoteClient.ResolveAsync(remoteGame, default);
    Check("Undownloadable service images fall through SGDB then IGDB then RAWG", remoteGame.CoverProvider == "RAWG" && remoteAfter.Count == 3 && remoteAfter[1].Contains("after=SteamGridDB") && remoteAfter[2].Contains("after=IGDB"));
    Check("Credential-free service skips consumed provider prefix", (await anonymousResolver.Resolve("DemoGame", default, "SteamGridDB")).Match is null);

    var cancelledGame = CoverGame();
    using var cancelClient = new CoverClient(coverStore, new(), new HttpClient(new FakeHandler(Anonymous)));
    using var alreadyCancelled = new CancellationTokenSource(); alreadyCancelled.Cancel();
    try { await cancelClient.ResolveAsync(cancelledGame, alreadyCancelled.Token); throw new Exception("Cancellation ignored"); }
    catch (OperationCanceledException) { Check("Cover cancellation leaves lookup eligible", cancelledGame.CoverLookupKey is null); }
    await cancelClient.ResolveAsync(cancelledGame, default);
    Check("Cover queue usable after cancellation", cancelledGame.CoverPath is not null);
    var gtaGame = CoverGame(); gtaGame.Name = "GTAV Enhanced";
    gtaGame.CoverLookupKey = "public-artwork-v1:gtavenhanced:"; gtaGame.CoverRetryAt = DateTimeOffset.UtcNow.AddDays(7);
    var gtaQueries = new List<string>();
    using var gtaClient = new CoverClient(coverStore, new(), new HttpClient(new FakeHandler(request =>
    {
        gtaQueries.Add(request.RequestUri!.AbsoluteUri);
        if (request.RequestUri.Host == "store.steampowered.com") return Json("{\"items\":[{\"type\":\"app\",\"id\":3240220,\"name\":\"Grand Theft Auto V Enhanced\"}]}");
        return Png();
    })));
    await gtaClient.ResolveAsync(gtaGame, default);
    Check("GTA old negative cache retried using canonical title", gtaGame.CoverProvider == "Steam" && gtaQueries[0].Contains("Grand%20Theft%20Auto%20V%20Enhanced") && gtaGame.CoverRetryAt is null);

    var shoppingConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    { ["STEAM_API_KEY"] = "fixture", ["STEAMGRIDDB_API_KEY"] = "fixture", ["IGDB_CLIENT_ID"] = "fixture", ["IGDB_CLIENT_SECRET"] = "fixture", ["RAWG_API_KEY"] = "fixture", ["SERPAPI_API_KEY"] = "fixture" }).Build();
    var shoppingCalls = new List<string>();
    using var shoppingResolver = new ArtworkResolver(shoppingConfig, new HttpClient(new FakeHandler(request =>
    {
        shoppingCalls.Add(request.RequestUri!.Host);
        if (request.Method == HttpMethod.Head) { var image = Png(); image.Content.Headers.ContentType = new("image/png"); return image; }
        return Json(request.RequestUri.Host switch
        {
            "api.steampowered.com" => "{\"response\":{\"apps\":[],\"have_more_results\":false}}",
            "www.steamgriddb.com" => "{\"data\":[]}",
            "id.twitch.tv" => "{\"access_token\":\"fixture\",\"expires_in\":3600}",
            "api.igdb.com" => "[]",
            "api.rawg.io" => "{\"results\":[]}",
            _ => "{\"shopping_results\":[{\"product_id\":\"wrong\",\"title\":\"Demo Game 2 PC\",\"thumbnail\":\"https://encrypted-tbn0.gstatic.com/shopping?q=wrong\"},{\"product_id\":\"poster\",\"title\":\"Demo Game Poster\",\"thumbnail\":\"https://encrypted-tbn0.gstatic.com/shopping?q=poster\"},{\"product_id\":\"first-match\",\"title\":\"Demo Game PC\",\"thumbnails\":[\"https://encrypted-tbn0.gstatic.com/shopping?q=valid\"]},{\"product_id\":\"second-match\",\"title\":\"Demo Game PC\",\"thumbnail\":\"https://encrypted-tbn1.gstatic.com/shopping?q=second\"}]}"
        });
    })));
    var shoppingCover = await shoppingResolver.Resolve("Demo Game", default);
    Check("Shopping comes after Steam SGDB IGDB and RAWG", shoppingCalls.Take(6).SequenceEqual(new[] { "api.steampowered.com", "www.steamgriddb.com", "id.twitch.tv", "api.igdb.com", "api.rawg.io", "serpapi.com" }));
    Check("Shopping uses first matching game instead of first result", shoppingCover.Match?.Provider == "GoogleShopping" && shoppingCover.Match.GameId == "first-match" && shoppingCover.Match.ImageUrl.Contains("q=valid"));
    var shoppingCount = shoppingCalls.Count; await shoppingResolver.Resolve("Demo Game", default);
    Check("Shopping results are cached", shoppingCount == shoppingCalls.Count);
    using var categorizedShopping = new ArtworkResolver(shoppingConfig, new HttpClient(new FakeHandler(request =>
    {
        if (request.Method == HttpMethod.Head)
        {
            if (request.RequestUri!.Query.Contains("missing")) return new(HttpStatusCode.NotFound);
            var image = Png(); image.Content.Headers.ContentType = new("image/png"); return image;
        }
        return Json("""
            {"categorized_shopping_results":[{"title":"Popular options","shopping_results":[
              {"title":"Demo Game PC","product_id":"broken-image","thumbnail":"https://encrypted-tbn0.gstatic.com/shopping?q=missing"},
              {"title":"Demo Game 2 PC","product_id":"sequel","thumbnail":"https://encrypted-tbn0.gstatic.com/shopping?q=wrong"}]},
              {"title":"More options","shopping_results":[{"title":"Demo Game PC","product_id":"category-match","thumbnail":"https://encrypted-tbn2.gstatic.com/shopping?q=valid"}]}]}
            """);
    })));
    Check("Categorized Shopping skips broken images and wrong games", (await categorizedShopping.Resolve("Demo Game", default, "RAWG")).Match?.GameId == "category-match");
    using var shoppingUnavailable = new ArtworkResolver(shoppingConfig, new HttpClient(new FakeHandler(_ => new(HttpStatusCode.TooManyRequests))));
    Check("Shopping API failure is retryable", (await shoppingUnavailable.Resolve("Demo Game", default, "RAWG")).Retryable);
    Check("No configured shopping key yields placeholder without requests", (await empty.Resolve("Demo Game", default, "RAWG")).Match is null);
    Check("Google image hosts are narrowly allowed", CoverClient.IsAllowedImage(new("https://encrypted-tbn0.gstatic.com/shopping?q=test")) && !CoverClient.IsAllowedImage(new("https://encrypted-tbn0.gstatic.com.evil.example/test.png")));
    var shoppingGame = CoverGame();
    using var shoppingClient = new CoverClient(coverStore, new() { CoverServiceUrl = "https://covers.example.com" }, new HttpClient(new FakeHandler(request =>
    {
        if (request.RequestUri!.Host == "store.steampowered.com") return Json("{\"items\":[]}");
        if (request.RequestUri.Host == "www.steamgriddb.com") return Json("{\"success\":true,\"data\":[]}");
        if (request.RequestUri.Host == "covers.example.com") return Json(System.Text.Json.JsonSerializer.Serialize(shoppingCover));
        return Png();
    })));
    await shoppingClient.ResolveAsync(shoppingGame, default);
    Check("Desktop downloads and stores Shopping fallback", shoppingGame.CoverProvider == "GoogleShopping" && File.Exists(shoppingGame.CoverPath));

    var manualGame = CoverGame();
    using var manualHttp = new HttpClient(new FakeHandler(Anonymous));
    using var manualClient = new CoverClient(coverStore, new(), manualHttp);
    var manualBytes = await Png().Content.ReadAsByteArrayAsync();
    await manualClient.ImportManualAsync(manualGame, manualBytes, default);
    var manualPath = manualGame.CoverPath!;
    Check("Manual cover is copied into local cache", manualGame.ManualCover && PathPolicy.Within(coverStore.DataDirectory, manualPath) && File.Exists(manualPath));
    Check("Manual cover persists without web attribution", coverStore.Games().Single(g => g.Id == manualGame.Id).ManualCover && manualGame.CoverAttribution is null);
    var networkBeforeManual = anonymousCalls.Count;
    await manualClient.ResolveAsync(manualGame, default);
    Check("Automatic lookup does not replace manual cover", manualGame.CoverPath == manualPath && anonymousCalls.Count == networkBeforeManual);
    manualGame.Name = "New Name"; await manualClient.ResolveAsync(manualGame, default);
    Check("Title change preserves manual cover", manualGame.CoverPath == manualPath);
    try { await manualClient.ImportManualAsync(manualGame, Encoding.UTF8.GetBytes("invalid picture"), default); throw new Exception("Invalid image accepted"); }
    catch (IOException) { Check("Invalid manual image preserves previous cover", manualGame.CoverPath == manualPath); }
    await manualClient.ImportManualAsync(manualGame, manualBytes, default);
    Check("Manual replacement uses new URI for image refresh", manualGame.CoverPath != manualPath && File.Exists(manualGame.CoverPath));
    File.Delete(manualGame.CoverPath!); await manualClient.ResolveAsync(manualGame, default);
    Check("Missing manual image still requires explicit automatic reset", manualGame.ManualCover && anonymousCalls.Count == networkBeforeManual);
    manualGame.Name = "DemoGame"; await manualClient.UseAutomaticAsync(manualGame, default);
    Check("Explicit automatic reset resumes providers", !manualGame.ManualCover && manualGame.CoverProvider == "SteamGridDB" && File.Exists(manualGame.CoverPath));
    game.Deleted = true; store.SaveGame(game);
    var reinstalled = new Game { RootId = library.Id, RootPath = root, FolderPath = gameDir, Name = "Demo Reinstalled" }; store.SaveGame(reinstalled);
    Check("Reinstall uses new identity and keeps archived history", store.Games().Count == 2 && store.Sessions(game.Id).Count > 0);
    if (OperatingSystem.IsWindows())
    {
        GameOperations.BindIdentity(trackingGame);
        var moved = gameDir + "-original"; Directory.Move(gameDir, moved); Directory.CreateDirectory(gameDir);
        Throws("Replaced directory identity blocks deletion", () => GameOperations.Delete(trackingGame, true, []));
        Directory.Delete(gameDir); Directory.Move(moved, gameDir);
        GameOperations.Delete(trackingGame, true, []);
        Check("Permanent deletion touches only fixture game folder", !Directory.Exists(gameDir) && Directory.Exists(root) && File.Exists(Path.Combine(sibling, "sentinel")));
    }
    else Console.WriteLine("SKIP: Windows identity and permanent-delete fixture checks (Windows required).");
}
finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(temp, true); }
Console.WriteLine($"{passed} checks passed.");

sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
}

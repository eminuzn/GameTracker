using System.Threading.RateLimiting;
using GameTracker.CoverService;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<ArtworkResolver>();
builder.Services.AddRateLimiter(options => options.AddPolicy("covers", context => RateLimitPartition.GetFixedWindowLimiter(
    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
    _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));
var app = builder.Build();
app.UseRateLimiter();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/api/covers", async (string? title, string? after, ArtworkResolver resolver, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(title) || title.Length > 200) return Results.BadRequest("Geçerli bir oyun adı gerekli.");
    if (after is not null && after is not ("Steam" or "SteamGridDB" or "IGDB" or "RAWG" or "GoogleShopping")) return Results.BadRequest("Geçersiz kapak sağlayıcısı.");
    return Results.Ok(await resolver.Resolve(title, ct, after));
}).RequireRateLimiting("covers");
app.Run();

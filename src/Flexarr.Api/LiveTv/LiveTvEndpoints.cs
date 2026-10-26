using System.Security.Cryptography;
using System.Text;
using Flexarr.Api.Authentication;
using Flexarr.Api.Configuration;
using Flexarr.Api.Profiles;
using Flexarr.Api.Security;
using Flexarr.Core.Data;
using Flexarr.Core.LiveTv;
using Flexarr.Streaming;
using Flexarr.Streaming.Live;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Flexarr.Api.LiveTv;

public sealed record LiveTvStatusDto(bool Configured, int Channels, bool Refreshing, DateTime? RefreshedAt, string? Error, int RewindMinutes);

public sealed record LiveProgrammeDto(long Id, DateTime Start, DateTime End, string Title, string? Subtitle, string? Description, string? Category,
    int? Season, int? Episode, bool IsNew);

/// <param name="IsFavourite">Starred by the profile asking.</param>
public sealed record LiveChannelDto(Guid Id, double Number, string Name, string? Group, string? LogoUrl, bool IsFavourite, LiveProgrammeDto? Now, LiveProgrammeDto? Next);

public sealed record GuideChannelDto(Guid Id, double Number, string Name, string? Group, string? LogoUrl, bool IsFavourite, IReadOnlyList<LiveProgrammeDto> Programmes);

public sealed record GuideDto(DateTime From, DateTime Until, IReadOnlyList<GuideChannelDto> Channels);

/// <param name="Replaces">The viewer's previous channel, left first (with one connection, it has to go before this can start).</param>
public sealed record WatchRequest(string? Replaces = null);

/// <param name="Playlist">The live HLS playlist; its address is the viewer's capability, so it needs no sign-in.</param>
/// <param name="Method">How the channel reaches the player ("Original", "Converted to 720p, deinterlaced").</param>
public sealed record WatchDto(string Viewer, string Playlist, LiveChannelDto Channel, string Method, int RewindSeconds);

public sealed record LiveWatcherDto(Guid ChannelId, string ChannelName, string ProfileId, DateTime StartedAt);

public static partial class LiveTvEndpoints
{
    private static readonly TimeSpan FormatKeptFor = TimeSpan.FromHours(12);
    private static readonly TimeSpan MaxGuideWindow = TimeSpan.FromHours(24);

    public static RouteGroupBuilder MapLiveTvEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/livetv").WithTags("Live TV");

        group.MapGet("/status", async (IOptionsMonitor<FlexarrOptions> options, LiveTvStatus status, FlexarrDbContext db, CancellationToken ct) =>
        {
            var o = options.CurrentValue.LiveTv;
            var channels = await db.LiveChannels.CountAsync(ct);
            var refreshed = status.RefreshedAt ?? await db.LiveChannels.MaxAsync(c => (DateTime?)c.UpdatedAt, ct);
            return new LiveTvStatusDto(o?.Account is not null, channels, status.Refreshing, refreshed, status.Error, o?.RewindMinutes ?? 60);
        });

        group.MapGet("/channels", async (FlexarrDbContext db, CurrentProfile profile, TimeProvider clock, CancellationToken ct) =>
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var channels = await db.LiveChannels.AsNoTracking().OrderBy(c => c.Number).ThenBy(c => c.Name).ToListAsync(ct);
            var favourites = await FavouritesAsync(db, profile.Id, ct);

            // What's on and what's next on each: the two programmes that end soonest after now.
            var upcoming = (await db.LiveProgrammes.AsNoTracking()
                    .Where(p => p.End > now && p.Start < now.AddHours(12))
                    .OrderBy(p => p.Start).ToListAsync(ct))
                .GroupBy(p => p.ChannelId).ToDictionary(g => g.Key, g => g.Take(2).ToList());
            return channels.Select(c =>
            {
                var list = upcoming.GetValueOrDefault(c.Id) ?? [];
                var on = list.FirstOrDefault(p => p.Start <= now);
                var next = list.FirstOrDefault(p => p.Start > now);
                return ToDto(c, favourites.Contains(c.Id), on, next);
            }).ToList();
        });

        group.MapGet("/guide", async (DateTime? from, double? hours, FlexarrDbContext db, CurrentProfile profile, TimeProvider clock, CancellationToken ct) =>
        {
            var start = (from?.ToUniversalTime() ?? clock.GetUtcNow().UtcDateTime.AddMinutes(-30));
            var window = TimeSpan.FromHours(Math.Clamp(hours ?? 6, 1, MaxGuideWindow.TotalHours));
            var until = start + window;
            var channels = await db.LiveChannels.AsNoTracking().OrderBy(c => c.Number).ThenBy(c => c.Name).ToListAsync(ct);
            var programmes = (await db.LiveProgrammes.AsNoTracking()
                    .Where(p => p.End > start && p.Start < until).OrderBy(p => p.Start).ToListAsync(ct))
                .ToLookup(p => p.ChannelId);
            var favourites = await FavouritesAsync(db, profile.Id, ct);
            return new GuideDto(start, until, [.. channels.Select(c =>
                new GuideChannelDto(c.Id, c.Number, c.Name, c.Group, LogoUrl(c), favourites.Contains(c.Id), [.. programmes[c.Id].Select(ToDto)]))]);
        });

        // Each profile's own favourites; starring twice, or unstarring what isn't starred, is fine.
        group.MapPut("/channels/{id:guid}/favourite", async (Guid id, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            if (!await db.LiveChannels.AnyAsync(c => c.Id == id, ct))
            {
                return Results.NotFound();
            }

            if (!await db.LiveFavourites.AnyAsync(f => f.ProfileId == profile.Id && f.ChannelId == id, ct))
            {
                db.LiveFavourites.Add(new LiveFavourite { ProfileId = profile.Id, ChannelId = id });
                await db.SaveChangesAsync(ct);
            }

            return Results.NoContent();
        });

        group.MapDelete("/channels/{id:guid}/favourite", async (Guid id, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            await db.LiveFavourites.Where(f => f.ProfileId == profile.Id && f.ChannelId == id).ExecuteDeleteAsync(ct);
            return Results.NoContent();
        });

        group.MapPost("/channels/{id:guid}/watch", async (Guid id, [FromBody] WatchRequest? request, FlexarrDbContext db, IOptionsMonitor<FlexarrOptions> options,
            LiveStreams streams, LiveTvViewers viewers, CurrentProfile profile, IMemoryCache memory, TimeProvider clock, CancellationToken ct) =>
        {
            var settings = options.CurrentValue;
            if (settings.LiveTv is not { Account: { } account } liveTv)
            {
                return Results.Problem("Live TV isn't set up. Add a source in Settings › Live TV.", statusCode: StatusCodes.Status409Conflict);
            }

            if (await db.LiveChannels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct) is not { } channel)
            {
                return Results.NotFound();
            }

            // Changing channel: the old one is let go first, so its connection is free for this one.
            if (request?.Replaces is { } previous && viewers.Get(previous) is { } old && old.ProfileId == profile.Id)
            {
                viewers.Remove(previous);
                await streams.LeaveAsync(old.Session, old.Id);
            }

            var source = account.StreamOf(channel.SourceId);
            var ffprobe = settings.FfprobePath;
            var viewerId = TranscodeManager.NewId();
            LiveSession session;
            try
            {
                session = await streams.JoinAsync(channel.Id.ToString("N"), source, async token =>
                {
                    var key = $"livetv-format:{channel.Id}";
                    if (!memory.TryGetValue(key, out LiveFormat? format) || format is null)
                    {
                        format = await LiveProbe.ProbeAsync(ffprobe, source, token);
                        memory.Set(key, format, FormatKeptFor);
                    }

                    return LivePlan.For(format);
                }, TimeSpan.FromMinutes(Math.Clamp(liveTv.RewindMinutes, 1, 240)), viewerId, ct);
            }
            catch (LiveStreamException ex)
            {
                // A channel that wouldn't start is probed afresh next time: its format may be what changed.
                memory.Remove($"livetv-format:{channel.Id}");
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable, title: "The channel couldn't be played");
            }

            viewers.Add(new LiveViewer(viewerId, channel.Id, channel.Name, profile.Id, profile.SignInId, session));
            var now = clock.GetUtcNow().UtcDateTime;
            var on = await db.LiveProgrammes.AsNoTracking().Where(p => p.ChannelId == channel.Id && p.End > now).OrderBy(p => p.Start).Take(2).ToListAsync(ct);
            var favourite = await db.LiveFavourites.AnyAsync(f => f.ProfileId == profile.Id && f.ChannelId == channel.Id, ct);
            return Results.Ok(new WatchDto(viewerId, $"/api/v1/livetv/watch/{viewerId}/{LiveSession.PlaylistName}",
                ToDto(channel, favourite, on.FirstOrDefault(p => p.Start <= now), on.FirstOrDefault(p => p.Start > now)),
                session.Plan.Describe(), (int)session.Rewind.TotalSeconds));
        });

        group.MapGet("/watching", (LiveTvViewers viewers) =>
            viewers.All.Select(v => new LiveWatcherDto(v.ChannelId, v.ChannelName, v.ProfileId, v.StartedAt)).ToList())
            .RequireAuthorization(FlexarrClaims.AdminPolicy);

        group.MapPost("/refresh", (LiveTvRefreshSignal signal) =>
        {
            signal.Request();
            return Results.Accepted();
        }).RequireAuthorization(FlexarrClaims.AdminPolicy);

        return api;
    }

    /// <summary>
    /// The playlist and segments, fetched by players that can't send a sign-in (Apple's, a browser's video element):
    /// the viewer id in the address is the capability, as with on-demand playback. Logos likewise, for image tags.
    /// </summary>
    public static RouteGroupBuilder MapLiveTvStreamEndpoints(this RouteGroupBuilder api)
    {
        var watch = api.MapGroup("/livetv/watch/{viewer}").WithTags("Live TV").AllowAnonymous();

        watch.MapGet($"/{LiveSession.PlaylistName}", (string viewer, LiveTvViewers viewers, HttpContext http) =>
        {
            if (viewers.Get(viewer) is not { } v || v.Session.ReadPlaylist() is not { } playlist)
            {
                return Results.NotFound();
            }

            v.Session.Touch(viewer);
            http.Response.Headers[HeaderNames.CacheControl] = "no-cache";
            return Results.Text(playlist, "application/vnd.apple.mpegurl");
        });

        watch.MapGet("/{segment}", (string viewer, string segment, LiveTvViewers viewers, HttpContext http) =>
        {
            if (viewers.Get(viewer) is not { } v || v.Session.SegmentPath(segment) is not { } path)
            {
                return Results.NotFound();
            }

            v.Session.Touch(viewer);
            // A segment never changes once written.
            http.Response.Headers[HeaderNames.CacheControl] = "private, max-age=3600";
            return Results.File(path, "video/mp2t");
        });

        // The player is still open (paused, perhaps, so it isn't fetching anything).
        watch.MapPost("/heartbeat", (string viewer, LiveTvViewers viewers) =>
        {
            if (viewers.Get(viewer) is not { } v)
            {
                return Results.NotFound();
            }

            v.Session.Touch(viewer);
            return Results.NoContent();
        });

        watch.MapDelete("/", async (string viewer, LiveTvViewers viewers, LiveStreams streams) =>
        {
            if (viewers.Remove(viewer) is { } v)
            {
                await streams.LeaveAsync(v.Session, v.Id);
            }

            return Results.NoContent();
        });

        api.MapGet("/livetv/channels/{id:guid}/logo", async (Guid id, FlexarrDbContext db, IOptionsMonitor<FlexarrOptions> options, XtreamClient client,
            HttpContext http, CancellationToken ct) =>
        {
            var o = options.CurrentValue;
            if (o.LiveTv?.Account is not { } account
                || await db.LiveChannels.AsNoTracking().Where(c => c.Id == id).Select(c => c.LogoUrl).FirstOrDefaultAsync(ct) is not { } logo
                || !Uri.TryCreate(logo, UriKind.Absolute, out var url))
            {
                return Results.NotFound();
            }

            // Cached on disk by the logo's address, so a changed logo is fetched afresh.
            var file = Path.Combine(o.ResolvedCacheDirectory, "livetv-logos", $"{Hash(logo)}");
            if (!File.Exists(file))
            {
                try
                {
                    if (await client.GetLogoAsync(account, url, ct) is not { } fetched)
                    {
                        return Results.NotFound();
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    await File.WriteAllBytesAsync(file + ".tmp", fetched.Bytes, ct);
                    await File.WriteAllTextAsync(file + ".type", fetched.ContentType, ct);
                    File.Move(file + ".tmp", file, overwrite: true);
                }
                catch (HttpRequestException)
                {
                    return Results.NotFound();
                }
            }

            var type = File.Exists(file + ".type") ? await File.ReadAllTextAsync(file + ".type", ct) : "image/png";
            http.Response.Headers[HeaderNames.CacheControl] = "public, max-age=86400";
            return Results.File(file, type.StartsWith("image/", StringComparison.Ordinal) ? type : "image/png");
        }).WithTags("Live TV").AllowAnonymous();

        return api;
    }

    /// <summary>The profile's favourite channels in number order, with what's on now and next (Home's Live TV row).</summary>
    internal static async Task<List<LiveChannelDto>> FavouritesOnNowAsync(FlexarrDbContext db, string profileId, DateTime now, CancellationToken ct)
    {
        var channels = await db.LiveChannels.AsNoTracking()
            .Where(c => db.LiveFavourites.Any(f => f.ProfileId == profileId && f.ChannelId == c.Id))
            .OrderBy(c => c.Number).ThenBy(c => c.Name).ToListAsync(ct);
        if (channels.Count == 0)
        {
            return [];
        }

        var ids = channels.Select(c => c.Id).ToList();
        var upcoming = (await db.LiveProgrammes.AsNoTracking()
                .Where(p => ids.Contains(p.ChannelId) && p.End > now && p.Start < now.AddHours(12))
                .OrderBy(p => p.Start).ToListAsync(ct))
            .ToLookup(p => p.ChannelId);
        return [.. channels.Select(c => ToDto(c, true, upcoming[c.Id].FirstOrDefault(p => p.Start <= now), upcoming[c.Id].FirstOrDefault(p => p.Start > now)))];
    }

    internal static LiveChannelDto ToDto(LiveChannel c, bool favourite, LiveProgramme? now, LiveProgramme? next) =>
        new(c.Id, c.Number, c.Name, c.Group, LogoUrl(c), favourite, now is null ? null : ToDto(now), next is null ? null : ToDto(next));

    private static async Task<HashSet<Guid>> FavouritesAsync(FlexarrDbContext db, string profileId, CancellationToken ct) =>
        [.. await db.LiveFavourites.AsNoTracking().Where(f => f.ProfileId == profileId).Select(f => f.ChannelId).ToListAsync(ct)];

    private static LiveProgrammeDto ToDto(LiveProgramme p) =>
        new(p.Id, p.Start, p.End, p.Title, p.Subtitle, p.Description, p.Category, p.Season, p.Episode, p.IsNew);

    private static string? LogoUrl(LiveChannel c) => c.LogoUrl is null ? null : $"/api/v1/livetv/channels/{c.Id}/logo";

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
}

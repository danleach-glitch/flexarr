using System.Security.Cryptography;
using System.Text;
using Flexarr.Api.Authentication;
using Flexarr.Api.Configuration;
using Flexarr.Api.Profiles;
using Flexarr.Api.Security;
using Flexarr.Core.Data;
using Flexarr.Core.Localization;
using Flexarr.Core.LiveTv;
using Flexarr.Streaming;
using Flexarr.Streaming.Live;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Flexarr.Api.Localization;

namespace Flexarr.Api.LiveTv;

/// <param name="Error">How the last refresh failed; to the main profile only since the release after 0.63.</param>
/// <param name="ChannelsWithoutListings">Channels with nothing listed from now on; null to any other profile, or without Live TV.</param>
/// <param name="DownloadError">Why Dispatcharr couldn't be asked to download its guides, last time it was. Main profile only.</param>
/// <param name="RefreshesFinished">Goes up as each refresh finishes, however it went, for telling when one pressed has. Main profile only.</param>
public sealed record LiveTvStatusDto(bool Configured, int Channels, bool Refreshing, DateTime? RefreshedAt, string? Error, int RewindMinutes,
    int? ChannelsWithoutListings = null, string? DownloadError = null, int? RefreshesFinished = null);

/// <param name="Recording">"scheduled" or "recording" when it's on the DVR's list (the guide says); otherwise null.</param>
/// <param name="Series">A series covers it (every showing of its title is recorded).</param>
public sealed record LiveProgrammeDto(long Id, DateTime Start, DateTime End, string Title, string? Subtitle, string? Description, string? Category,
    int? Season, int? Episode, bool IsNew, string? Recording = null, bool Series = false);

/// <param name="IsFavourite">Starred by the profile asking.</param>
/// <param name="FineForKids">Profiles with an age limit may watch it (absent from servers before 0.56).</param>
public sealed record LiveChannelDto(Guid Id, double Number, string Name, string? Group, string? LogoUrl, bool IsFavourite, LiveProgrammeDto? Now, LiveProgrammeDto? Next,
    bool FineForKids = false);

public sealed record ChannelKidsRequest(bool FineForKids);

public sealed record GuideChannelDto(Guid Id, double Number, string Name, string? Group, string? LogoUrl, bool IsFavourite, IReadOnlyList<LiveProgrammeDto> Programmes);

public sealed record GuideDto(DateTime From, DateTime Until, IReadOnlyList<GuideChannelDto> Channels);

/// <param name="Replaces">The viewer's previous channel, left first (with one connection, it has to go before this can start).</param>
/// <param name="Quality">
/// "original" or "reduced" to choose; by default a viewer at home gets the channel as it comes, and one away from home
/// (over Tailscale or the internet) a reduced version.
/// </param>
/// <param name="Audio">"surround" from a player that takes Dolby Digital (Apple's): a channel's 5.1 comes as it is.</param>
public sealed record WatchRequest(string? Replaces = null, string? Quality = null, string? Audio = null);

/// <param name="Playlist">The live HLS playlist; its address is the viewer's capability, so it needs no sign-in.</param>
/// <param name="Method">How the channel reaches the player ("Original", "Converted to 720p, deinterlaced").</param>
public sealed record WatchDto(string Viewer, string Playlist, LiveChannelDto Channel, string Method, int RewindSeconds);

public sealed record LiveWatcherDto(Guid ChannelId, string ChannelName, string ProfileId, DateTime StartedAt);

public static partial class LiveTvEndpoints
{
    /// <summary>In words, for the status and for testing a key in Settings.</summary>
    internal static string? DownloadErrorText(GuideDownloadProblem problem) => problem switch
    {
        GuideDownloadProblem.None => null,
        GuideDownloadProblem.KeyRefused => Loc.T("Dispatcharr refused the key. It needs an admin’s key, on Dispatcharr 0.20 or later."),
        GuideDownloadProblem.NotSupported => Loc.T("This version of Dispatcharr can’t be asked to download its guides."),
        GuideDownloadProblem.SourceFailed => Loc.T("Dispatcharr couldn’t download one of its guides. Check its guide sources."),
        GuideDownloadProblem.TimedOut => Loc.T("Dispatcharr is taking a while to download its guides. They’ll come in at the next refresh."),
        _ => Loc.T("Flexarr couldn’t ask Dispatcharr to download its guides."),
    };

    /// <summary>The last refresh's failure, in this request's language when it's Flexarr's own words.</summary>
    private static string? RefreshErrorText(LiveTvStatus status) => status.Problem switch
    {
        LiveTvRefreshProblem.GuideUnreadable => Loc.T("The guide from the Live TV source couldn’t be read."),
        LiveTvRefreshProblem.DidntFinish => Loc.T("The Live TV refresh didn’t finish."),
        _ => status.Error,
    };

    /// <summary>The status is asked for a lot, and the count only changes as programmes run out or a refresh finishes (which clears it).</summary>
    private static readonly TimeSpan MissingKeptFor = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan FormatKeptFor = TimeSpan.FromHours(12);
    private static readonly TimeSpan MaxGuideWindow = TimeSpan.FromHours(24);

    public static RouteGroupBuilder MapLiveTvEndpoints(this RouteGroupBuilder api)
    {
        // A kids profile has no Live TV (its status says it isn't there).
        var group = api.MapGroup("/livetv").WithTags("Live TV").NotForKids();

        api.MapGet("/livetv/status", async (IOptionsMonitor<FlexarrOptions> options, LiveTvStatus status, FlexarrDbContext db, CurrentProfile profile, TimeProvider clock, CancellationToken ct) =>
        {
            var o = options.CurrentValue.LiveTv;
            var channels = await db.LiveChannels.CountAsync(ct);
            var refreshed = status.RefreshedAt ?? await db.LiveChannels.MaxAsync(c => (DateTime?)c.UpdatedAt, ct);
            // To a profile with an age limit, Live TV is only there once a parent has picked channels for it; to a kids profile, never.
            var available = o?.Account is not null && !db.ServesKids && (channels > 0 || !db.IsLimited());
            var now = clock.GetUtcNow().UtcDateTime;
            // For the main profile, who can do something about them: the others' apps ask for the status too.
            var main = profile.IsMainProfile;
            int? missing = null;
            if (available && main)
            {
                missing = status.MissingCount(now, MissingKeptFor);
                if (missing is null)
                {
                    missing = await db.LiveChannels.CountAsync(c => !db.LiveProgrammes.Any(p => p.ChannelId == c.Id && p.End > now), ct);
                    status.RememberMissing(missing.Value, now);
                }
            }

            return new LiveTvStatusDto(available, channels, status.Refreshing, refreshed, main ? RefreshErrorText(status) : null, o?.RewindMinutes ?? 60, missing,
                main && !string.IsNullOrEmpty(o?.AdminApiKey) ? DownloadErrorText(status.DownloadProblem) : null, main ? status.RefreshesFinished : null);
        }).WithTags("Live TV");

        group.MapGet("/channels", async (FlexarrDbContext db, CurrentProfile profile, TimeProvider clock, CancellationToken ct) =>
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var channels = await db.LiveChannels.AsNoTracking().OrderBy(c => c.Number).ThenBy(c => c.Name).ToListAsync(ct);
            var favourites = await FavouritesAsync(db, profile.Id, ct);

            // What's on and what's next on each: the two programmes that end soonest after now. Only the channels it lists:
            // a profile with an age limit may have a handful of a source's thousands.
            var listed = db.IsLimited() ? channels.Select(c => c.Id).ToList() : null;
            var upcoming = (await db.LiveProgrammes.AsNoTracking()
                    .Where(p => p.End > now && p.Start < now.AddHours(12) && (listed == null || listed.Contains(p.ChannelId)))
                    .OrderBy(p => p.Start).ToListAsync(ct))
                .GroupBy(p => p.ChannelId).ToDictionary(g => g.Key, g => g.Take(2).ToList());
            var marks = await DvrEndpoints.Marks.LoadAsync(db, now, now.AddHours(12), ct);
            return channels.Select(c =>
            {
                var list = upcoming.GetValueOrDefault(c.Id) ?? [];
                var on = list.FirstOrDefault(p => p.Start <= now);
                var next = list.FirstOrDefault(p => p.Start > now);
                return ToDto(c, favourites.Contains(c.Id), on, next, marks);
            }).ToList();
        });

        // The guide from `from` (default half an hour ago) for `hours` (default 6, 1 to 24). Optional, all absent from
        // servers before the release after 0.62.1, and each leaves the answer as it was when left out:
        //   channels=<id>,<id>  only those channels (400 if one isn't a channel id);
        //   group=<name>        only channels in that group (exactly as the channel's `group` says);
        //   favourites=true     only the asking profile's favourites;
        //   descriptions=false  programmes without their description (null); GET /livetv/programmes/{id} has it.
        // The filters combine (a channel has to pass each one given). Every answer carries a weak ETag and answers 304 to
        // If-None-Match when nothing in it changed; ask with the same `from` (rounded, say to the half-hour) to get one.
        group.MapGet("/guide", async (DateTime? from, double? hours, string? channels, [FromQuery(Name = "group")] string? channelGroup, bool? favourites,
            bool? descriptions, FlexarrDbContext db, CurrentProfile profile, TimeProvider clock, HttpContext http,
            IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json, CancellationToken ct) =>
        {
            if (!GuideChannels.TryParse(channels, channelGroup, favourites, out var only))
            {
                return Results.BadRequest();
            }

            var start = (from?.ToUniversalTime() ?? clock.GetUtcNow().UtcDateTime.AddMinutes(-30));
            var window = TimeSpan.FromHours(Math.Clamp(hours ?? 6, 1, MaxGuideWindow.TotalHours));
            var until = start + window;
            var shown = await db.LiveChannels.AsNoTracking().OrderBy(c => c.Number).ThenBy(c => c.Name).ToListAsync(ct);
            var favouriteIds = await FavouritesAsync(db, profile.Id, ct);
            if (only.Narrows)
            {
                shown = [.. shown.Where(c => only.Keeps(c, favouriteIds))];
            }

            var listed = db.IsLimited() || only.Narrows ? shown.Select(c => c.Id).ToList() : null;
            var programmes = (await db.LiveProgrammes.AsNoTracking()
                    .Where(p => p.End > start && p.Start < until && (listed == null || listed.Contains(p.ChannelId))).OrderBy(p => p.Start).ToListAsync(ct))
                .ToLookup(p => p.ChannelId);
            var marks = await DvrEndpoints.Marks.LoadAsync(db, start, until, ct);
            var withDescriptions = descriptions != false;
            var guide = new GuideDto(start, until, [.. shown.Select(c =>
                new GuideChannelDto(c.Id, c.Number, c.Name, c.Group, LogoUrl(c), favouriteIds.Contains(c.Id),
                    [.. programmes[c.Id].Select(p => ToDto(p) with
                    {
                        Recording = marks.StateOf(p), Series = marks.InSeries(p), Description = withDescriptions ? p.Description : null,
                    })]))]);
            return JsonWithETag(guide, http, json.Value.SerializerOptions);
        });

        MapProgrammeEndpoint(group);

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
            LiveStreams streams, LiveTvViewers viewers, DvrService dvr, CurrentProfile profile, IMemoryCache memory, TimeProvider clock, HttpContext http,
            CancellationToken ct) =>
        {
            var settings = options.CurrentValue;
            if (settings.LiveTv is not { Account: { } account } liveTv)
            {
                return Problems.Of("Live TV isn’t set up. Connect Dispatcharr in Settings › Connections.", status: StatusCodes.Status409Conflict);
            }

            if (await db.LiveChannels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct) is not { } channel)
            {
                return Results.NotFound();
            }

            // Every stream is recording something else: recording wins. The player offers that channel, or stopping it.
            if (await dvr.BlockingAsync(channel.Id, ct) is [var blocking, ..] all)
            {
                var names = all.Select(a => Loc.T("{title} on {channel}", new { title = a.Title, channel = a.ChannelName }))
                    .Aggregate((first, second) => Loc.T("{first} and {second}", new { first, second }));
                return Results.Problem(
                    Loc.T("{names} {count, plural, one {is} other {are}} recording, using every stream there is. Watch {channel}, or stop the recording.",
                        new { names, count = all.Count, channel = blocking.ChannelName }),
                    statusCode: StatusCodes.Status409Conflict, title: Loc.T("Recording", ctx: "live-tv"), extensions: new Dictionary<string, object?>
                    {
                        ["recordingId"] = blocking.Id,
                        ["channelId"] = blocking.ChannelId,
                        ["channelName"] = blocking.ChannelName,
                        ["recordingTitle"] = blocking.Title,
                        ["until"] = blocking.Until,
                    });
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
            // Away from home, a reduced version: a session of its own, beside the original one if someone at home is
            // watching the same channel (both read the one connection Dispatcharr holds for it).
            var reduced = Reduced(request?.Quality, NetworkZones.Of(http));
            // A player that takes Dolby (Apple's) gets the channel's 5.1 as it comes, in a session of its own beside
            // the stereo one a browser needs; on a channel known not to be Dolby, the two would be the same, so it
            // shares. Away from home it's stereo, for the bandwidth.
            var formatKey = $"livetv-format:{channel.Id}";
            var surround = !reduced && string.Equals(request?.Audio, "surround", StringComparison.OrdinalIgnoreCase)
                && !(memory.TryGetValue(formatKey, out LiveFormat? known) && known is not null && !LivePlan.HasSurround(known));
            LiveSession session;
            try
            {
                session = await streams.JoinAsync(channel.Id.ToString("N") + (reduced ? ":away" : surround ? ":surround" : ""), source, async token =>
                {
                    if (!memory.TryGetValue(formatKey, out LiveFormat? format) || format is null)
                    {
                        format = await LiveProbe.ProbeAsync(ffprobe, source, token);
                        memory.Set(formatKey, format, FormatKeptFor);
                    }

                    return reduced ? LivePlan.ForAway(format) : LivePlan.For(format, surround);
                }, TimeSpan.FromMinutes(Math.Clamp(liveTv.RewindMinutes, 1, 240)), viewerId, ct);
            }
            catch (LiveStreamException ex)
            {
                // A channel that wouldn't start is probed afresh next time: its format may be what changed.
                memory.Remove($"livetv-format:{channel.Id}");
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable, title: Loc.T("The channel couldn’t be played"));
            }

            viewers.Add(new LiveViewer(viewerId, channel.Id, channel.Name, profile.Id, profile.SignInId, session));
            var now = clock.GetUtcNow().UtcDateTime;
            // Remembered for Home, where the profile's last channel comes first.
            if (await db.LiveLastChannels.FindAsync([profile.Id], ct) is { } last)
            {
                (last.ChannelId, last.WatchedAt) = (channel.Id, now);
            }
            else
            {
                db.LiveLastChannels.Add(new LiveLastChannel { ProfileId = profile.Id, ChannelId = channel.Id, WatchedAt = now });
            }

            await db.SaveChangesAsync(ct);
            var on = await db.LiveProgrammes.AsNoTracking().Where(p => p.ChannelId == channel.Id && p.End > now).OrderBy(p => p.Start).Take(2).ToListAsync(ct);
            var favourite = await db.LiveFavourites.AnyAsync(f => f.ProfileId == profile.Id && f.ChannelId == channel.Id, ct);
            return Results.Ok(new WatchDto(viewerId, $"/api/v1/livetv/watch/{viewerId}/{LiveSession.PlaylistName}",
                ToDto(channel, favourite, on.FirstOrDefault(p => p.Start <= now), on.FirstOrDefault(p => p.Start > now)),
                session.Plan.Describe(), (int)session.Rewind.TotalSeconds));
        });

        group.MapGet("/watching", (LiveTvViewers viewers) =>
            viewers.All.Select(v => new LiveWatcherDto(v.ChannelId, v.ChannelName, v.ProfileId, v.StartedAt)).ToList())
            .RequireAuthorization(FlexarrClaims.AdminPolicy);

        // The main profile even while it's locked, which at home with sign-in only away from home is any device on the
        // network. It's safe to open up: only one refresh waits at a time, and how often Dispatcharr's sources are asked has its own limit.
        group.MapPost("/refresh", (LiveTvRefreshSignal signal) =>
        {
            signal.Request(download: true);
            return Results.Accepted();
        }).RequireAuthorization(FlexarrClaims.MainProfilePolicy);

        // The channels profiles with an age limit may watch, picked by a parent.
        group.MapPut("/channels/{id:guid}/kids", async (Guid id, ChannelKidsRequest request, FlexarrDbContext db, CancellationToken ct) =>
        {
            if (await db.LiveChannels.Where(c => c.Id == id).ExecuteUpdateAsync(u => u.SetProperty(c => c.FineForKids, request.FineForKids), ct) == 0)
            {
                return Results.NotFound();
            }

            LibraryVisibility.KidsSettingsChanged();
            return Results.NoContent();
        })
            .RequireAuthorization(FlexarrClaims.AdminPolicy);

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
                // Stopped for a recording: gone, with why.
                return viewers.EndedBecause(viewer) is { } reason
                    ? Results.Problem(reason, statusCode: StatusCodes.Status410Gone, title: Loc.T("The channel was stopped"))
                    : Results.NotFound();
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
    /// <summary>Home's Live TV row: the channel the profile watched last, then its favourites in number order.</summary>
    internal static async Task<List<LiveChannelDto>> HomeChannelsAsync(FlexarrDbContext db, string profileId, DateTime now, CancellationToken ct)
    {
        var favourites = await FavouritesAsync(db, profileId, ct);
        var lastId = await db.LiveLastChannels.AsNoTracking().Where(l => l.ProfileId == profileId).Select(l => (Guid?)l.ChannelId).FirstOrDefaultAsync(ct);
        var channels = await db.LiveChannels.AsNoTracking()
            .Where(c => favourites.Contains(c.Id) || c.Id == lastId)
            .OrderBy(c => c.Number).ThenBy(c => c.Name).ToListAsync(ct);
        if (channels.Count == 0)
        {
            return [];
        }

        channels = [.. channels.OrderByDescending(c => c.Id == lastId)];

        var ids = channels.Select(c => c.Id).ToList();
        var upcoming = (await db.LiveProgrammes.AsNoTracking()
                .Where(p => ids.Contains(p.ChannelId) && p.End > now && p.Start < now.AddHours(12))
                .OrderBy(p => p.Start).ToListAsync(ct))
            .ToLookup(p => p.ChannelId);
        var marks = await DvrEndpoints.Marks.LoadAsync(db, now, now.AddHours(12), ct);
        return [.. channels.Select(c => ToDto(c, favourites.Contains(c.Id), marks, upcoming[c.Id].FirstOrDefault(p => p.Start <= now), upcoming[c.Id].FirstOrDefault(p => p.Start > now)))];
    }

    /// <summary>Whether a viewer gets the reduced version: as asked, else when they're away from home.</summary>
    internal static bool Reduced(string? quality, NetworkZone zone) => quality?.ToLowerInvariant() switch
    {
        "original" => false,
        "reduced" => true,
        _ => zone != NetworkZone.Local,
    };

    internal static LiveChannelDto ToDto(LiveChannel c, bool favourite, LiveProgramme? now, LiveProgramme? next, DvrEndpoints.Marks? marks = null) =>
        new(c.Id, c.Number, c.Name, c.Group, LogoUrl(c), favourite, now is null ? null : Marked(now, marks), next is null ? null : Marked(next, marks), c.FineForKids);

    private static LiveChannelDto ToDto(LiveChannel c, bool favourite, DvrEndpoints.Marks marks, LiveProgramme? now, LiveProgramme? next) =>
        ToDto(c, favourite, now, next, marks);

    /// <summary>A programme with what the DVR has for it: set to record, recording, covered by a series.</summary>
    private static LiveProgrammeDto Marked(LiveProgramme p, DvrEndpoints.Marks? marks) =>
        marks is null ? ToDto(p) : ToDto(p) with { Recording = marks.StateOf(p), Series = marks.InSeries(p) };

    private static async Task<HashSet<Guid>> FavouritesAsync(FlexarrDbContext db, string profileId, CancellationToken ct) =>
        [.. await db.LiveFavourites.AsNoTracking().Where(f => f.ProfileId == profileId).Select(f => f.ChannelId).ToListAsync(ct)];

    private static LiveProgrammeDto ToDto(LiveProgramme p) =>
        new(p.Id, p.Start, p.End, p.Title, p.Subtitle, p.Description, p.Category, p.Season, p.Episode, p.IsNew);

    /// <summary>
    /// The channel's logo through Flexarr. The version is the logo's own address, so a new logo (the community one, or
    /// one changed in Dispatcharr) is a new address and browsers don't keep showing the old one for a day.
    /// </summary>
    private static string? LogoUrl(LiveChannel c) => c.LogoUrl is null ? null : $"/api/v1/livetv/channels/{c.Id}/logo?v={Hash(c.LogoUrl)[..10]}";

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
}

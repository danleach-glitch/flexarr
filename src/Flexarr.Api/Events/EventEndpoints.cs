using System.Text.Json;
using System.Text.Json.Serialization;
using Flexarr.Api.Profiles;
using Microsoft.AspNetCore.Http.Features;
using Flexarr.Api.Localization;

namespace Flexarr.Api.Events;

/// <summary>
/// What's playing where, as it changes: a stream of server-sent events for Home Assistant and other automations
/// ("dim the lights when a film starts, bring them up at the credits").
/// </summary>
public static class EventEndpoints
{
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(20);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static RouteGroupBuilder MapEventEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/events", StreamAsync).WithTags("Events");
        return api;
    }

    /// <summary>
    /// "event: playback" with a <see cref="PlaybackEventDto"/> as JSON for each change, "event: added" with a
    /// <see cref="LibraryAddedEventDto"/> for each film, episode, album or book new to the library (absent from servers
    /// before 0.64), and a comment every twenty seconds so proxies keep it open. It shows everyone's watching, so only the API key or a trusted sign-in has it.
    /// </summary>
    private static async Task StreamAsync(HttpContext http, CurrentProfile current, PlaybackEvents events, Core.Data.FlexarrDbContext db, CancellationToken ct)
    {
        // Titles from libraries hidden from this profile are told as playing, but not what they are.
        var hidden = db.HiddenLibraryIds.ToHashSet();
        var kidsSettings = LibraryVisibility.KidsSettingsVersion;
        if (!current.IsTrusted)
        {
            await Problems.Of("Only the API key or a signed-in browser or device can follow what’s playing.", status: 403).ExecuteAsync(http);
            return;
        }

        // Nor a kids profile: what the household's other screens play, and when, isn't a child's to follow.
        if (db.ServesKids)
        {
            await Problems.Of("That isn’t available on a kids profile.", status: 403).ExecuteAsync(http);
            return;
        }

        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.Headers.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        using var listener = events.Listen(added: true);
        await http.Response.WriteAsync(": connected\n\n", ct);
        await http.Response.Body.FlushAsync(ct);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    wait.CancelAfter(Heartbeat);
                    await Task.WhenAny(listener.Events.WaitToReadAsync(wait.Token).AsTask(), listener.Added.WaitToReadAsync(wait.Token).AsTask());
                    // Lets go of the wait on the other kind, so a stream open for days doesn't gather them.
                    await wait.CancelAsync();
                }

                ct.ThrowIfCancellationRequested();
                if (listener.Events.Count == 0 && listener.Added.Count == 0)
                {
                    await http.Response.WriteAsync(": ping\n\n", ct);
                    await http.Response.Body.FlushAsync(ct);
                    continue;
                }

                while (listener.Events.TryRead(out var playback))
                {
                    // An age limit or a library hidden since the stream opened applies from the next event on.
                    if (kidsSettings != LibraryVisibility.KidsSettingsVersion)
                    {
                        kidsSettings = LibraryVisibility.KidsSettingsVersion;
                        await LibraryVisibility.ApplyAsync(db, current.Id, ct);
                        hidden = db.HiddenLibraryIds.ToHashSet();
                        if (db.ServesKids)
                        {
                            return; // made a kids profile while it listened
                        }
                    }

                    if (playback.Item is { } item && (hidden.Contains(item.LibraryId)
                        || db.IsLimited() && item.Id != Guid.Empty && !await LibraryVisibility.CanSeeAsync(db, item.LibraryId, item.Id, ct)))
                    {
                        // Its length, or the chapter of a book being heard, could say what it is as surely as its name.
                        playback = playback with { Item = item.Private(), DurationSeconds = 0, Chapter = null };
                    }

                    await http.Response.WriteAsync($"event: playback\ndata: {JsonSerializer.Serialize(playback, Json)}\n\n", ct);
                }

                while (listener.Added.TryRead(out var added))
                {
                    if (kidsSettings != LibraryVisibility.KidsSettingsVersion)
                    {
                        kidsSettings = LibraryVisibility.KidsSettingsVersion;
                        await LibraryVisibility.ApplyAsync(db, current.Id, ct);
                        hidden = db.HiddenLibraryIds.ToHashSet();
                        if (db.ServesKids)
                        {
                            return;
                        }
                    }

                    // Only what this profile may see: unlike playback, there's nothing to tell without saying what it is.
                    if (hidden.Contains(added.Item.LibraryId) || db.IsLimited() && !await LibraryVisibility.CanSeeAsync(db, added.Item.LibraryId, added.Item.Id, ct))
                    {
                        continue;
                    }

                    await http.Response.WriteAsync($"event: added\ndata: {JsonSerializer.Serialize(added, Json)}\n\n", ct);
                }

                await http.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // The listener went away.
        }
    }
}

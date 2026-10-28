using System.Text.Json;
using System.Text.Json.Serialization;
using Flexarr.Api.Profiles;
using Microsoft.AspNetCore.Http.Features;

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
    /// "event: playback" with a <see cref="PlaybackEventDto"/> as JSON for each change, and a comment every twenty
    /// seconds so proxies keep it open. It shows everyone's watching, so only the API key or a trusted sign-in has it.
    /// </summary>
    private static async Task StreamAsync(HttpContext http, CurrentProfile current, PlaybackEvents events, Core.Data.FlexarrDbContext db, CancellationToken ct)
    {
        // Titles from libraries hidden from this profile are told as playing, but not what they are.
        var hidden = db.HiddenLibraryIds.ToHashSet();
        if (!current.IsTrusted)
        {
            await Results.Problem("Only the API key or a signed-in browser or device can follow what's playing.", statusCode: 403).ExecuteAsync(http);
            return;
        }

        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.Headers.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        using var listener = events.Listen();
        await http.Response.WriteAsync(": connected\n\n", ct);
        await http.Response.Body.FlushAsync(ct);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(Heartbeat);
                try
                {
                    await listener.Events.WaitToReadAsync(wait.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await http.Response.WriteAsync(": ping\n\n", ct);
                    await http.Response.Body.FlushAsync(ct);
                    continue;
                }

                while (listener.Events.TryRead(out var playback))
                {
                    if (playback.Item is { } item && hidden.Contains(item.LibraryId))
                    {
                        playback = playback with { Item = item.Private() };
                    }

                    await http.Response.WriteAsync($"event: playback\ndata: {JsonSerializer.Serialize(playback, Json)}\n\n", ct);
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

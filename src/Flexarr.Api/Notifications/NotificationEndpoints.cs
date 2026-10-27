using System.Text.RegularExpressions;
using Flexarr.Api.Profiles;
using Flexarr.Core.Data;
using Flexarr.Core.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Notifications;

/// <param name="Token">Apple's push token for the phone, in hex.</param>
/// <param name="Environment">"production", or "sandbox" for a development build.</param>
/// <param name="Key">Base64 of the 32-byte key the phone made to read its notifications with.</param>
/// <param name="TimeZone">The phone's time zone (an IANA name), for Upcoming at its hour.</param>
/// <param name="UseCurrentProfile">
/// Notifications for the profile the phone is on now. Otherwise a phone already registered keeps its profile: a
/// phone's sign-in follows whichever profile was last chosen on it, and someone else watching on the shared phone
/// mustn't take its notifications over.
/// </param>
public sealed record NotificationDeviceRequest(
    string Token, string Environment, string Key, string? TimeZone,
    bool Films = true, bool Shows = true, bool Podcasts = true, bool Recordings = true, bool Requests = true,
    bool Upcoming = false, int UpcomingHour = 9, bool UseCurrentProfile = false);

/// <param name="Relay">False when notifications are turned off on the server.</param>
public sealed record NotificationDeviceDto(
    string ProfileId, string? ProfileName, bool Films, bool Shows, bool Podcasts, bool Recordings, bool Requests,
    bool Upcoming, int UpcomingHour, string TimeZone, bool Relay);

/// <summary>A phone signing up for notifications, changing what it hears about, or stopping (see docs/notifications.md).</summary>
public static partial class NotificationEndpoints
{
    public static RouteGroupBuilder MapNotificationEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/notifications/device").WithTags("Notifications");

        group.MapGet("/", async (CurrentProfile current, FlexarrDbContext db, PushRelay relay, CancellationToken ct) =>
            await FindAsync(db, current, ct) is { } device ? Results.Ok(await ToDtoAsync(db, device, relay, ct)) : Results.NotFound());

        group.MapPut("/", async (NotificationDeviceRequest request, CurrentProfile current, FlexarrDbContext db, PushRelay relay, CancellationToken ct) =>
        {
            byte[] key;
            try
            {
                key = Convert.FromBase64String(request.Key ?? "");
            }
            catch (FormatException)
            {
                key = [];
            }

            var errors = new Dictionary<string, string[]>();
            if (!HexToken().IsMatch(request.Token ?? ""))
            {
                errors["token"] = ["Use the device token Apple gave, in hex."];
            }

            if (request.Environment is not ("production" or "sandbox"))
            {
                errors["environment"] = ["Use \"production\" or \"sandbox\"."];
            }

            if (key.Length != 32)
            {
                errors["key"] = ["Use 32 random bytes, in base64."];
            }

            if (request.UpcomingHour is < 0 or > 23)
            {
                errors["upcomingHour"] = ["Use an hour from 0 to 23."];
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var token = request.Token!.ToLowerInvariant();
            var id = current.SignInId ?? $"token:{token}";
            // A phone has one registration: the same token under another sign-in is this phone signed in again.
            await db.NotificationDevices.Where(d => d.Token == token && d.Id != id).ExecuteDeleteAsync(ct);
            var device = await db.NotificationDevices.FindAsync([id], ct);
            if (device is null)
            {
                device = new NotificationDevice { Id = id, ProfileId = current.Id, Token = token, Environment = request.Environment, Key = key };
                db.NotificationDevices.Add(device);
            }

            device.SignInId = current.SignInId;
            if (request.UseCurrentProfile)
            {
                device.ProfileId = current.Id;
            }

            device.Token = token;
            device.Environment = request.Environment;
            device.Key = key;
            device.TimeZone = TimeZoneInfo.TryFindSystemTimeZoneById(request.TimeZone ?? "", out _) ? request.TimeZone! : "UTC";
            device.Films = request.Films;
            device.Shows = request.Shows;
            device.Podcasts = request.Podcasts;
            device.Recordings = request.Recordings;
            device.Requests = request.Requests;
            device.Upcoming = request.Upcoming;
            device.UpcomingHour = request.UpcomingHour;
            device.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(await ToDtoAsync(db, device, relay, ct));
        });

        group.MapDelete("/", async (CurrentProfile current, FlexarrDbContext db, CancellationToken ct) =>
        {
            if (await FindAsync(db, current, ct) is { } device)
            {
                db.NotificationDevices.Remove(device);
                await db.SaveChangesAsync(ct);
            }

            return Results.NoContent();
        });

        group.MapPost("/test", async (CurrentProfile current, FlexarrDbContext db, PushRelay relay, CancellationToken ct) =>
        {
            if (await FindAsync(db, current, ct) is not { } device)
            {
                return Results.NotFound();
            }

            var (outcome, reason) = await relay.SendAsync(device, new PushMessage("Notifications are on", "This is how Flexarr will tell you something new is ready.", "flexarr.test"), null, ct);
            if (outcome == PushOutcome.Gone)
            {
                db.NotificationDevices.Remove(device);
                await db.SaveChangesAsync(ct);
            }

            return outcome == PushOutcome.Sent
                ? Results.Accepted()
                : Results.Problem(outcome == PushOutcome.Gone ? "Apple no longer recognises this phone. Turn notifications off and on again." : reason, statusCode: 502);
        });

        return api;
    }

    /// <summary>
    /// This phone's registration, by its sign-in. A phone signed in with the server's API key has no sign-in of its
    /// own, so it's taken to be the latest registration made that way (there's rarely more than one).
    /// </summary>
    private static async Task<NotificationDevice?> FindAsync(FlexarrDbContext db, CurrentProfile current, CancellationToken ct) =>
        current.SignInId is { } signIn
            ? await db.NotificationDevices.FirstOrDefaultAsync(d => d.Id == signIn, ct)
            : await db.NotificationDevices.Where(d => d.SignInId == null).OrderByDescending(d => d.UpdatedAt).FirstOrDefaultAsync(ct);

    private static async Task<NotificationDeviceDto> ToDtoAsync(FlexarrDbContext db, NotificationDevice d, PushRelay relay, CancellationToken ct)
    {
        var name = await db.Profiles.Where(p => p.Id == d.ProfileId).Select(p => p.Name).FirstOrDefaultAsync(ct);
        return new NotificationDeviceDto(d.ProfileId, name, d.Films, d.Shows, d.Podcasts, d.Recordings, d.Requests, d.Upcoming, d.UpcomingHour, d.TimeZone, relay.IsEnabled);
    }

    [GeneratedRegex("^[0-9a-fA-F]{64,200}$")]
    private static partial Regex HexToken();
}

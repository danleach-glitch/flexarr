using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Flexarr.Api.Profiles;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Import;

public sealed record PlexAvatarMatch(string Profile, string PlexUser);

public sealed record PlexAvatarResult(IReadOnlyList<PlexAvatarMatch> Matched, IReadOnlyList<string> UnmatchedPlexUsers);

/// <summary>
/// Copies Plex Home users' profile pictures onto the Flexarr profiles with the same names. The Plex server's own
/// sign-in is read from its settings for the one plex.tv request and is never stored.
/// </summary>
public sealed class PlexAvatars(HttpClient http, FlexarrDbContext db, ProfileImages images)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<PlexAvatarResult> ImportAsync(string? databasePath, CancellationToken ct)
    {
        var token = await FindTokenAsync(PlexImporter.Resolve(databasePath), ct)
            ?? throw new InvalidOperationException("Couldn't find Plex's sign-in on this computer. Is Plex Media Server installed here and signed in to plex.tv?");

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://plex.tv/api/v2/home/users"));
        request.Headers.Add("X-Plex-Token", token);
        request.Headers.Add("X-Plex-Client-Identifier", "flexarr");
        request.Headers.Add("X-Plex-Product", "Flexarr");
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var home = await response.Content.ReadFromJsonAsync<PlexHome>(Json, ct) ?? new PlexHome([]);

        var profiles = await db.Profiles.ToListAsync(ct);
        var matched = new List<PlexAvatarMatch>();
        var unmatched = new List<string>();
        foreach (var user in home.Users.Where(u => !string.IsNullOrEmpty(u.Thumb)))
        {
            var profile = profiles.FirstOrDefault(p => SameName(p.Name, user.Title) || SameName(p.Name, user.Username))
                // The Plex server's owner is most likely whoever owns the main profile.
                ?? (user.Admin && !home.Users.Any(u => u != user && profiles.Any(p => p.IsMain && (SameName(p.Name, u.Title) || SameName(p.Name, u.Username))))
                    ? profiles.FirstOrDefault(p => p.IsMain && !matched.Any(m => m.Profile == p.Name)) : null);
            if (profile is null || matched.Any(m => m.Profile == profile.Name))
            {
                unmatched.Add(user.Title ?? user.Username ?? "Plex user");
                continue;
            }

            using var picture = await http.GetAsync(new Uri(user.Thumb!), HttpCompletionOption.ResponseHeadersRead, ct);
            if (!picture.IsSuccessStatusCode || picture.Content.Headers.ContentLength > ProfileImages.MaxUploadBytes)
            {
                continue;
            }

            await using var body = await picture.Content.ReadAsStreamAsync(ct);
            if (await images.SaveAsync(profile.Id, body, ct))
            {
                profile.ImageUpdatedAt = DateTime.UtcNow;
                matched.Add(new PlexAvatarMatch(profile.Name, user.Title ?? user.Username ?? ""));
            }
        }

        await db.SaveChangesAsync(ct);
        return new PlexAvatarResult(matched, unmatched);
    }

    private static bool SameName(string profile, string? plex) =>
        plex is not null && string.Equals(profile.Trim(), plex.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Preferences.xml beside Plex's data (Linux, Docker, Windows), or the macOS preferences domain.</summary>
    private static async Task<string?> FindTokenAsync(string? databasePath, CancellationToken ct)
    {
        // .../Plex Media Server/Plug-in Support/Databases/com.plexapp.plugins.library.db
        var plexFolder = databasePath is null ? null : Directory.GetParent(databasePath)?.Parent?.Parent?.FullName;
        if (plexFolder is not null && Path.Combine(plexFolder, "Preferences.xml") is var xml && File.Exists(xml))
        {
            var token = XDocument.Load(xml).Root?.Attribute("PlexOnlineToken")?.Value;
            if (!string.IsNullOrEmpty(token))
            {
                return token;
            }
        }

        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }

        var start = new ProcessStartInfo("/usr/bin/defaults") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { "read", "com.plexapp.plexmediaserver", "PlexOnlineToken" })
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var output = (await process.StandardOutput.ReadToEndAsync(ct)).Trim();
        await process.WaitForExitAsync(ct);
        return process.ExitCode == 0 && output.Length > 0 ? output : null;
    }

    private sealed record PlexHome([property: JsonPropertyName("users")] IReadOnlyList<PlexHomeUser> Users);

    private sealed record PlexHomeUser(string? Title, string? Username, string? Thumb, bool Admin);
}

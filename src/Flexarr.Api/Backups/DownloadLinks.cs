using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Flexarr.Api.Backups;

/// <summary>
/// One-use links to download a backup as it is. Downloading one asks for the admin PIN again (it holds every key), and
/// a plain link can't carry a PIN, so the PIN buys a link first; the browser then saves the file straight to disk
/// rather than holding it all in the page. A link works once, for one backup, for a minute.
/// </summary>
public sealed class DownloadLinks(TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, (string Name, DateTimeOffset Expires, string? Passphrase)> _links = new(StringComparer.Ordinal);

    /// <summary>
    /// A new link's ticket for backup <paramref name="name"/>; with <paramref name="passphrase"/>, the link downloads
    /// it protected by that passphrase (kept in memory with the ticket, for its minute at most).
    /// </summary>
    public string Issue(string name, string? passphrase = null)
    {
        var now = clock.GetUtcNow();
        foreach (var (ticket, link) in _links)
        {
            if (link.Expires <= now)
            {
                _links.TryRemove(ticket, out _);
            }
        }

        var issued = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        _links[issued] = (name, now + Lifetime, passphrase);
        return issued;
    }

    /// <summary>Uses up <paramref name="ticket"/>: true if it was issued for <paramref name="name"/> and hasn't expired.</summary>
    public bool Redeem(string? ticket, string name) => Redeem(ticket, name, out _);

    /// <summary>As <see cref="Redeem(string?, string)"/>, also giving the passphrase the link was issued with, if any.</summary>
    public bool Redeem(string? ticket, string name, out string? passphrase)
    {
        passphrase = null;
        if (ticket is null || !_links.TryRemove(ticket, out var link) || link.Name != name || link.Expires <= clock.GetUtcNow())
        {
            return false;
        }

        passphrase = link.Passphrase;
        return true;
    }
}

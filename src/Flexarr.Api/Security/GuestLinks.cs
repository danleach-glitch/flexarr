using Flexarr.Api.Profiles;

namespace Flexarr.Api.Security;

/// <summary>
/// Quality for slow links: a family guest whose device reaches this server only through Tailscale's shared relays
/// (DERP, which slow down to about 2 Mbps) gets streams capped to fit, so they play rather than buffer. A direct
/// connection, or one through this server's own relay, isn't capped.
/// </summary>
public sealed class GuestLinks(IHttpContextAccessor http, CurrentProfile current, ITailnet tailnet)
{
    /// <summary>What fits through Tailscale's shared relays with room to spare.</summary>
    public const long SlowLinkBitrate = 1_500_000;

    /// <summary>The bitrate this request's streams are held to, or null for no cap.</summary>
    public async Task<long?> CapAsync(CancellationToken ct)
    {
        if (!current.IsGuest || http.HttpContext is not { } context || RequestPeer.Of(context) is not { } peer)
        {
            return null;
        }

        var address = (peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer).ToString();
        TailnetSnapshot? snapshot;
        try
        {
            // Playback never waits long on Tailscale for this: no answer in two seconds, no cap (the snapshot carries
            // on in the background and is kept for the next request).
            snapshot = await tailnet.SnapshotAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2), ct);
        }
        catch (TimeoutException)
        {
            return null;
        }

        return snapshot?.Peers.FirstOrDefault(p => p.Address == address)?.Path == TailnetPath.Derp ? SlowLinkBitrate : null;
    }
}

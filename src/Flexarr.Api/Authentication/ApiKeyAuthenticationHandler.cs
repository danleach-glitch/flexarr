using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Flexarr.Api.Configuration;
using Flexarr.Api.Profiles;
using Flexarr.Api.Security;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Authentication;

/// <summary>
/// Flexarr's sign-in, checked in this order:
/// <list type="number">
/// <item>A key, as an <c>X-Api-Key</c> header or <c>apikey</c> query parameter: the server's API key (scripts, *arr
/// apps, Apple TVs set up before per-device sign-ins), or a device's own token (an Apple TV once it's connected).</item>
/// <item>The web app's session cookie, which a browser gets in exchange for the API key (so it never keeps the key),
/// or over Tailscale for choosing the main profile with its PIN (that one only with the same Tailscale login). Each
/// browser has its own, which can be signed out from Settings.</item>
/// <item>A Tailscale identity: <c>tailscale serve</c> adds the sender's verified login to requests from your tailnet.
/// Only believed when the connection came from this machine, for a tailnet address that Tailscale confirms belongs to
/// that login, and mapped to the profile linked to that Tailscale account. It never manages the server: that takes
/// the browser's own sign-in.</item>
/// <item>The home network, when "only away from home" is chosen. Never applies to tunnelled traffic.</item>
/// </list>
/// Then it works out the profile the request acts for and whether it may manage the server (see
/// <see cref="CurrentProfile"/>). Writes that use the cookie, the home network or Tailscale must also come from the
/// web app itself (see <see cref="RequestForgery"/>).
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    IOptionsMonitor<FlexarrOptions> flexarrOptions,
    SignIns signIns,
    AdminUnlocks unlocks,
    ITailnetWhois whois,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, loggerFactory, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";
    public const string QueryName = "apikey";
    public const string TailscaleLoginHeader = "Tailscale-User-Login";
    public const string SessionCookieName = "flexarr.session";
    public const string RequestedWithHeader = "X-Requested-With";

    /// <summary>Sign-in methods, as the <see cref="FlexarrClaims.Method"/> claim.</summary>
    public static class Methods
    {
        public const string ApiKey = "api-key";
        public const string Device = "device";
        public const string Session = "session";
        public const string Tailscale = "tailscale";
        public const string Local = "local";
    }

    /// <summary>Browsers still carrying the cookie earlier versions gave out, by address and browser, while they swap it.</summary>
    private static readonly ConcurrentDictionary<string, Lazy<(SignIn SignIn, string Token)>> Upgrading = new(StringComparer.Ordinal);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var options = flexarrOptions.CurrentValue;

        string? provided = Request.Headers[HeaderName].FirstOrDefault() ?? Request.Query[QueryName].FirstOrDefault();
        if (!string.IsNullOrEmpty(provided))
        {
            if (IsApiKey(provided, options.ApiKey))
            {
                return await SucceedAsync(Methods.ApiKey);
            }

            if (signIns.Find(provided, SignInKind.Device) is not { } device)
            {
                return AuthenticateResult.Fail("Invalid API key");
            }

            // A guest's token only works from the guest device that redeemed the invite: copied anywhere else, it's refused.
            if (device.Guest && !FromGuestAddress(device))
            {
                return AuthenticateResult.Fail("This guest sign-in belongs to another device");
            }

            signIns.NoteDeviceType(device, DeviceTypes.Of(Request));
            return await SucceedAsync(Methods.Device, device);
        }

        // A stale or signed-out cookie is ignored rather than refused, so the other ways still apply.
        string? tailscaleLogin = null;
        var askedTailscale = false;
        if (Request.Cookies[SessionCookieName] is { Length: > 0 } cookie)
        {
            if (signIns.Find(cookie, SignInKind.Browser) is { } browser)
            {
                if (!browser.PinVerified)
                {
                    return await SucceedAsync(Methods.Session, browser);
                }

                // Earned with the admin PIN at home, where the home network signs people in: only there, and
                // only while it still does. Taken anywhere else, it's ignored.
                if (browser.TailscaleLogin is null)
                {
                    if (HomeNetworkSignsIn(options))
                    {
                        return await SucceedAsync(Methods.Session, browser);
                    }
                }
                else
                {

                // Earned with the admin PIN over Tailscale: only for that login, while it's linked to a profile
                // (unlinking it signs these out too). Copied to another device or used from home, it's just ignored.
                (tailscaleLogin, askedTailscale) = (await TrustedTailscaleLoginAsync(Context, whois, options), true);
                if (tailscaleLogin is not null && string.Equals(tailscaleLogin, browser.TailscaleLogin, StringComparison.OrdinalIgnoreCase)
                    && await Context.RequestServices.GetRequiredService<FlexarrDbContext>().Profiles.AnyAsync(p => p.TailscaleLogin == tailscaleLogin, Context.RequestAborted))
                {
                    return await SucceedAsync(Methods.Session, browser, login: tailscaleLogin);
                }
                }
            }

            // Earlier versions' shared cookie, for the month after upgrading (see FlexarrOptions.LegacySignInsUntil).
            if (!string.IsNullOrEmpty(options.ApiKey) && options.AcceptsLegacySignIns(DateTime.UtcNow) && IsApiKey(cookie, LegacySessionValue(options.ApiKey)))
            {
                return await SucceedAsync(Methods.Session, await UpgradeLegacyCookieAsync());
            }
        }

        if ((askedTailscale ? tailscaleLogin : await TrustedTailscaleLoginAsync(Context, whois, options)) is { } login)
        {
            var db = Context.RequestServices.GetRequiredService<FlexarrDbContext>();
            var profile = await db.Profiles.AsNoTracking().Where(p => p.TailscaleLogin == login).Select(p => p.Id).FirstOrDefaultAsync(Context.RequestAborted);
            return profile is null
                ? AuthenticateResult.Fail($"The Tailscale account {login} isn't linked to a Flexarr profile")
                : await SucceedAsync(Methods.Tailscale, linkedProfile: profile, login: login);
        }

        if (HomeNetworkSignsIn(options))
        {
            return await SucceedAsync(Methods.Local);
        }

        return AuthenticateResult.NoResult();
    }

    /// <summary>"Only away from home" is chosen and this request is from home, addressed to one of the server's own names.</summary>
    private bool HomeNetworkSignsIn(FlexarrOptions options) =>
        options.AuthenticationRequired == AuthenticationRequired.DisabledForLocalAddresses && NetworkZones.Of(Context) == NetworkZone.Local
        && NetworkZones.AddressedToKnownHost(Context, options.KnownHosts);

    /// <summary>The cookie earlier versions gave every browser: derived from the API key, and the same for all of them.</summary>
    public static string LegacySessionValue(string apiKey) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(apiKey), "flexarr web session"u8));

    /// <summary>Gives a browser its session cookie: HttpOnly, so scripts on the page can't read it.</summary>
    public static void AppendSessionCookie(HttpContext http, string token)
    {
        ArgumentNullException.ThrowIfNull(http);
        http.Response.Cookies.Append(SessionCookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            // The browser keeps it as long as it can; the server signs out browsers left unused for six months.
            MaxAge = TimeSpan.FromDays(400),
        });
    }

    /// <summary>"Safari on Mac", for the list of signed-in browsers.</summary>
    public static string BrowserName(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var agent = request.Headers.UserAgent.ToString();
        var browser = agent switch
        {
            _ when agent.Contains("Edg/", StringComparison.Ordinal) => "Edge",
            _ when agent.Contains("Firefox/", StringComparison.Ordinal) || agent.Contains("FxiOS/", StringComparison.Ordinal) => "Firefox",
            _ when agent.Contains("Chrome/", StringComparison.Ordinal) || agent.Contains("CriOS/", StringComparison.Ordinal) => "Chrome",
            _ when agent.Contains("Safari/", StringComparison.Ordinal) => "Safari",
            _ => "A browser",
        };
        var system = agent switch
        {
            _ when agent.Contains("iPhone", StringComparison.Ordinal) => "iPhone",
            _ when agent.Contains("iPad", StringComparison.Ordinal) => "iPad",
            _ when agent.Contains("Android", StringComparison.Ordinal) => "Android",
            _ when agent.Contains("Mac OS X", StringComparison.Ordinal) => "Mac",
            _ when agent.Contains("Windows", StringComparison.Ordinal) => "Windows",
            _ when agent.Contains("Linux", StringComparison.Ordinal) => "Linux",
            _ => null,
        };
        return system is null ? browser : $"{browser} on {system}";
    }

    /// <summary>
    /// The Tailscale login on a request, if it can be believed: it came over a loopback connection (from
    /// <c>tailscale serve</c> on this machine) for a client on the tailnet, not through Funnel, addressed to one of this
    /// server's names, and Tailscale says that client's address belongs to that login. A device on the LAN connecting
    /// directly can't set it, because its connection isn't loopback. Another program on this machine can still claim
    /// to be someone if it knows the address of one of their devices (<c>tailscale status</c> lists them), which is
    /// why a Tailscale identity alone never manages the server (see <see cref="SucceedAsync"/>).
    /// </summary>
    private static async Task<string?> TrustedTailscaleLoginAsync(HttpContext http, ITailnetWhois whois, FlexarrOptions options)
    {
        var login = http.Request.Headers[TailscaleLoginHeader].FirstOrDefault()?.Trim();
        var peer = RequestPeer.Of(http);
        if (string.IsNullOrEmpty(login)
            || peer is null || !System.Net.IPAddress.IsLoopback(peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer)
            || NetworkZones.Of(http) != NetworkZone.Tailnet
            || !NetworkZones.AddressedToKnownHost(http, options.KnownHosts)
            || http.Connection.RemoteIpAddress is not { } client)
        {
            return null;
        }

        var owner = await whois.LoginAsync(client.IsIPv4MappedToIPv6 ? client.MapToIPv4() : client, http.RequestAborted);
        return string.Equals(owner, login, StringComparison.OrdinalIgnoreCase) ? login : null;
    }

    /// <summary>A browser signed in by an earlier version: swap its shared cookie for its own session, which can be signed out.</summary>
    private async Task<SignIn> UpgradeLegacyCookieAsync()
    {
        var chosen = await ChosenProfileAsync(Context.RequestServices.GetRequiredService<FlexarrDbContext>());
        var now = DateTime.UtcNow;
        foreach (var (stale, _) in Upgrading.Where(u => u.Value.IsValueCreated && now - u.Value.Value.SignIn.CreatedAt > TimeSpan.FromMinutes(1)))
        {
            Upgrading.TryRemove(stale, out _);
        }

        // The page's first requests arrive together; they share one new session rather than making one each.
        var key = $"{Context.Connection.RemoteIpAddress}|{Request.Headers.UserAgent}";
        for (var attempt = 0; ; attempt++)
        {
            var pending = Upgrading.GetOrAdd(key, _ => new Lazy<(SignIn, string)>(() =>
                signIns.Issue(SignInKind.Browser, BrowserName(Request), NetworkZones.Of(Context), Context.Connection.RemoteIpAddress?.ToString(),
                    chosen == PlaybackState.DefaultUser ? null : chosen)));
            (SignIn SignIn, string Token) issued;
            try
            {
                issued = pending.Value;
            }
            catch
            {
                Upgrading.TryRemove(key, out _); // let the next request try again
                throw;
            }

            // Signed out from Settings since it was made: don't go on using it, make another.
            var current = signIns.Find(issued.Token, SignInKind.Browser);
            if (current is null && attempt == 0)
            {
                Upgrading.TryRemove(new KeyValuePair<string, Lazy<(SignIn, string)>>(key, pending));
                continue;
            }

            AppendSessionCookie(Context, issued.Token);
            return current ?? issued.SignIn;
        }
    }

    /// <summary>
    /// Signs the request in, working out its profile and whether it may manage the server: the API key always may;
    /// otherwise it takes the main profile and its PIN, given lately on this browser or device (see
    /// <see cref="AdminUnlocks"/>).
    /// </summary>
    private async Task<AuthenticateResult> SucceedAsync(string method, SignIn? signIn = null, string? linkedProfile = null, string? login = null)
    {
        var db = Context.RequestServices.GetRequiredService<FlexarrDbContext>();
        string profile;
        bool admin;
        bool trusted;
        if (signIn is { Guest: true })
        {
            // A family guest: its invite's profile, nothing else, and never trusted. A removed profile ends it.
            if (signIn.ProfileId is not { } guestProfile || !await db.Profiles.AnyAsync(p => p.Id == guestProfile && p.Id != PlaybackState.DefaultUser, Context.RequestAborted))
            {
                return AuthenticateResult.Fail("This guest's profile has been removed");
            }

            (profile, admin, trusted) = (guestProfile, false, false);
        }
        else if (signIn is not null)
        {
            // A browser or device the main profile signed in: the profile chosen on it decides, whatever headers say.
            trusted = true;
            if (signIn.ProfileId is null || await db.Profiles.AnyAsync(p => p.Id == signIn.ProfileId, Context.RequestAborted))
            {
                profile = signIn.ProfileId ?? PlaybackState.DefaultUser;
                admin = profile == PlaybackState.DefaultUser && unlocks.IsUnlocked(signIn.Id);
            }
            else if (signIn.KidsLock && await FirstKidsProfileAsync(db) is { } kids)
            {
                // Locked to kids profiles and its own has gone: another of them, never one without a limit.
                (profile, admin) = (kids, false);
            }
            else
            {
                // Its profile has been removed: it watches as the main profile, but only manages the server once the
                // main profile is chosen on it again (with its PIN).
                (profile, admin) = (PlaybackState.DefaultUser, false);
            }
        }
        else
        {
            var basis = linkedProfile ?? PlaybackState.DefaultUser;
            trusted = method != Methods.Tailscale || basis == PlaybackState.DefaultUser;
            var chosen = await ChosenProfileAsync(db);
            profile = chosen ?? basis;
            // At home with "only away from home", a browser without a profile chosen would be the main profile, so a
            // child could leave a kids profile by opening a private window. With kids profiles in the house, it starts
            // on one; choosing the main profile there takes its PIN, which signs the browser in.
            if (method == Methods.Local && chosen is null && await FirstKidsProfileAsync(db) is { } kids)
            {
                profile = kids;
            }
            // Without a sign-in, only the key itself. The admin PIN gives a browser at home or on the tailnet
            // a sign-in of its own, unlocked (see AdminEndpoints). Over Tailscale that matters doubly: another program
            // on this machine can pose as a tailnet user (it only needs one of their devices' addresses).
            admin = method == Methods.ApiKey;
        }

        var guest = signIn is { Guest: true };
        var canUnlock = !admin && !guest && profile == PlaybackState.DefaultUser;
        var claims = new List<Claim> { new(FlexarrClaims.Method, method), new(FlexarrClaims.Profile, profile) };
        if (admin)
        {
            claims.Add(new Claim(FlexarrClaims.Admin, "true"));
        }

        if (canUnlock)
        {
            claims.Add(new Claim(FlexarrClaims.CanUnlock, "true"));
        }

        if (trusted)
        {
            claims.Add(new Claim(FlexarrClaims.Trusted, "true"));
        }

        if (signIn is not null)
        {
            claims.Add(new Claim(FlexarrClaims.SignIn, signIn.Id));
        }

        if (guest)
        {
            claims.Add(new Claim(FlexarrClaims.Guest, "true"));
        }

        if (login is not null)
        {
            claims.Add(new Claim(FlexarrClaims.TailscaleLogin, login));
        }

        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName));
    }

    /// <summary>Whether the request came straight from the tailnet address the guest's device redeemed its invite from.</summary>
    private bool FromGuestAddress(SignIn guest)
    {
        var peer = RequestPeer.Of(Context);
        var address = peer is { IsIPv4MappedToIPv6: true } ? peer.MapToIPv4() : peer;
        return address is not null && guest.GuestAddress is { } expected && address.ToString() == expected;
    }

    /// <summary>The household's first kids profile, if it has one.</summary>
    private Task<string?> FirstKidsProfileAsync(FlexarrDbContext db) =>
        db.Profiles.AsNoTracking().Where(p => p.IsKids).OrderBy(p => p.CreatedAt).Select(p => p.Id).FirstOrDefaultAsync(Context.RequestAborted);

    /// <summary>The profile named by a valid <c>X-Flexarr-Profile</c> token, if it still exists.</summary>
    private async Task<string?> ChosenProfileAsync(FlexarrDbContext db)
    {
        var token = Request.Headers[CurrentProfile.Header].FirstOrDefault();
        var dot = token?.LastIndexOf('.') ?? -1;
        if (token is null || dot <= 0)
        {
            return null;
        }

        var id = token[..dot];
        var options = flexarrOptions.CurrentValue;
        var found = await db.Profiles.AsNoTracking().Where(p => p.Id == id).Select(p => new { p.PinHash }).FirstOrDefaultAsync(Context.RequestAborted);
        if (found is null)
        {
            return null;
        }

        return CurrentProfile.Verifies(options.ApiKey, id, found.PinHash, token[(dot + 1)..], options.AcceptsLegacySignIns(DateTime.UtcNow)) ? id : null;
    }

    private static bool IsApiKey(string provided, string? key) =>
        !string.IsNullOrEmpty(key) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(key));
}

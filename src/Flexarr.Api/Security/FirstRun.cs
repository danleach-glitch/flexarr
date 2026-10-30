using System.Security.Cryptography;
using System.Text;
using Flexarr.Api.Authentication;
using Flexarr.Api.Configuration;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Security;

/// <param name="CodeRequired">Setting up asks for the code in the server's log (absent from servers before 0.52).</param>
/// <param name="ServerId">
/// This server's id (<see cref="FlexarrOptions.ServerId"/>), so an app can check that an address it has just learnt
/// for its server is the same server (absent from servers before 0.55).
/// </param>
/// <param name="Install">How the server was installed, so the welcome page can say where its code is (absent before 0.56).</param>
public sealed record SetupStatusDto(bool Open, bool CodeRequired = true, string? ServerId = null, Updates.InstallKind? Install = null);

/// <summary>What a browser sends to set the server up: the setup code, and the main profile's PIN (it guards the server's settings).</summary>
public sealed record SetupClaimDto(string? Code, string? Pin = null);

/// <summary>
/// Setting up a new server without its API key: until anyone has signed in, a browser on the home network with the
/// setup code becomes the owner (see <see cref="FlexarrOptions.SetupOpen"/>). Only from the home network, and only
/// addressed to one of this server's own names, so neither the internet nor a web page using your browser (DNS
/// rebinding) can claim it. The code is printed in the server's log and kept in <see cref="CodeFileName"/> in its data
/// folder: behind a reverse proxy or Docker's port forwarding, every visitor can look like the home network, and the
/// code is what only someone with the machine has. Claiming closes setup for good: one owner, then codes and the key.
/// </summary>
public sealed partial class FirstRun(IOptionsMonitor<FlexarrOptions> options, Microsoft.AspNetCore.Hosting.Server.IServer server, ILogger<FirstRun> logger)
{
    public const string CodeFileName = "setup-code.txt";

    /// <summary>Wrong codes before the code is replaced with a new one, so it can't be guessed by trying them all.</summary>
    internal const int TriesPerCode = 10;

    // No 0/O or 1/I/L, so it reads back without doubt from a terminal.
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    private static readonly Lock ClaimLock = new();
    private string? _code;
    private int _wrongTries;

    public enum ClaimResult { Claimed, Closed, WrongCode }

    /// <summary>Whether <paramref name="http"/> may set this server up.</summary>
    public bool OpenTo(HttpContext http) =>
        options.CurrentValue.SetupOpen && NetworkZones.Of(http) == NetworkZone.Local
        && NetworkZones.AddressedToKnownHost(http, options.CurrentValue.KnownHosts);

    /// <summary>The code setup asks for, made (and announced) the first time it's needed. Null once set up.</summary>
    internal string? Code
    {
        get
        {
            lock (ClaimLock)
            {
                return options.CurrentValue.SetupOpen ? _code ??= NewCode() : null;
            }
        }
    }

    /// <summary>At startup, while the server has no owner: says the code where whoever installed it will look.</summary>
    public void Announce() => _ = Code;

    /// <summary>
    /// Takes the one claim, if <paramref name="http"/> may, has the code, and nobody has yet. <paramref name="claiming"/>
    /// (setting the PIN, signing the browser in) runs before setup closes: if it fails, setup stays open with the same
    /// code.
    /// </summary>
    public ClaimResult TryClaim(HttpContext http, string? code, Action? claiming = null)
    {
        lock (ClaimLock)
        {
            if (!OpenTo(http))
            {
                return ClaimResult.Closed;
            }

            var expected = Code!;
            if (!Matches(code, expected))
            {
                if (++_wrongTries >= TriesPerCode)
                {
                    LogTooManyTries(TriesPerCode);
                    _code = NewCode();
                }

                return ClaimResult.WrongCode;
            }

            claiming?.Invoke();
            Close(options.CurrentValue);
            return ClaimResult.Claimed;
        }
    }

    /// <summary>Ignoring case, spaces and dashes, compared in constant time.</summary>
    internal static bool Matches(string? given, string expected)
    {
        var typed = new string([.. (given ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant)]);
        var wanted = expected.Replace("-", "", StringComparison.Ordinal);
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(typed), Encoding.ASCII.GetBytes(wanted));
    }

    /// <summary>Call with the claim lock held.</summary>
    private string NewCode()
    {
        var chars = new char[8];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        var code = $"{new string(chars, 0, 4)}-{new string(chars, 4, 4)}";
        _wrongTries = 0;
        var file = Path.Combine(options.CurrentValue.DataDirectory, CodeFileName);
        try
        {
            ServerConfigStore.WritePrivately(file, code + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            file = "(couldn't be saved to a file)";
        }

        LogCode(SetupLink("localhost", ListeningPort(), code), code, file);
        return code;
    }

    /// <summary>Any sign-in closes setup: the server has an owner now.</summary>
    public static void Close(FlexarrOptions current)
    {
        ArgumentNullException.ThrowIfNull(current);
        lock (ClaimLock)
        {
            if (!current.SetupOpen)
            {
                return;
            }

            current.SetupOpen = false;
            ServerConfigStore.Update(current.DataDirectory, config => config["setupOpen"] = false);
            try
            {
                File.Delete(Path.Combine(current.DataDirectory, CodeFileName));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Harmless once set up: the code no longer opens anything.
            }
        }
    }

    /// <summary>
    /// The welcome page with the code filled in: after the #, which browsers never send to the server, so the code
    /// stays out of logs and proxies. Docker's log viewers make it something to click.
    /// </summary>
    internal static string SetupLink(string host, int port, string code) => $"http://{host}:{port}/#setup={code}";

    /// <summary>The port the server listens on (once it's started; see Program.cs), else the usual one.</summary>
    private int ListeningPort()
    {
        var addresses = server.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses ?? [];
        foreach (var address in addresses)
        {
            // "http://*:8484" and "http://[::]:8484" aren't URIs as such; the port is after the last colon.
            if (int.TryParse(address[(address.LastIndexOf(':') + 1)..].TrimEnd('/'), out var port))
            {
                return port;
            }
        }

        return 8383;
    }

    // A warning, so it's in the console even where only warnings are (the macOS service), and easy to spot in it. The
    // link first: setting up from it is only choosing a PIN.
    [LoggerMessage(Level = LogLevel.Warning, Message = "This server isn't set up yet. Set it up at {Link} (from another computer, use this one's name or IP address in place of localhost). The setup code is {Code}, also in {File}.")]
    private partial void LogCode(string link, string code, string file);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Setup was tried with a wrong code {Tries} times, so it has a new code")]
    private partial void LogTooManyTries(int tries);
}

public static class FirstRunEndpoints
{
    /// <summary>
    /// Anonymous: a browser that isn't signed in asks whether it can set the server up instead, and a phone waiting
    /// for its code asks whether to say "set it up on a computer first".
    /// </summary>
    public static RouteGroupBuilder MapFirstRunEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/setup").WithTags("System").AllowAnonymous();
        group.MapGet("/", (HttpContext http, FirstRun firstRun, IOptionsMonitor<FlexarrOptions> options, Updates.IInstallEnvironment install) =>
        {
            var open = firstRun.OpenTo(http);
            return Results.Ok(new SetupStatusDto(open, ServerId: options.CurrentValue.ServerId, Install: open ? install.Kind : null));
        });
        // Only the web app's own scripts send X-Requested-With, so a form on another site can't claim the server.
        // The PIN comes with the code, so setup is done all at once or not at all: closing the page before the button
        // leaves it open, with the same code.
        group.MapPost("/claim", async (HttpContext http, FirstRun firstRun, SignIns signIns, AdminUnlocks unlocks, FlexarrDbContext db, SetupClaimDto? claim,
            CancellationToken ct) =>
        {
            var fromWebApp = http.Request.Headers.ContainsKey(ApiKeyAuthenticationHandler.RequestedWithHeader);
            if (fromWebApp && firstRun.OpenTo(http) && !AdminEndpoints.IsPin(claim?.Pin))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["pin"] = ["Choose a PIN of 4 to 8 digits."] });
            }

            // Done inside the claim, before setup closes, so it's all or nothing. (A sign-in closes setup too.)
            var main = fromWebApp ? await db.Profiles.FindAsync([PlaybackState.DefaultUser], ct) : null;
            string? token = null;
            var result = fromWebApp
                ? firstRun.TryClaim(http, claim?.Code, () =>
                {
                    if (main is not null)
                    {
                        main.SetPin(claim!.Pin);
                        db.SaveChanges();
                    }

                    (var signIn, token) = signIns.Issue(SignInKind.Browser, ApiKeyAuthenticationHandler.BrowserName(http.Request), NetworkZone.Local,
                        http.Connection.RemoteIpAddress?.ToString());
                    unlocks.Unlock(signIn.Id);
                })
                : FirstRun.ClaimResult.Closed;
            if (result == FirstRun.ClaimResult.WrongCode)
            {
                return Results.Problem("That isn't this server's setup code. It's in the server's log, and in setup-code.txt in its data folder.",
                    statusCode: 400);
            }

            if (result == FirstRun.ClaimResult.Closed)
            {
                return Results.Problem("This server is already set up. Sign in with a code from a device that's signed in.", statusCode: 403);
            }

            ApiKeyAuthenticationHandler.AppendSessionCookie(http, token!);
            return Results.NoContent();
        });
        return api;
    }
}

using System.Security.Cryptography;
using System.Text;
using Flexarr.Api.Authentication;
using Flexarr.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Security;

/// <param name="CodeRequired">Setting up asks for the code in the server's log (absent from servers before 0.52).</param>
public sealed record SetupStatusDto(bool Open, bool CodeRequired = true);

/// <summary>What a browser sends to set the server up.</summary>
public sealed record SetupClaimDto(string? Code);

/// <summary>
/// Setting up a new server without its API key: until anyone has signed in, a browser on the home network with the
/// setup code becomes the owner (see <see cref="FlexarrOptions.SetupOpen"/>). Only from the home network, and only
/// addressed to one of this server's own names, so neither the internet nor a web page using your browser (DNS
/// rebinding) can claim it. The code is printed in the server's log and kept in <see cref="CodeFileName"/> in its data
/// folder: behind a reverse proxy or Docker's port forwarding, every visitor can look like the home network, and the
/// code is what only someone with the machine has. Claiming closes setup for good: one owner, then codes and the key.
/// </summary>
public sealed partial class FirstRun(IOptionsMonitor<FlexarrOptions> options, ILogger<FirstRun> logger)
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

    /// <summary>Takes the one claim, if <paramref name="http"/> may, has the code, and nobody has yet.</summary>
    public ClaimResult TryClaim(HttpContext http, string? code)
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

        LogCode(code, file);
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

    // A warning, so it's in the console even where only warnings are (the macOS service), and easy to spot in it.
    [LoggerMessage(Level = LogLevel.Warning, Message = "This server isn't set up yet. To set it up, open it in a browser on your home network and enter the setup code {Code}. The code is also in {File}.")]
    private partial void LogCode(string code, string file);

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
        group.MapGet("/", (HttpContext http, FirstRun firstRun) => Results.Ok(new SetupStatusDto(firstRun.OpenTo(http))));
        // Only the web app's own scripts send X-Requested-With, so a form on another site can't claim the server.
        group.MapPost("/claim", (HttpContext http, FirstRun firstRun, SignIns signIns, SetupClaimDto? claim) =>
        {
            var result = http.Request.Headers.ContainsKey(ApiKeyAuthenticationHandler.RequestedWithHeader)
                ? firstRun.TryClaim(http, claim?.Code)
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

            var (_, token) = signIns.Issue(SignInKind.Browser, ApiKeyAuthenticationHandler.BrowserName(http.Request), NetworkZone.Local,
                http.Connection.RemoteIpAddress?.ToString());
            ApiKeyAuthenticationHandler.AppendSessionCookie(http, token);
            return Results.NoContent();
        });
        return api;
    }
}

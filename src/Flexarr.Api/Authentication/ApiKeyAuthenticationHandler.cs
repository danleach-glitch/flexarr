using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Flexarr.Api.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Authentication;

/// <summary>
/// *arr-style API key auth: the key may be sent as an <c>X-Api-Key</c> header or an <c>apikey</c>
/// query parameter (the latter is what *arr webhook URLs use).
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    IOptionsMonitor<FlexarrOptions> flexarrOptions,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, loggerFactory, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";
    public const string QueryName = "apikey";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var options = flexarrOptions.CurrentValue;

        if (options.AuthenticationRequired == AuthenticationRequired.DisabledForLocalAddresses
            && IsLocalAddress(Context.Connection.RemoteIpAddress))
        {
            return Task.FromResult(Success("local"));
        }

        string? provided = Request.Headers[HeaderName].FirstOrDefault() ?? Request.Query[QueryName].FirstOrDefault();
        if (string.IsNullOrEmpty(provided))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var matches = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided),
            Encoding.UTF8.GetBytes(options.ApiKey ?? string.Empty));

        return Task.FromResult(matches ? Success("api-key") : AuthenticateResult.Fail("Invalid API key"));
    }

    private static AuthenticateResult Success(string method)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.AuthenticationMethod, method)], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    private static bool IsLocalAddress(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal)
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && (bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168));
    }
}

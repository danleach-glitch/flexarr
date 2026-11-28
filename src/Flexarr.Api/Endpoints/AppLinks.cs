using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Flexarr.Api.Security;
using Flexarr.Api.Updates;

namespace Flexarr.Api.Endpoints;

/// <summary>
/// Where a browser opens an app Flexarr talks to (Sonarr, Radarr, Lidarr, Seerr, Bazarr, Dispatcharr). The address
/// Flexarr uses often means nothing to the browser: localhost is only localhost to this server, and in Docker
/// "sonarr" or host.docker.internal are names only containers know. An app at such an address runs on this server,
/// so the browser goes to the host it reached Flexarr on, at the app's port. An address an admin set for browsers
/// (<see cref="Flexarr.Integrations.Arr.ArrInstance.WebUrl"/>) is used as it is.
/// </summary>
public sealed class AppLinks
{
    /// <summary>For links nobody opens (notifications, counts): an app's own address, unchanged.</summary>
    public static readonly AppLinks None = new(null, false);

    private readonly string? _host;
    private readonly bool _container;
    private HashSet<IPAddress>? _own;

    /// <param name="host">The host to send the browser to for an app on this server; null to leave addresses as they are.</param>
    /// <param name="container">Flexarr runs in Docker, where other containers' names and Docker's own addresses are this server too.</param>
    internal AppLinks(string? host, bool container)
    {
        _host = string.IsNullOrEmpty(host) ? null : host;
        _container = container;
    }

    public static async Task<AppLinks> ForAsync(HttpRequest request, ITailnet tailnet, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var container = request.HttpContext.RequestServices.GetService<IInstallEnvironment>()?.Kind == InstallKind.Docker
            || Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true";
        return new(await BrowserHostAsync(request.Host.Host, tailnet, ct), container);
    }

    /// <summary>
    /// The host for links to apps on this server, from the one the browser reached Flexarr on. Browsers only speak
    /// HTTPS to a .ts.net name (the domain is on their HSTS preload list) and the apps answer plain HTTP, so Flexarr
    /// reached at its Tailscale name links to this server's tailnet address instead, or, when Tailscale can't say
    /// what that is, to the machine's short MagicDNS name.
    /// </summary>
    internal static async Task<string?> BrowserHostAsync(string? host, ITailnet tailnet, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(host) || !host.TrimEnd('.').EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase))
        {
            return host;
        }

        TailnetSnapshot? snapshot = null;
        try
        {
            snapshot = await tailnet.SnapshotAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Tailscale not answering: the short name below still works on a tailnet with MagicDNS.
        }

        return snapshot?.Address ?? host.Split('.')[0];
    }

    /// <summary>A page of the app at <paramref name="baseUrl"/> (<paramref name="path"/> under its URL base), or null when there's no web address.</summary>
    /// <param name="webUrl">The app's address for browsers, when an admin set one.</param>
    public string? Open(string? baseUrl, string? webUrl = null, string path = "")
    {
        var given = Web(webUrl);
        var url = given ?? Web(baseUrl);
        if (url is null)
        {
            return null;
        }

        var builder = new UriBuilder(url);
        if (given is null && _host is not null && OnThisServer(url))
        {
            builder.Host = _host;
        }

        if (path.Length > 0)
        {
            builder.Path = $"{builder.Path.TrimEnd('/')}/{path.TrimStart('/')}";
        }

        return builder.Uri.ToString();
    }

    /// <inheritdoc cref="Open(string?, string?, string)"/>
    public string? Open(Uri baseUrl, Uri? webUrl = null, string path = "") => Open(baseUrl?.ToString(), webUrl?.ToString(), path);

    private static Uri? Web(string? value) =>
        value?.Trim() is { Length: > 0 } text && Uri.TryCreate(text, UriKind.Absolute, out var url) && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps)
            ? url
            : null;

    /// <summary>Whether an app at <paramref name="url"/> runs on this server, so is reached at the browser's host for Flexarr.</summary>
    internal bool OnThisServer(Uri url)
    {
        if (url.IsLoopback)
        {
            return true;
        }

        var host = url.IdnHost.TrimEnd('.');
        if (IPAddress.TryParse(host.Trim('[', ']'), out var address))
        {
            if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            {
                return true;
            }

            // Docker's own networks (172.16.0.0/12), as compose gives them out.
            if (_container && address.AddressFamily == AddressFamily.InterNetwork && address.GetAddressBytes() is [172, var second, ..] && second is >= 16 and <= 31)
            {
                return true;
            }

            return OwnAddresses().Contains(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
        }

        if (string.Equals(host, Dns.GetHostName().TrimEnd('.'), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // In Docker, another container by its service name ("sonarr", "gluetun"), or the host machine.
        return _container && (!host.Contains('.', StringComparison.Ordinal)
            || host.EndsWith(".docker.internal", StringComparison.OrdinalIgnoreCase)
            || host.Equals("host.containers.internal", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>This machine's own addresses: an app set up at its home address is on this server too.</summary>
    private HashSet<IPAddress> OwnAddresses()
    {
        if (_own is not null)
        {
            return _own;
        }

        try
        {
            _own = [.. NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)];
        }
        catch (NetworkInformationException)
        {
            _own = [];
        }

        return _own;
    }
}

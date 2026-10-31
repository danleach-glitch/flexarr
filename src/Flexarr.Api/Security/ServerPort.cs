using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Flexarr.Api.Security;

public static class ServerPort
{
    /// <summary>The port Flexarr listens on for the network (not loopback only), once it's started; null before.</summary>
    public static int? Of(IServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.Features.Get<IServerAddressesFeature>()?.Addresses
            .Select(a => Uri.TryCreate(a.Replace("*", "0.0.0.0", StringComparison.Ordinal).Replace("+", "0.0.0.0", StringComparison.Ordinal), UriKind.Absolute, out var uri) ? uri : null)
            .FirstOrDefault(u => u is { Scheme: "http" } && (u.Host == "0.0.0.0" || u.Host == "[::]" || !u.IsLoopback))?.Port;
    }
}

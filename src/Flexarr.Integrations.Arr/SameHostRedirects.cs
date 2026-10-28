using System.Net;

namespace Flexarr.Integrations.Arr;

/// <summary>
/// Follows redirects only to the same host (http to https included), for clients that send an API key in a header of
/// their own. .NET drops Authorization when a redirect leaves the host but keeps X-Api-Key, so a Sonarr address that
/// redirected elsewhere would hand the key to whoever is there. Use it with AllowAutoRedirect off.
/// </summary>
public sealed class SameHostRedirects : DelegatingHandler
{
    private const int MaxHops = 5;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        for (var hop = 0; hop < MaxHops && IsRedirect(response.StatusCode) && response.Headers.Location is { } location; hop++)
        {
            var next = location.IsAbsoluteUri ? location : new Uri(request.RequestUri!, location);
            if (!string.Equals(next.Host, request.RequestUri!.Host, StringComparison.OrdinalIgnoreCase)
                || (next.Scheme != Uri.UriSchemeHttp && next.Scheme != Uri.UriSchemeHttps))
            {
                return response; // Left as the redirect: the caller sees it didn't get an answer.
            }

            // 307 and 308 keep the method and body; the others become a GET, as browsers do.
            var keep = response.StatusCode is HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
            var again = new HttpRequestMessage(keep ? request.Method : HttpMethod.Get, next) { Content = keep ? request.Content : null };
            foreach (var header in request.Headers)
            {
                again.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            response.Dispose();
            request = again;
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        return response;
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
}

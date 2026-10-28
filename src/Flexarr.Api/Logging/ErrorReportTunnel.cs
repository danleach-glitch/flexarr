using System.Net.Http.Headers;
using System.Text.Json;

namespace Flexarr.Api.Logging;

/// <summary>
/// Passes the web app's error reports on to Sentry. The page's content security policy only lets it talk to this
/// server, and sending them from here also keeps people's addresses from Sentry. Only reports addressed to the web
/// app's own Sentry project are passed on, so this can't be used to send anything anywhere else.
/// </summary>
public sealed class ErrorReportTunnel(HttpClient http)
{
    /// <summary>The web app's project: the same DSN as in <c>frontend/src/lib/sentry.ts</c>.</summary>
    public const string WebDsn = "https://6e5a389d7a7c54800f54ebb0ad0e92ed@o4512326167232512.ingest.de.sentry.io/4512326210027600";

    /// <summary>Sentry's own limit for an envelope is larger, but one scrubbed error from the web app is a few KB.</summary>
    public const int MaxEnvelopeBytes = 512 * 1024;

    private static readonly Uri WebEnvelopeUrl = new("https://o4512326167232512.ingest.de.sentry.io/api/4512326210027600/envelope/");

    /// <summary>Sends one envelope on and gives back Sentry's status, or 400 or 413 when it isn't one to pass on.</summary>
    public async Task<int> ForwardAsync(Stream body, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxEnvelopeBytes)
            {
                return StatusCodes.Status413PayloadTooLarge;
            }

            buffer.Write(chunk, 0, read);
        }

        var bytes = buffer.ToArray();
        if (!IsForTheWebApp(bytes))
        {
            return StatusCodes.Status400BadRequest;
        }

        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-sentry-envelope");
        try
        {
            using var response = await http.PostAsync(WebEnvelopeUrl, content, ct);
            return (int)response.StatusCode;
        }
        catch (HttpRequestException)
        {
            return StatusCodes.Status502BadGateway;
        }
    }

    /// <summary>An envelope's first line is its JSON header, naming the DSN it's for.</summary>
    internal static bool IsForTheWebApp(ReadOnlySpan<byte> envelope)
    {
        var end = envelope.IndexOf((byte)'\n');
        var header = end < 0 ? envelope : envelope[..end];
        try
        {
            var reader = new Utf8JsonReader(header);
            using var json = JsonDocument.ParseValue(ref reader);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("dsn", out var dsn)
                && dsn.ValueKind == JsonValueKind.String
                && dsn.GetString() == WebDsn;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

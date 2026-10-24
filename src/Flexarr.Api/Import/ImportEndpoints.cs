using Flexarr.Api.Configuration;
using Flexarr.Api.Security;
using Flexarr.Core.Data;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Import;

public static class ImportEndpoints
{
    public static RouteGroupBuilder MapImportEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/import/plex").WithTags("Import").RequireAuthorization(FlexarrClaims.AdminPolicy);

        group.MapGet("/", async (string? path, CancellationToken ct) => await PlexImporter.DescribeAsync(path, ct));

        group.MapPost("/", async (PlexImportRequest request, FlexarrDbContext db, IOptionsMonitor<FlexarrOptions> options, ILogger<PlexImporter> logger, CancellationToken ct) =>
        {
            try
            {
                var importer = new PlexImporter(db, options.CurrentValue.PathMappings, logger);
                return Results.Ok(await importer.ImportAsync(request, ct));
            }
            catch (FileNotFoundException ex)
            {
                return Results.Problem(ex.Message, statusCode: 404);
            }
        });

        return api;
    }
}

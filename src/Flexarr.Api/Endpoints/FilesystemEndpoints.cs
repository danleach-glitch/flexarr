using Flexarr.Api.Security;

namespace Flexarr.Api.Endpoints;

public sealed record DirectoryDto(string Name, string Path);

public sealed record DirectoryListingDto(string Path, string? Parent, IReadOnlyList<DirectoryDto> Directories);

public static class FilesystemEndpoints
{
    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
    };

    /// <summary>Folder browser for choosing library folders, like the *arr root-folder picker.</summary>
    public static RouteGroupBuilder MapFilesystemEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/filesystem", (string? path) =>
        {
            var dir = string.IsNullOrWhiteSpace(path) ? DefaultRoot() : Path.GetFullPath(path);
            if (!Directory.Exists(dir))
            {
                return Results.NotFound();
            }

            var directories = Directory.EnumerateDirectories(dir, "*", Options)
                .Select(d => new DirectoryDto(Path.GetFileName(d), d))
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return Results.Ok(new DirectoryListingDto(dir, Path.GetDirectoryName(dir), directories));
        }).WithTags("Filesystem").RequireAuthorization(FlexarrClaims.AdminPolicy);

        return api;
    }

    private static string DefaultRoot() => OperatingSystem.IsMacOS() && Directory.Exists("/Volumes") ? "/Volumes" : "/";
}

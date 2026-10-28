using Flexarr.Api.Security;

namespace Flexarr.Api.Endpoints;

public sealed record DirectoryDto(string Name, string Path);

/// <param name="ChooseOnMac">The server runs in the Mac app, which can only read folders chosen in macOS's folder window.</param>
public sealed record DirectoryListingDto(string Path, string? Parent, IReadOnlyList<DirectoryDto> Directories, bool ChooseOnMac = false);

public sealed record ChosenFolderDto(string Path);

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
        api.MapGet("/filesystem", (string? path, MacAppHost mac) =>
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
            return Results.Ok(new DirectoryListingDto(dir, Path.GetDirectoryName(dir), directories, mac.IsAvailable));
        }).WithTags("Filesystem").RequireAuthorization(FlexarrClaims.AdminPolicy);

        // In the Mac app: opens macOS's folder window on the Mac and waits for a folder, which the server can then read.
        api.MapPost("/filesystem/choose", async (string? start, MacAppHost mac, CancellationToken cancellationToken) =>
        {
            if (!mac.IsAvailable)
            {
                return Results.NotFound();
            }

            return await mac.ChooseFolderAsync(start, cancellationToken) is { } path ? Results.Ok(new ChosenFolderDto(path)) : Results.NoContent();
        }).WithTags("Filesystem").RequireAuthorization(FlexarrClaims.AdminPolicy);

        return api;
    }

    private static string DefaultRoot() => OperatingSystem.IsMacOS() && Directory.Exists("/Volumes") ? "/Volumes" : "/";
}

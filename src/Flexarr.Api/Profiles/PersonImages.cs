using Flexarr.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Profiles;

/// <summary>Photos of people an admin uploaded, cropped square and shrunk to 600px JPEGs in the data folder.</summary>
public sealed class PersonImages(IOptionsMonitor<FlexarrOptions> options, ProfileImages converter)
{
    public const long MaxUploadBytes = 10 * 1024 * 1024;
    private const int Size = 600;

    private string Directory => Path.Combine(options.CurrentValue.DataDirectory, Backups.BackupArchive.PersonImagesFolder);

    /// <summary>Where a person's uploaded photo lives.</summary>
    public string PathFor(Guid personId) => Path.Combine(Directory, $"{personId:N}.jpg");

    /// <summary>The uploaded photo, as the person's image URL.</summary>
    public string UrlFor(Guid personId) => $"file://{PathFor(personId)}";

    /// <summary>
    /// Whether a person's photo is an upload whose file is gone (a restore onto a new machine from before uploads were
    /// backed up): that's no photo.
    /// </summary>
    public bool Missing(Core.Media.Person person)
    {
        ArgumentNullException.ThrowIfNull(person);
        return person.PhotoSource == Core.Media.PersonSources.Upload && !File.Exists(PathFor(person.Id));
    }

    /// <summary>Converts any image ffmpeg can read; false if it isn't one.
    public Task<bool> SaveAsync(Guid personId, Stream image, CancellationToken ct) => converter.SaveSquareAsync(PathFor(personId), Size, image, ct);
}

namespace Flexarr.Api.Endpoints;

/// <summary>Checks, when a folder is saved in Settings, that Flexarr can make it and write in it.</summary>
public static class WritableFolder
{
    /// <summary>
    /// Why Flexarr can't use this folder, or null when it can. The folder is made if it isn't there (by
    /// <paramref name="create"/>, else <see cref="Directory.CreateDirectory(string)"/>), and a file written and deleted in it.
    /// In the Mac app's sandbox this catches a typed path macOS hasn't let it reach.
    /// </summary>
    public static string? Problem(string folder, Action<string>? create = null)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (!Path.IsPathFullyQualified(folder) || folder.Contains('\0', StringComparison.Ordinal))
        {
            return "Enter the folder’s full path.";
        }

        try
        {
            if (create is null)
            {
                Directory.CreateDirectory(folder);
            }
            else
            {
                create(folder);
            }

            var probe = Path.Combine(folder, $".flexarr-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return $"Flexarr can’t write to {folder}.";
        }
    }
}

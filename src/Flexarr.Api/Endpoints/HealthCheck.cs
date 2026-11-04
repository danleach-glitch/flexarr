using Flexarr.Core.Data;
using Flexarr.Core.Localization;
using Flexarr.Core.Probing;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Endpoints;

/// <param name="Problem">"Missing" (not on disk), "Empty" (zero bytes) or "Unreadable" (ffprobe still can't read it).</param>
/// <param name="Detail">For Missing and Empty, English marked with Loc.Mark: the check runs in the background, so it's
/// translated when it's shown (<see cref="Localized"/>). For Unreadable, ffprobe's own words.</param>
public sealed record FileProblemDto(Guid FileId, Guid ItemId, string Path, string Problem, string? Detail)
{
    /// <summary>This problem with its detail in the request's language.</summary>
    public FileProblemDto Localized() => Problem is "Missing" or "Empty" && Detail is not null ? this with { Detail = Loc.T(Detail) } : this;
}

public sealed record HealthCheckDto(bool Running, int Checked, int Total, DateTime? FinishedAt, IReadOnlyList<FileProblemDto> Problems);

/// <summary>
/// The Health check button: looks at every file Flexarr knows, so files deleted or moved outside Sonarr and Radarr, empty
/// copies, and files that still won't read are found without waiting for a scan. One check at a time; the latest
/// result is kept in memory.
/// </summary>
public sealed partial class HealthCheck(IServiceScopeFactory scopes, IMediaProbe probe, ILogger<HealthCheck> logger)
{
    private readonly Lock _gate = new();
    private Task? _running;
    private int _checked;
    private int _total;
    private DateTime? _finishedAt;
    private IReadOnlyList<FileProblemDto> _problems = [];

    public HealthCheckDto Status
    {
        get
        {
            lock (_gate)
            {
                return new HealthCheckDto(_running is { IsCompleted: false }, _checked, _total, _finishedAt, _problems);
            }
        }
    }

    /// <summary>Starts a check unless one is running. False when one already is.</summary>
    public bool Start()
    {
        lock (_gate)
        {
            if (_running is { IsCompleted: false })
            {
                return false;
            }

            _checked = 0;
            _running = Task.Run(RunAsync);
            return true;
        }
    }

    private async Task RunAsync()
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var files = await db.MediaFiles.AsNoTracking().Select(f => new { f.Id, f.MediaItemId, f.Path, f.ProbeError }).ToListAsync();
            Interlocked.Exchange(ref _total, files.Count);

            var problems = new List<FileProblemDto>();
            foreach (var file in files)
            {
                var info = new FileInfo(file.Path);
                if (!info.Exists)
                {
                    problems.Add(new FileProblemDto(file.Id, file.MediaItemId, file.Path, "Missing", Loc.Mark("Not on disk. It may have been deleted or moved, or its drive isn’t connected.")));
                }
                else if (info.Length == 0)
                {
                    problems.Add(new FileProblemDto(file.Id, file.MediaItemId, file.Path, "Empty", Loc.Mark("The file is 0 bytes.")));
                }
                else if (file.ProbeError is not null)
                {
                    // Failed when scanned: look again, it may have been replaced or finished copying since.
                    try
                    {
                        await probe.ProbeAsync(file.Path, CancellationToken.None);
                    }
                    catch (MediaProbeException ex)
                    {
                        problems.Add(new FileProblemDto(file.Id, file.MediaItemId, file.Path, "Unreadable", ex.Message));
                    }
                }

                Interlocked.Increment(ref _checked);
            }

            lock (_gate)
            {
                _problems = problems;
                _finishedAt = DateTime.UtcNow;
            }

            LogFinished(files.Count, problems.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or DbUpdateException)
        {
            LogFailed(ex);
            lock (_gate)
            {
                _finishedAt = DateTime.UtcNow;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Health check: {Files} files checked, {Problems} problems")]
    private partial void LogFinished(int files, int problems);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Health check failed")]
    private partial void LogFailed(Exception exception);
}

using System.Collections.Concurrent;
using Flexarr.Api.Endpoints;
using Flexarr.Core.Data;
using Flexarr.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Flexarr.Api.Profiles;

/// <summary>
/// Looking a person up again after an admin unlocked them: their folder, Audible and Open Library, which can
/// take minutes when a provider is slow. It runs on its own, after the reply, in a scope of its own; one at a time for
/// each person.
/// </summary>
public sealed partial class PersonLookups(IServiceScopeFactory scopes, IMemoryCache memory, IHostApplicationLifetime lifetime, ILogger<PersonLookups> logger)
{
    private readonly ConcurrentDictionary<Guid, Task> _running = new();

    /// <summary>Starts looking a person up, unless that's already happening.</summary>
    public void Start(Guid personId)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_running.TryAdd(personId, started.Task))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await LookUpAsync(personId, lifetime.ApplicationStopping);
            }
            finally
            {
                _running.TryRemove(personId, out _);
                started.SetResult();
            }
        });
    }

    /// <summary>When every lookup started so far is done (for tests).</summary>
    internal Task WhenIdleAsync() => Task.WhenAll(_running.Values);

    private async Task LookUpAsync(Guid personId, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var authors = scope.ServiceProvider.GetRequiredService<AuthorInfo>();
            if (await db.People.FirstOrDefaultAsync(p => p.Id == personId, ct) is not { InfoLocked: false } person)
            {
                return;
            }

            await authors.RefreshPersonAsync(person, ct);

            // Locked again (a photo chosen, a biography written) while this looked: theirs stands.
            if (await db.People.AsNoTracking().AnyAsync(p => p.Id == personId && p.InfoLocked, ct))
            {
                return;
            }

            await db.SaveChangesAsync(ct);
            memory.Remove(ImageEndpoints.PersonSourceKey(personId));
            memory.Remove(PeopleEndpoints.CandidatesKey(personId));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is not OutOfMemoryException)
        {
            LogLookupFailed(ex, personId);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Looking up person {PersonId} again failed")]
    private partial void LogLookupFailed(Exception exception, Guid personId);
}

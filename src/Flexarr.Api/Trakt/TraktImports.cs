using System.Collections.Concurrent;

namespace Flexarr.Api.Trakt;

/// <summary>Runs confirmed history imports off the request, one at a time per profile, and remembers how the last one went.</summary>
public sealed partial class TraktImports(IServiceScopeFactory scopes, IHostApplicationLifetime lifetime, ILogger<TraktImports> logger)
{
    private readonly ConcurrentDictionary<string, Task> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _errors = new(StringComparer.Ordinal);

    public bool Running(string profileId) => _running.ContainsKey(profileId);

    public string? Error(string profileId) => _errors.GetValueOrDefault(profileId);

    /// <summary>Starts the import; false if one is running already.</summary>
    public bool Start(string profileId)
    {
        var started = new TaskCompletionSource();
        if (!_running.TryAdd(profileId, started.Task))
        {
            return false;
        }

        _errors.TryRemove(profileId, out _);
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<TraktSync>().ImportAsync(profileId, apply: true, lifetime.ApplicationStopping);
                TraktSyncWorker.Nudge(profileId);
            }
            catch (TraktException ex)
            {
                _errors[profileId] = ex.Message;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(ex, profileId);
                _errors[profileId] = "The import didn’t finish. Try again.";
            }
            finally
            {
                _running.TryRemove(profileId, out _);
                started.TrySetResult();
            }
        });
        return true;
    }

    /// <summary>Waits for a profile's import to end (tests).</summary>
    internal Task WaitAsync(string profileId) => _running.TryGetValue(profileId, out var task) ? task : Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "The Trakt import for profile {Profile} failed")]
    private partial void LogFailed(Exception ex, string profile);
}

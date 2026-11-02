namespace Flexarr.Api.Endpoints;

/// <summary>Every film and show Seerr is waiting for, household-wide, with how far along each is.</summary>
public static class RequestRequested
{
    private static CancellationTokenSource _changed = new();

    /// <summary>A request or an unrequest changed what Seerr is waiting for.</summary>
    public static void Forget() => Interlocked.Exchange(ref _changed, new CancellationTokenSource()).Cancel();
}

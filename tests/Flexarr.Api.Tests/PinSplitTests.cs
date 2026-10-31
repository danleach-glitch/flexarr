using Flexarr.Api.Profiles;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>The admin PIN and the main profile's own PIN, once one, kept apart.</summary>
public class PinSplitTests
{
    /// <summary>The main profile as the migration leaves it: its one PIN copied into the admin PIN.</summary>
    private static async Task<(IServiceScope Scope, FlexarrDbContext Db, Profile Main)> UpgradedAsync(FlexarrApiFactory server)
    {
        var scope = server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var main = (await db.Profiles.FindAsync(PlaybackState.DefaultUser))!;
        main.SetPin("2580");
        main.AdminPinHash = main.PinHash;
        await db.SaveChangesAsync();
        return (scope, db, main);
    }

    [Fact]
    public async Task Upgrading_TheMainProfileLosesTheCopy_UnlessItWasAskedFor()
    {
        using var server = new FlexarrApiFactory();
        var (scope, db, main) = await UpgradedAsync(server);
        using var _ = scope;
        var tokenBound = main.TokenPinHash;

        await PinSplit.SettleAsync(db, askedForMainProfile: false, CancellationToken.None);

        Assert.Null(main.PinHash);
        Assert.True(main.CheckAdminPin("2580"));
        Assert.Equal(tokenBound, main.TokenPinHash); // nobody on the main profile is signed out
    }

    [Fact]
    public async Task Upgrading_KeepsTheMainProfilesPin_WhenItWasAskedFor()
    {
        using var server = new FlexarrApiFactory();
        var (scope, db, main) = await UpgradedAsync(server);
        using var _ = scope;

        await PinSplit.SettleAsync(db, askedForMainProfile: true, CancellationToken.None);

        Assert.True(main.CheckPin("2580"));
        Assert.True(main.CheckAdminPin("2580"));
    }

    [Fact]
    public async Task APinChosenSince_IsNeverTakenOff()
    {
        using var server = new FlexarrApiFactory();
        var (scope, db, main) = await UpgradedAsync(server);
        using var _ = scope;
        main.SetPin("2580"); // the same digits, chosen again: a hash of its own

        await PinSplit.SettleAsync(db, askedForMainProfile: false, CancellationToken.None);

        Assert.True(main.CheckPin("2580"));
        Assert.NotEqual(main.AdminPinHash, main.PinHash);
    }
}

using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Profiles;

/// <summary>
/// The admin PIN and the main profile's own PIN were one; the migration that split them copied it into the admin PIN
/// (<see cref="Profile.AdminPinHash"/>). The main profile keeps it as its own only if its owner had it asked for when
/// it's chosen (<see cref="Configuration.FlexarrOptions.AskPinForMainProfile"/>).
/// </summary>
public static class PinSplit
{
    /// <summary>
    /// Runs at every start. The copy is the very same hash (a PIN set later has a salt of its own), so this only ever
    /// touches the copy: from the migration, or from a backup made before it and restored later.
    /// </summary>
    public static async Task SettleAsync(FlexarrDbContext db, bool askedForMainProfile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (askedForMainProfile
            || await db.Profiles.FirstOrDefaultAsync(p => p.Id == PlaybackState.DefaultUser, ct) is not { PinHash: not null } main
            || main.PinHash != main.AdminPinHash)
        {
            return;
        }

        // Its tokens were signed with that hash, and now are with the admin PIN's, the same one: nobody is signed out.
        main.PinHash = null;
        await db.SaveChangesAsync(ct);
    }
}

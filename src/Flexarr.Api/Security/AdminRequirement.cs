using Flexarr.Api.Profiles;
using Microsoft.AspNetCore.Authorization;

namespace Flexarr.Api.Security;

/// <summary>Server administration: see <see cref="CurrentProfile.IsAdmin"/>.</summary>
public sealed class AdminRequirement : IAuthorizationRequirement;

/// <summary>Lets admins through; a change made with an unlock (anything but reading) keeps it unlocked for longer.</summary>
public sealed class AdminHandler(CurrentProfile profile, AdminUnlocks unlocks, IHttpContextAccessor http) : AuthorizationHandler<AdminRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, AdminRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated == true && profile.IsAdmin)
        {
            context.Succeed(requirement);
            if (profile.SignInId is { } signIn && http.HttpContext?.Request.Method is { } method && !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method))
            {
                unlocks.Extend(signIn);
            }
        }

        return Task.CompletedTask;
    }
}

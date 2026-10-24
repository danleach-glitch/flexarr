using Flexarr.Api.Profiles;
using Microsoft.AspNetCore.Authorization;

namespace Flexarr.Api.Security;

/// <summary>Server administration: see <see cref="CurrentProfile.IsAdmin"/>.</summary>
public sealed class AdminRequirement : IAuthorizationRequirement;

public sealed class AdminHandler(CurrentProfile profile) : AuthorizationHandler<AdminRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, AdminRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated == true && profile.IsAdmin)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

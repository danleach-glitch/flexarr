using System.Text.RegularExpressions;
using Flexarr.Api.Security;
using Flexarr.Core.Localization;
using Microsoft.AspNetCore.Authorization;

namespace Flexarr.Api.Profiles;

/// <summary>
/// Holds back a request answered as no profile chosen (the main profile has a PIN of its own that wasn't given here, and
/// the client said it can ask who's watching; see
/// <see cref="Authentication.ApiKeyAuthenticationHandler.ProfileChoiceHeader"/>). Until a profile is chosen it may only
/// see who it is, list the profiles and choose one, and sign out: anything else that needs signing in is refused with
/// 403 and <c>chooseProfile: true</c>, so none of the main profile's watching shows.
/// </summary>
public static partial class ProfileChoiceGate
{
    public static IApplicationBuilder UseFlexarrProfileChoiceGate(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        if (!http.User.HasClaim(FlexarrClaims.ChooseProfile, "true") || Allowed(http))
        {
            await next(http);
            return;
        }

        await Results.Problem(detail: Loc.T("Choose who’s watching first."), statusCode: StatusCodes.Status403Forbidden,
            extensions: new Dictionary<string, object?> { ["chooseProfile"] = true }).ExecuteAsync(http);
    });

    private static bool Allowed(HttpContext http)
    {
        // What needs no sign-in (the web app's files, artwork, languages) is as it is for anyone.
        if (http.GetEndpoint() is not { } endpoint || endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null
            || endpoint.Metadata.GetMetadata<IAuthorizeData>() is null)
        {
            return true;
        }

        var method = http.Request.Method;
        var path = http.Request.Path.Value ?? "";
        return HttpMethods.IsGet(method) && (Is(path, "/api/v1/me") || Is(path, "/api/v1/profiles") || Is(path, "/api/v1/system/status"))
            || HttpMethods.IsPost(method) && (ChoosingPath().IsMatch(path) || Is(path, "/api/v1/logs/client"));
    }

    private static bool Is(string path, string expected) => string.Equals(path.TrimEnd('/'), expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>Choosing a profile, or taking off its forgotten PIN with the admin PIN.</summary>
    [GeneratedRegex(@"^/api/v1/profiles/[^/]+/(select|forgot-pin)/?$", RegexOptions.IgnoreCase)]
    private static partial Regex ChoosingPath();
}

using System.Globalization;
using System.Reflection;
using Flexarr.Core.Localization;

namespace Flexarr.Api.Localization;

/// <summary>
/// Error answers in the request's language (docs/languages.md, *The server's own words*). The detail is translated
/// like any <see cref="Loc.T"/>; beside it, <c>code</c> is the English message's key and <c>args</c> its values, so an
/// app can tell errors apart without matching words. In English the detail is exactly the English. Both extensions
/// are absent from servers before 0.61.
/// </summary>
public static class Problems
{
    /// <summary>A problem answer: <c>Problems.Of("That folder isn't there.", status: 404)</c>.</summary>
    public static IResult Of(string english, object? args = null, int status = StatusCodes.Status400BadRequest, string? ctx = null) =>
        Results.Problem(detail: Loc.T(english, args, ctx), statusCode: status, extensions: Extensions(english, args, ctx));

    /// <summary>A validation answer about one field: <c>Problems.Field("Give the playlist a name.", field: "name")</c>.</summary>
    public static IResult Field(string english, string field, object? args = null, string? ctx = null) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [Loc.T(english, args, ctx)] }, extensions: Extensions(english, args, ctx));

    private static Dictionary<string, object?> Extensions(string english, object? args, string? ctx)
    {
        var extensions = new Dictionary<string, object?> { ["code"] = ctx is null ? english : $"{ctx}::{english}" };
        if (args is not null)
        {
            extensions["args"] = args.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .ToDictionary(p => p.Name, p => Convert.ToString(p.GetValue(args), CultureInfo.InvariantCulture));
        }

        return extensions;
    }
}

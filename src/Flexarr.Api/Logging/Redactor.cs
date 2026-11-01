using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Flexarr.Api.Logging;

/// <summary>
/// Keeps credentials out of the log, which people attach to bug reports. Every line passes through here on its way to
/// a file, the console or the Logs page.
/// </summary>
public static partial class Redactor
{
    private static volatile string[] _secrets = [];

    /// <summary>The configured keys and passwords, removed wherever they turn up; set again when the settings change.</summary>
    public static void UseSecrets(IEnumerable<string?> secrets) =>
        _secrets = [.. secrets.OfType<string>().Where(s => s.Length >= 6).Distinct().OrderByDescending(s => s.Length)];

    public static string Redact(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        foreach (var secret in _secrets)
        {
            if (text.Contains(secret, StringComparison.Ordinal))
            {
                text = text.Replace(secret, "***", StringComparison.Ordinal);
            }
        }

        text = Named().Replace(text, m => $"{m.Groups[1].Value}***");
        text = Bearer().Replace(text, "$1***");

        // API keys, sign-in tokens and playback session ids are all long runs of hex. Each is swapped for a short
        // one-way hash, so a playback can still be followed from line to line (it's the playbackId apps report).
        return LongHex().Replace(text, m => "#" + ShortHash(m.Value));
    }

    /// <summary>The short id a secret hex token appears as in the log; also a playback's public <c>playbackId</c>.</summary>
    public static string ShortHash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token.ToLowerInvariant())))[..8];

    [GeneratedRegex(@"\b((?:api[_-]?key|apikey|(?<=[?&])key|access[_-]?token|token|password|passwd|secret|sig|signature)[""']?\s*[=:]\s*[""']?)[^&\s""',;\\]+", RegexOptions.IgnoreCase)]
    private static partial Regex Named();

    [GeneratedRegex(@"\b(Bearer\s+)[^\s""\\]+", RegexOptions.IgnoreCase)]
    private static partial Regex Bearer();

    [GeneratedRegex(@"(?<![0-9A-Za-z])[0-9A-Fa-f]{32,}(?![0-9A-Za-z])")]
    private static partial Regex LongHex();
}

// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;

namespace AetherMesh.Security.Services;

/// <summary>
/// Static helper for sanitizing sensitive identifiers in log output.
/// UHIDs are truncated and suffixed with a daily-rotating hash to allow
/// correlation within a single day without exposing the full identifier.
/// </summary>
public static class LogSanitizer
{
    /// <summary>
    /// Sanitizes a UHID for safe logging. Returns the first 4 characters
    /// followed by "..." and a 4-character daily-rotating hash suffix.
    /// </summary>
    /// <param name="uhid">The full UHID to sanitize.</param>
    /// <returns>A sanitized string safe for logging, e.g. "ab12...x9k2".</returns>
    public static string SanitizeUhid(string? uhid)
    {
        if (string.IsNullOrEmpty(uhid))
            return "[empty]";

        if (uhid.Length <= 4)
            return uhid;

        var prefix = uhid[..4];
        var daySalt = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var hashInput = $"{uhid}:{daySalt}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(hashInput));
        var suffix = Convert.ToHexString(hashBytes)[..4].ToLowerInvariant();

        return $"{prefix}...{suffix}";
    }
}

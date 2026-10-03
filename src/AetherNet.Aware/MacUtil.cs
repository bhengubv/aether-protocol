// SPDX-License-Identifier: MIT
// Ported from Fieldwatch app/src/main/java/app/fieldwatch/domain/Models.kt (MacUtil, TextMatch, uuidAliases)
// (github.com/offgridpete/fieldwatch, cf6562d).
// Copyright (c) 2026 Off Grid Pete LLC. See NOTICE.md.

using System.Text;
using System.Text.RegularExpressions;

namespace AetherNet.Aware;

/// <summary>Radio addresses (MACs and BSSIDs).</summary>
public static class MacUtil
{
    /// <summary>"aa-bb-cc-dd-ee-ff" becomes "AA:BB:CC:DD:EE:FF".</summary>
    public static string Normalize(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var hex = Strings.LettersAndDigits(raw).ToUpperInvariant();
        if (hex.Length < 2)
        {
            return raw.ToUpperInvariant();
        }
        var sb = new StringBuilder(hex.Length + (hex.Length / 2));
        for (var i = 0; i < hex.Length; i += 2)
        {
            if (i > 0)
            {
                sb.Append(':');
            }
            sb.Append(hex, i, Math.Min(2, hex.Length - i));
        }
        return sb.ToString();
    }

    /// <summary>The first <paramref name="n"/> bytes, "AA:BB:CC".</summary>
    public static string PrefixBytes(string mac, int n = 3)
    {
        var parts = Normalize(mac).Split(':');
        return string.Join(':', parts.Take(Math.Min(n, parts.Length)));
    }

    /// <summary>A private (locally administered, unicast) address — the kind phones and tags rotate.</summary>
    public static bool IsRandomized(string mac)
    {
        var normalized = Normalize(mac);
        var colon = normalized.IndexOf(':');
        var firstByte = colon < 0 ? normalized : normalized[..colon];
        if (Strings.ToIntOrNull(firstByte, 16) is not { } first)
        {
            return false;
        }
        return (first & 0x02) != 0 && (first & 0x01) == 0;
    }

    /// <summary>
    /// A Wi-Fi BSSID's 24-bit OUI with the locally-administered bit cleared. Guest, mesh and extra networks often set
    /// that bit on the maker's own prefix. Null if the address is already universal, multicast, or short.
    /// </summary>
    public static string? WifiOui24Universal(string mac)
    {
        var hex = Normalize(mac).Replace(":", "", StringComparison.Ordinal);
        if (hex.Length < 6)
        {
            return null;
        }
        if (Strings.ToIntOrNull(hex[..2], 16) is not { } first)
        {
            return null;
        }
        if ((first & 0x02) == 0 || (first & 0x01) != 0)
        {
            return null;
        }
        return (first & 0xFD).ToString("X2", System.Globalization.CultureInfo.InvariantCulture) + hex.Substring(2, 4);
    }

    public static bool MatchesPrefix(string mac, string prefix)
    {
        var m = Normalize(mac).Replace(":", "", StringComparison.Ordinal);
        var p = Strings.LettersAndDigits(prefix).ToUpperInvariant();
        return p.Length > 0 && m.StartsWith(p, StringComparison.Ordinal);
    }

    /// <summary>The last two bytes as a number.</summary>
    public static int Last16(string mac)
    {
        var hex = Normalize(mac).Replace(":", "", StringComparison.Ordinal);
        if (hex.Length < 4)
        {
            return 0;
        }
        return Strings.ToIntOrNull(Strings.TakeLast(hex, 4), 16) ?? 0;
    }
}

/// <summary>Name matching for signature rules.</summary>
public static class TextMatch
{
    // Java's '.' does not match a line terminator; .NET's only excludes '\n'.
    private const string AnyChar = "[^\\n\\r\\u0085\\u2028\\u2029]";

    /// <summary>The name contains the needle, ignoring case. A blank needle matches nothing.</summary>
    public static bool Contains(string hay, string needle) =>
        !Strings.IsBlank(needle) && hay.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>The whole name matches the pattern, ignoring case: <c>*</c> any run, <c>?</c> one character.</summary>
    public static bool Glob(string text, string pattern) =>
        !Strings.IsBlank(pattern) && Regex.IsMatch(text, GlobRegex(pattern), GlobOptions);

    internal const RegexOptions GlobOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    internal static string GlobRegex(string pattern)
    {
        var sb = new StringBuilder("\\A");
        foreach (var ch in pattern)
        {
            switch (ch)
            {
                case '*':
                    sb.Append(AnyChar).Append('*');
                    break;
                case '?':
                    sb.Append(AnyChar);
                    break;
                default:
                    sb.Append(Regex.Escape(ch.ToString()));
                    break;
            }
        }
        return sb.Append("\\z").ToString();
    }
}

/// <summary>Bluetooth service UUIDs.</summary>
public static class Uuids
{
    private const string SigBaseTail = "00001000800000805F9B34FB";

    /// <summary>
    /// Every spelling a UUID might be written in, so "FD44", "0xFD44" and "0000FD44-0000-1000-8000-00805F9B34FB" all
    /// meet. A 16-bit alias is only made for the Bluetooth SIG base UUID.
    /// </summary>
    public static IReadOnlySet<string> Aliases(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var hex = Strings.LettersAndDigits(raw).ToUpperInvariant();
        var output = new HashSet<string>(StringComparer.Ordinal) { raw.ToUpperInvariant(), hex };
        if (hex.Length == 4)
        {
            output.Add("0x" + hex);
            output.Add("0000" + hex + "-0000-1000-8000-00805F9B34FB");
            output.Add("0000" + hex + SigBaseTail);
            return output;
        }
        if (hex.Length == 32 && hex.StartsWith("0000", StringComparison.Ordinal) && hex.EndsWith(SigBaseTail, StringComparison.Ordinal))
        {
            var shortUuid = hex.Substring(4, 4);
            output.Add(shortUuid);
            output.Add("0x" + shortUuid);
            output.Add("0000" + shortUuid + "-0000-1000-8000-00805F9B34FB");
        }
        return output;
    }
}

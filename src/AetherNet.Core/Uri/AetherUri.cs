// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AetherNet.Identity;

namespace AetherNet.Uri;

/// <summary>
/// Aether URI — the canonical addressing format for resources on the Aether mesh.
///
/// <h3>Grammar (ABNF, RFC 5234)</h3>
/// <code>
/// aether-uri   = "aether://" authority [ "/" path ] [ "?" query ] [ "#" fragment ]
/// authority    = aether-tag / uhid
/// aether-tag   = 5(crockford) [ "-" ] 5(crockford)         ; case-insensitive
/// uhid         = 64(HEXDIG)                                ; SHA-256 hex of public key
/// path         = path-segment *( "/" path-segment )
/// path-segment = 1*( unreserved / pct-encoded / sub-delims / ":" / "@" )
/// query        = query-param *( "&amp;" query-param )
/// query-param  = key [ "=" value ]
/// key          = 1*( unreserved / pct-encoded )
/// value        = *( unreserved / pct-encoded / sub-delims / ":" / "@" / "/" / "?" )
/// fragment     = *( unreserved / pct-encoded / sub-delims / ":" / "@" / "/" / "?" )
/// crockford    = %x30-39 / %x41-48 / %x4A / %x4B / %x4D / %x4E / %x50-54 / %x56-5A
///              ; 0–9 A-H J K M N P-T V-Z (no I L O U)
/// unreserved   = ALPHA / DIGIT / "-" / "." / "_" / "~"
/// pct-encoded  = "%" HEXDIG HEXDIG
/// sub-delims   = "!" / "$" / "&amp;" / "'" / "(" / ")" / "*" / "+" / "," / ";" / "="
/// </code>
///
/// <h3>Components</h3>
/// <list type="bullet">
///   <item><description><b>Scheme</b> is always <c>aether</c>. Case-insensitive on parse, lowercase on emit.</description></item>
///   <item><description><b>Authority</b> identifies the destination — an <see cref="AetherNetTag"/>
///     (10 Crockford base-32 chars, dash optional) or a UHID (64 hex chars). Case-insensitive.</description></item>
///   <item><description><b>Path</b> is opaque to the protocol — it names a handler within the destination
///     (e.g. <c>/content/&lt;hash&gt;</c>, <c>/profile</c>, <c>/inbox</c>). Case-sensitive.</description></item>
///   <item><description><b>Query</b> carries handler arguments. Keys are case-insensitive, values are case-sensitive.</description></item>
///   <item><description><b>Fragment</b> is a client-side hint and is never transmitted over the wire
///     (e.g. <c>#t=1m30s</c> for a playback position).</description></item>
/// </list>
///
/// <h3>Examples</h3>
/// <code>
/// aether://KXJB7-MN2P4/profile
/// aether://KXJB7-MN2P4/content/sha256-abc123?codec=opus
/// aether://KXJB7MN2P4/stream/live?bitrate=hd#t=1m30s
/// aether://a1b2c3d4e5f6...64hex/inbox
/// </code>
///
/// <h3>Why no userinfo or port</h3>
/// The authority IS the user — there is no separate userinfo. Ports have no meaning in
/// mesh routing because the transport layer selects the carrier (BLE / Wi-Fi Direct /
/// NearLink / HTTP relay); a URI never picks one.
/// </summary>
public readonly struct AetherUri : IEquatable<AetherUri>
{
    /// <summary>The fixed scheme name — <c>aether</c>.</summary>
    public const string Scheme = "aether";

    private const string SchemePrefix = "aether://";

    // ── State ────────────────────────────────────────────────────────────────

    /// <summary>The destination authority (AetherTag or UHID), canonicalised to upper case.</summary>
    public string Authority { get; }

    /// <summary>The handler path, without the leading slash. Empty string means "root".</summary>
    public string Path { get; }

    /// <summary>Decoded query parameters. Keys are stored lower-case for case-insensitive lookup.</summary>
    public IReadOnlyDictionary<string, string> Query { get; }

    /// <summary>The fragment, with leading "#" stripped. Empty if none.</summary>
    public string Fragment { get; }

    /// <summary>Returns true if this is a properly-parsed value (not a default struct).</summary>
    public bool IsValid => !string.IsNullOrEmpty(Authority);

    private AetherUri(string authority, string path, IReadOnlyDictionary<string, string> query, string fragment)
    {
        Authority = authority;
        Path = path;
        Query = query;
        Fragment = fragment;
    }

    // ── Parsing ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses an <c>aether://</c> URI. Throws <see cref="AetherUriException"/> on any
    /// syntactic violation. Use <see cref="TryParse"/> for non-throwing parses.
    /// </summary>
    public static AetherUri Parse(string input)
    {
        if (input is null) throw new AetherUriException("Input is null.");
        if (!TryParse(input, out var uri, out var error))
            throw new AetherUriException(error ?? "Invalid aether URI.");
        return uri;
    }

    /// <summary>
    /// Attempts to parse an <c>aether://</c> URI. Returns true on success and populates
    /// <paramref name="result"/>; returns false and sets <paramref name="error"/> on failure.
    /// </summary>
    public static bool TryParse(string input, out AetherUri result, out string? error)
    {
        result = default;
        error = null;

        if (string.IsNullOrEmpty(input))
        {
            error = "Input is null or empty.";
            return false;
        }

        // Scheme is case-insensitive per RFC 3986.
        if (input.Length < SchemePrefix.Length ||
            !input.AsSpan(0, SchemePrefix.Length).Equals(SchemePrefix.AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            error = $"Scheme must be '{Scheme}://'.";
            return false;
        }

        var rest = input.Substring(SchemePrefix.Length);

        // Split on fragment first (only one '#' is allowed).
        var fragmentSplit = rest.IndexOf('#');
        string fragment = string.Empty;
        if (fragmentSplit >= 0)
        {
            fragment = PercentDecode(rest.Substring(fragmentSplit + 1));
            rest = rest.Substring(0, fragmentSplit);
        }

        // Then query.
        var querySplit = rest.IndexOf('?');
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (querySplit >= 0)
        {
            var queryRaw = rest.Substring(querySplit + 1);
            rest = rest.Substring(0, querySplit);
            foreach (var pair in queryRaw.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                var key = eq >= 0 ? pair.Substring(0, eq) : pair;
                var value = eq >= 0 ? pair.Substring(eq + 1) : string.Empty;
                var decodedKey = PercentDecode(key);
                if (decodedKey.Length == 0)
                {
                    error = "Empty query parameter key.";
                    return false;
                }
                query[decodedKey] = PercentDecode(value);
            }
        }

        // Then path.
        var pathSplit = rest.IndexOf('/');
        string authorityRaw;
        string path;
        if (pathSplit >= 0)
        {
            authorityRaw = rest.Substring(0, pathSplit);
            path = rest.Substring(pathSplit + 1);
        }
        else
        {
            authorityRaw = rest;
            path = string.Empty;
        }

        if (authorityRaw.Length == 0)
        {
            error = "Authority is missing.";
            return false;
        }

        // Authority validation: either an AetherNetTag or a 64-char hex UHID.
        var authority = CanonicaliseAuthority(authorityRaw, out var authError);
        if (authority is null)
        {
            error = authError;
            return false;
        }

        // Path validation: segments must contain only allowed characters.
        if (!ValidatePath(path, out var pathError))
        {
            error = pathError;
            return false;
        }

        result = new AetherUri(authority, PercentDecodePath(path), query, fragment);
        return true;
    }

    private static string? CanonicaliseAuthority(string raw, out string? error)
    {
        error = null;

        // Try UHID (64 hex chars).
        if (raw.Length == 64 && IsHex(raw))
        {
            return raw.ToUpperInvariant();
        }

        // Try AetherTag (10 Crockford chars with optional dash).
        if (AetherNetTag.TryParse(raw, out var tag))
        {
            return tag.Value; // Canonical form: XXXXX-XXXXX
        }

        error = $"Authority '{raw}' is neither a valid AetherTag nor a 64-char hex UHID.";
        return null;
    }

    private static bool IsHex(string s)
    {
        foreach (var c in s)
        {
            if (!((c >= '0' && c <= '9') ||
                  (c >= 'A' && c <= 'F') ||
                  (c >= 'a' && c <= 'f')))
                return false;
        }
        return true;
    }

    private static bool ValidatePath(string path, out string? error)
    {
        error = null;
        if (path.Length == 0) return true;

        // Walk segments, allow unreserved + pct-encoded + sub-delims + ':' + '@'.
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0)
            {
                error = "Empty path segment (consecutive slashes).";
                return false;
            }
            for (var i = 0; i < segment.Length; i++)
            {
                var c = segment[i];
                if (IsUnreserved(c) || IsSubDelim(c) || c == ':' || c == '@') continue;
                if (c == '%')
                {
                    if (i + 2 >= segment.Length || !IsHexChar(segment[i + 1]) || !IsHexChar(segment[i + 2]))
                    {
                        error = $"Malformed percent-encoding at position {i} of segment '{segment}'.";
                        return false;
                    }
                    i += 2;
                    continue;
                }
                error = $"Illegal character '{c}' in path segment '{segment}'.";
                return false;
            }
        }
        return true;
    }

    private static bool IsUnreserved(char c) =>
        (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ||
        c == '-' || c == '.' || c == '_' || c == '~';

    private static bool IsSubDelim(char c) =>
        c == '!' || c == '$' || c == '&' || c == '\'' || c == '(' || c == ')' ||
        c == '*' || c == '+' || c == ',' || c == ';' || c == '=';

    private static bool IsHexChar(char c) =>
        (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f');

    // ── Path helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the path split into segments (after percent-decoding each segment).
    /// Returns an empty array for the root path.
    /// </summary>
    public IReadOnlyList<string> PathSegments
    {
        get
        {
            if (string.IsNullOrEmpty(Path)) return Array.Empty<string>();
            var raw = Path.Split('/');
            var result = new string[raw.Length];
            for (var i = 0; i < raw.Length; i++)
                result[i] = raw[i]; // Path is already decoded.
            return result;
        }
    }

    /// <summary>
    /// Returns the first path segment (the "handler name"), or empty string for root.
    /// </summary>
    public string HandlerName
    {
        get
        {
            if (string.IsNullOrEmpty(Path)) return string.Empty;
            var slash = Path.IndexOf('/');
            return slash >= 0 ? Path.Substring(0, slash) : Path;
        }
    }

    // ── Encoding ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the canonical string form of this URI.
    /// Same value for two URIs that compare equal.
    /// </summary>
    public override string ToString()
    {
        if (!IsValid) return string.Empty;

        var sb = new StringBuilder(64);
        sb.Append(SchemePrefix);
        sb.Append(Authority);
        if (!string.IsNullOrEmpty(Path))
        {
            sb.Append('/');
            // Re-encode the path so the output is RFC-safe.
            EncodePath(sb, Path);
        }
        if (Query.Count > 0)
        {
            sb.Append('?');
            var first = true;
            foreach (var kv in Query)
            {
                if (!first) sb.Append('&');
                first = false;
                EncodeComponent(sb, kv.Key, EncodeKind.QueryKey);
                if (!string.IsNullOrEmpty(kv.Value))
                {
                    sb.Append('=');
                    EncodeComponent(sb, kv.Value, EncodeKind.QueryValue);
                }
            }
        }
        if (!string.IsNullOrEmpty(Fragment))
        {
            sb.Append('#');
            EncodeComponent(sb, Fragment, EncodeKind.Fragment);
        }
        return sb.ToString();
    }

    private enum EncodeKind { PathSegment, QueryKey, QueryValue, Fragment }

    private static void EncodePath(StringBuilder sb, string path)
    {
        var first = true;
        foreach (var segment in path.Split('/'))
        {
            if (!first) sb.Append('/');
            first = false;
            EncodeComponent(sb, segment, EncodeKind.PathSegment);
        }
    }

    private static void EncodeComponent(StringBuilder sb, string value, EncodeKind kind)
    {
        foreach (var c in value)
        {
            if (IsAllowedUnencoded(c, kind)) { sb.Append(c); continue; }
            // Percent-encode UTF-8 bytes.
            var bytes = Encoding.UTF8.GetBytes(new[] { c });
            foreach (var b in bytes)
            {
                sb.Append('%');
                sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }
    }

    private static bool IsAllowedUnencoded(char c, EncodeKind kind)
    {
        if (IsUnreserved(c)) return true;
        switch (kind)
        {
            case EncodeKind.PathSegment:
                // pchar = unreserved / pct-encoded / sub-delims / ":" / "@"
                return IsSubDelim(c) || c == ':' || c == '@';
            case EncodeKind.QueryKey:
                // We always encode '&' and '=' in keys; allow ':' and '@' and the
                // other sub-delims that don't collide with the query syntax.
                return c == ':' || c == '@' ||
                       c == '!' || c == '$' || c == '\'' || c == '(' || c == ')' ||
                       c == '*' || c == '+' || c == ',' || c == ';';
            case EncodeKind.QueryValue:
                // Allow sub-delims except '&' (separator); '=' is fine inside a value.
                return c == ':' || c == '@' || c == '/' || c == '?' ||
                       c == '!' || c == '$' || c == '\'' || c == '(' || c == ')' ||
                       c == '*' || c == '+' || c == ',' || c == ';' || c == '=';
            case EncodeKind.Fragment:
                // fragment = *( pchar / "/" / "?" )  ; pchar incl. ':' '@' sub-delims
                return IsSubDelim(c) || c == ':' || c == '@' || c == '/' || c == '?';
            default:
                return false;
        }
    }

    private static string PercentDecode(string input)
    {
        if (input.IndexOf('%') < 0) return input;

        var bytes = new List<byte>(input.Length);
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (c == '%' && i + 2 < input.Length &&
                IsHexChar(input[i + 1]) && IsHexChar(input[i + 2]))
            {
                bytes.Add((byte)((HexValue(input[i + 1]) << 4) | HexValue(input[i + 2])));
                i += 2;
            }
            else
            {
                // Non-encoded character — emit its UTF-8 bytes.
                foreach (var b in Encoding.UTF8.GetBytes(new[] { c }))
                    bytes.Add(b);
            }
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static string PercentDecodePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        // Decode each segment independently so '/' isn't lost.
        var segs = path.Split('/');
        for (var i = 0; i < segs.Length; i++) segs[i] = PercentDecode(segs[i]);
        return string.Join("/", segs);
    }

    private static int HexValue(char c) =>
        c <= '9' ? c - '0' :
        c <= 'F' ? c - 'A' + 10 :
                   c - 'a' + 10;

    // ── Equality ─────────────────────────────────────────────────────────────

    public bool Equals(AetherUri other)
    {
        if (Authority != other.Authority) return false;
        if (Path != other.Path) return false;
        if (Fragment != other.Fragment) return false;
        if (Query.Count != other.Query.Count) return false;
        foreach (var kv in Query)
            if (!other.Query.TryGetValue(kv.Key, out var v) || v != kv.Value) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is AetherUri other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(Authority, Path, Fragment, Query.Count);

    public static bool operator ==(AetherUri a, AetherUri b) => a.Equals(b);
    public static bool operator !=(AetherUri a, AetherUri b) => !a.Equals(b);
}

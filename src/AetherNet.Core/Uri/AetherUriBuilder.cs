// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using AetherNet.Identity;

namespace AetherNet.Addressing;

/// <summary>
/// Fluent builder for <see cref="AetherUri"/>. Use when programmatically constructing
/// an Aether URI from parts; for parsing an existing string, use <see cref="AetherUri.Parse"/>.
///
/// <h3>Example</h3>
/// <code>
/// var uri = new AetherUriBuilder()
///     .WithAuthority("KXJB7-MN2P4")
///     .WithPath("content/sha256-abc123")
///     .WithQueryParam("codec", "opus")
///     .WithFragment("t=1m30s")
///     .Build();
/// // -> aether://KXJB7-MN2P4/content/sha256-abc123?codec=opus#t=1m30s
/// </code>
/// </summary>
public sealed class AetherUriBuilder
{
    private string? _authority;
    private string _path = string.Empty;
    private readonly Dictionary<string, string> _query =
        new(StringComparer.OrdinalIgnoreCase);
    private string _fragment = string.Empty;

    /// <summary>Sets the authority from an <see cref="AetherNetTag"/>.</summary>
    public AetherUriBuilder WithAuthority(AetherNetTag tag)
    {
        if (!tag.IsValid) throw new AetherUriException("AetherTag is uninitialised.");
        _authority = tag.Value;
        return this;
    }

    /// <summary>Sets the authority from a raw string (AetherTag or 64-char hex UHID).</summary>
    public AetherUriBuilder WithAuthority(string authority)
    {
        if (string.IsNullOrEmpty(authority))
            throw new AetherUriException("Authority is null or empty.");
        // Validate by round-tripping through the parser.
        if (!AetherUri.TryParse($"aether://{authority}", out var u, out var err))
            throw new AetherUriException(err ?? "Invalid authority.");
        _authority = u.Authority;
        return this;
    }

    /// <summary>Sets the path component (without a leading slash).</summary>
    public AetherUriBuilder WithPath(string path)
    {
        _path = path?.TrimStart('/') ?? string.Empty;
        return this;
    }

    /// <summary>Appends a single segment to the path.</summary>
    public AetherUriBuilder AppendPathSegment(string segment)
    {
        if (string.IsNullOrEmpty(segment)) return this;
        _path = string.IsNullOrEmpty(_path)
            ? segment
            : $"{_path}/{segment.TrimStart('/')}";
        return this;
    }

    /// <summary>Adds or replaces a query parameter.</summary>
    public AetherUriBuilder WithQueryParam(string key, string value)
    {
        if (string.IsNullOrEmpty(key))
            throw new AetherUriException("Query key is null or empty.");
        _query[key] = value ?? string.Empty;
        return this;
    }

    /// <summary>Removes a query parameter by key.</summary>
    public AetherUriBuilder RemoveQueryParam(string key)
    {
        _query.Remove(key);
        return this;
    }

    /// <summary>Sets the fragment (without leading '#').</summary>
    public AetherUriBuilder WithFragment(string fragment)
    {
        _fragment = fragment?.TrimStart('#') ?? string.Empty;
        return this;
    }

    /// <summary>Builds the final <see cref="AetherUri"/>. Throws if any component is invalid.</summary>
    public AetherUri Build()
    {
        if (string.IsNullOrEmpty(_authority))
            throw new AetherUriException("Authority is required.");
        // Round-trip through the parser to guarantee canonicalisation + validation.
        return AetherUri.Parse(ToString());
    }

    /// <summary>Returns the URI string this builder currently represents (no validation).</summary>
    public override string ToString()
    {
        if (string.IsNullOrEmpty(_authority))
            return string.Empty;
        var sb = new System.Text.StringBuilder(64);
        sb.Append("aether://");
        sb.Append(_authority);
        if (!string.IsNullOrEmpty(_path))
        {
            sb.Append('/');
            sb.Append(_path);
        }
        if (_query.Count > 0)
        {
            sb.Append('?');
            var first = true;
            foreach (var kv in _query)
            {
                if (!first) sb.Append('&');
                first = false;
                sb.Append(kv.Key);
                if (!string.IsNullOrEmpty(kv.Value))
                {
                    sb.Append('=');
                    sb.Append(kv.Value);
                }
            }
        }
        if (!string.IsNullOrEmpty(_fragment))
        {
            sb.Append('#');
            sb.Append(_fragment);
        }
        return sb.ToString();
    }
}

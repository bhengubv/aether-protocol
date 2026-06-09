// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Linq;

namespace AetherNet.Addressing;

/// <summary>
/// Describes a single handler an app exposes on its <c>aether://</c> URI surface.
///
/// A handler is identified by its first path segment (the <c>HandlerName</c>) plus
/// an optional path-template that captures route parameters. The router matches an
/// incoming URI's <see cref="AetherUri.HandlerName"/> + path against this manifest
/// and dispatches accordingly.
///
/// <h3>Path template syntax</h3>
/// <code>
///   "content/{hash}"             // matches /content/abc
///   "watch/{sessionId}/join"     // matches /watch/123/join
///   "profile"                    // matches /profile exactly
///   "profile/avatar"             // matches /profile/avatar exactly
/// </code>
/// </summary>
public sealed class AetherUriHandlerDescriptor
{
    /// <summary>Handler name — the first path segment (e.g. "content", "stream").</summary>
    public string HandlerName { get; }

    /// <summary>Path template (e.g. "content/{hash}") — empty for a root handler.</summary>
    public string PathTemplate { get; }

    /// <summary>Optional list of expected query keys (informational).</summary>
    public IReadOnlyList<string> ExpectedQueryKeys { get; }

    /// <summary>Human-readable description for diagnostics + docs.</summary>
    public string Description { get; }

    public AetherUriHandlerDescriptor(
        string handlerName,
        string pathTemplate = "",
        IReadOnlyList<string>? expectedQueryKeys = null,
        string description = "")
    {
        if (string.IsNullOrWhiteSpace(handlerName))
            throw new AetherUriException("HandlerName is required.");
        HandlerName = handlerName;
        PathTemplate = pathTemplate ?? string.Empty;
        ExpectedQueryKeys = expectedQueryKeys ?? Array.Empty<string>();
        Description = description ?? string.Empty;
    }

    /// <summary>
    /// Matches an incoming URI's path against this descriptor's template.
    /// Returns the captured route parameters on success, or null on no match.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Match(string path)
    {
        var templateSegs = string.IsNullOrEmpty(PathTemplate)
            ? new[] { HandlerName }
            : (HandlerName + "/" + PathTemplate.TrimStart('/')).Split('/');
        var pathSegs = path.Split('/');
        if (templateSegs.Length != pathSegs.Length) return null;

        var captures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < templateSegs.Length; i++)
        {
            var t = templateSegs[i];
            var p = pathSegs[i];
            if (t.Length >= 2 && t[0] == '{' && t[t.Length - 1] == '}')
            {
                captures[t.Substring(1, t.Length - 2)] = p;
            }
            else if (!string.Equals(t, p, StringComparison.Ordinal))
            {
                return null;
            }
        }
        return captures;
    }
}

/// <summary>
/// An app's complete <c>aether://</c> handler manifest — the set of paths it accepts.
/// Each app registers exactly one manifest at startup; the router dispatches against it.
/// </summary>
public sealed class AetherUriHandlerManifest
{
    /// <summary>The owning app's identifier (e.g. "aether.media", "aether.txtme").</summary>
    public string AppId { get; }

    /// <summary>All registered handler descriptors.</summary>
    public IReadOnlyList<AetherUriHandlerDescriptor> Handlers { get; }

    public AetherUriHandlerManifest(string appId, IReadOnlyList<AetherUriHandlerDescriptor> handlers)
    {
        if (string.IsNullOrWhiteSpace(appId))
            throw new AetherUriException("AppId is required.");
        AppId = appId;
        Handlers = handlers ?? Array.Empty<AetherUriHandlerDescriptor>();
    }

    /// <summary>
    /// Resolves an incoming URI against this manifest. Returns the matched descriptor
    /// and its captured route parameters, or null if no handler matched.
    /// </summary>
    public (AetherUriHandlerDescriptor Handler, IReadOnlyDictionary<string, string> Captures)?
        Resolve(AetherUri uri)
    {
        if (!uri.IsValid) return null;
        foreach (var h in Handlers.Where(h =>
            string.Equals(h.HandlerName, uri.HandlerName, StringComparison.Ordinal)))
        {
            var captures = h.Match(uri.Path);
            if (captures is not null) return (h, captures);
        }
        return null;
    }
}

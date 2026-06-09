// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AetherNet.Addressing;

/// <summary>
/// Context delivered to a registered URI handler when its route matches.
/// Carries the original URI plus any captured route parameters.
/// </summary>
public sealed class AetherUriDispatchContext
{
    public AetherUri Uri { get; }
    public AetherUriHandlerDescriptor Handler { get; }
    public IReadOnlyDictionary<string, string> RouteParameters { get; }

    public AetherUriDispatchContext(
        AetherUri uri,
        AetherUriHandlerDescriptor handler,
        IReadOnlyDictionary<string, string> routeParameters)
    {
        Uri = uri;
        Handler = handler;
        RouteParameters = routeParameters;
    }
}

/// <summary>
/// Dispatches an incoming <c>aether://</c> URI to the registered handler for its
/// route. The router is per-app — each app constructs one with its own manifest.
///
/// <h3>Lifecycle</h3>
/// <list type="number">
///   <item><description>App startup: build an <see cref="AetherUriHandlerManifest"/> describing every
///     route the app accepts.</description></item>
///   <item><description>App startup: register a callback per <see cref="AetherUriHandlerDescriptor"/>
///     via <see cref="RegisterHandler"/>.</description></item>
///   <item><description>At runtime: when a URI is received (incoming intent, deep link, or
///     in-mesh dispatch), call <see cref="DispatchAsync"/> to invoke the right callback.</description></item>
/// </list>
/// </summary>
public interface IAetherUriRouter
{
    /// <summary>The manifest the router resolves against.</summary>
    AetherUriHandlerManifest Manifest { get; }

    /// <summary>
    /// Register a callback for a handler descriptor. The descriptor must be one
    /// present in <see cref="Manifest"/>. Re-registering replaces the existing callback.
    /// </summary>
    void RegisterHandler(
        AetherUriHandlerDescriptor descriptor,
        Func<AetherUriDispatchContext, CancellationToken, Task> handler);

    /// <summary>
    /// Resolve and dispatch a URI. Returns <c>true</c> if a handler was matched
    /// AND invoked; returns <c>false</c> if no handler matched. The handler's exceptions
    /// propagate to the caller.
    /// </summary>
    Task<bool> DispatchAsync(AetherUri uri, CancellationToken ct = default);

    /// <summary>
    /// Resolve and dispatch a URI given as a string. Throws <see cref="AetherUriException"/>
    /// if the string fails to parse.
    /// </summary>
    Task<bool> DispatchAsync(string uri, CancellationToken ct = default);
}

/// <summary>
/// Reference in-process implementation of <see cref="IAetherUriRouter"/>.
/// Thread-safe.
/// </summary>
public sealed class AetherUriRouter : IAetherUriRouter
{
    private readonly Dictionary<AetherUriHandlerDescriptor, Func<AetherUriDispatchContext, CancellationToken, Task>>
        _handlers = new();
    private readonly object _lock = new();

    public AetherUriHandlerManifest Manifest { get; }

    public AetherUriRouter(AetherUriHandlerManifest manifest)
    {
        Manifest = manifest ?? throw new AetherUriException("Manifest is null.");
    }

    public void RegisterHandler(
        AetherUriHandlerDescriptor descriptor,
        Func<AetherUriDispatchContext, CancellationToken, Task> handler)
    {
        if (descriptor is null) throw new AetherUriException("Descriptor is null.");
        if (handler is null) throw new AetherUriException("Handler is null.");
        if (!Manifest.Handlers.Contains(descriptor))
            throw new AetherUriException(
                $"Descriptor '{descriptor.HandlerName}' is not in the manifest.");
        lock (_lock) { _handlers[descriptor] = handler; }
    }

    public async Task<bool> DispatchAsync(AetherUri uri, CancellationToken ct = default)
    {
        var resolved = Manifest.Resolve(uri);
        if (resolved is null) return false;
        Func<AetherUriDispatchContext, CancellationToken, Task>? cb;
        lock (_lock) { _handlers.TryGetValue(resolved.Value.Handler, out cb); }
        if (cb is null) return false;
        var ctx = new AetherUriDispatchContext(uri, resolved.Value.Handler, resolved.Value.Captures);
        await cb(ctx, ct).ConfigureAwait(false);
        return true;
    }

    public Task<bool> DispatchAsync(string uri, CancellationToken ct = default)
        => DispatchAsync(AetherUri.Parse(uri), ct);
}

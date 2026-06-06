// SPDX-License-Identifier: MIT

using AetherNet.Extensibility.Events;

namespace AetherNet.Extensibility;

// ─────────────────────────────────────────────────────────────────────────────
//  Telemetry publication surface
//
//  Aether owns and publishes. The AI layer (CircleAI / BhenguAI) subscribes.
//  Aether never calls into the AI — traffic is strictly one-way outward.
//  External Aether adopters can implement IAetherNetTelemetry without pulling in
//  any AI dependency.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Receives all telemetry events emitted by the Aether runtime. Implement this
/// interface to react to mesh activity — nodes, transports, routes, security
/// signals, and topology changes.
///
/// <para>
/// All callbacks are invoked synchronously on the caller's thread. Implementations
/// must not block, throw, or call back into Aether.
/// </para>
/// </summary>
public interface IAetherNetTelemetryObserver
{
    /// <summary>A node joined, left, or its health metrics changed.</summary>
    void OnNodeEvent(AetherNetNodeEvent e);

    /// <summary>A transport was selected, switched, measured, or experienced packet loss.</summary>
    void OnTransportEvent(AetherNetTransportEvent e);

    /// <summary>A route was discovered, updated, or failed.</summary>
    void OnRouteEvent(AetherNetRouteEvent e);

    /// <summary>
    /// A protocol-layer security anomaly was detected. This is the primary feed
    /// for the AI Security Layer — the AI subscribes here and may subsequently
    /// publish a <see cref="SecurityDirective"/> via <see cref="ISecurityDirectiveConsumer"/>.
    /// </summary>
    void OnSecurityEvent(AetherNetSecurityEvent e);

    /// <summary>The mesh topology or overall congestion level changed materially.</summary>
    void OnNetworkEvent(AetherNetNetworkEvent e);
}

/// <summary>
/// The outward-facing telemetry publication surface of the Aether runtime.
/// The AI Security Layer and any other observer subscribes here. Aether publishes;
/// observers subscribe and dispose when no longer needed.
///
/// <para>
/// Register an implementation via DI and call
/// <c>IAetherNetProtocolBuilder.AddTelemetry&lt;T&gt;</c> (or the extension overload
/// accepting an <see cref="IAetherNetTelemetryObserver"/> instance) to wire it up.
/// When no implementation is registered, <see cref="NullAetherNetTelemetry"/> is used
/// and all events are silently discarded.
/// </para>
/// </summary>
public interface IAetherNetTelemetry
{
    /// <summary>
    /// Subscribe to all Aether telemetry events. Dispose the returned handle to
    /// unsubscribe. Subscribing the same observer more than once is a no-op — only
    /// one subscription per observer instance is maintained.
    /// </summary>
    IDisposable Subscribe(IAetherNetTelemetryObserver observer);

    /// <summary>
    /// Publish a node lifecycle event to all registered observers. Called internally
    /// by Aether services; external code should not need to call this directly.
    /// </summary>
    void Publish(AetherNetNodeEvent e);

    /// <summary>Publish a transport quality event to all registered observers.</summary>
    void Publish(AetherNetTransportEvent e);

    /// <summary>Publish a route discovery or failure event to all registered observers.</summary>
    void Publish(AetherNetRouteEvent e);

    /// <summary>Publish a protocol-layer security event to all registered observers.</summary>
    void Publish(AetherNetSecurityEvent e);

    /// <summary>Publish a mesh topology change event to all registered observers.</summary>
    void Publish(AetherNetNetworkEvent e);
}

/// <summary>
/// Thread-safe <see cref="IAetherNetTelemetry"/> implementation that fans out to all
/// registered observers. This is the default implementation registered by
/// <c>AddAetherNetProtocol()</c>.
///
/// <para>
/// Publish calls invoke each observer synchronously in subscription order. Observer
/// exceptions are swallowed — a misbehaving subscriber cannot disrupt the mesh.
/// </para>
/// </summary>
public sealed class AetherNetTelemetryBus : IAetherNetTelemetry
{
    private readonly Lock _lock = new();
    private readonly List<IAetherNetTelemetryObserver> _observers = [];

    /// <inheritdoc/>
    public IDisposable Subscribe(IAetherNetTelemetryObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_lock)
        {
            if (!_observers.Contains(observer))
                _observers.Add(observer);
        }
        return new Subscription(this, observer);
    }

    private void Unsubscribe(IAetherNetTelemetryObserver observer)
    {
        lock (_lock) { _observers.Remove(observer); }
    }

    private IReadOnlyList<IAetherNetTelemetryObserver> Snapshot()
    {
        lock (_lock) { return [.._observers]; }
    }

    /// <inheritdoc/>
    public void Publish(AetherNetNodeEvent e)
    {
        foreach (var obs in Snapshot())
            try { obs.OnNodeEvent(e); } catch { /* observer must not break the mesh */ }
    }

    /// <inheritdoc/>
    public void Publish(AetherNetTransportEvent e)
    {
        foreach (var obs in Snapshot())
            try { obs.OnTransportEvent(e); } catch { }
    }

    /// <inheritdoc/>
    public void Publish(AetherNetRouteEvent e)
    {
        foreach (var obs in Snapshot())
            try { obs.OnRouteEvent(e); } catch { }
    }

    /// <inheritdoc/>
    public void Publish(AetherNetSecurityEvent e)
    {
        foreach (var obs in Snapshot())
            try { obs.OnSecurityEvent(e); } catch { }
    }

    /// <inheritdoc/>
    public void Publish(AetherNetNetworkEvent e)
    {
        foreach (var obs in Snapshot())
            try { obs.OnNetworkEvent(e); } catch { }
    }

    private sealed class Subscription(AetherNetTelemetryBus bus, IAetherNetTelemetryObserver observer) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                bus.Unsubscribe(observer);
        }
    }
}

/// <summary>
/// No-op <see cref="IAetherNetTelemetry"/> — used when no telemetry consumer is
/// registered. Subscribe returns a no-op disposable; all Publish calls do nothing.
/// </summary>
public sealed class NullAetherNetTelemetry : IAetherNetTelemetry
{
    /// <summary>The singleton instance.</summary>
    public static readonly NullAetherNetTelemetry Instance = new();

    private NullAetherNetTelemetry() { }

    /// <inheritdoc/>
    public IDisposable Subscribe(IAetherNetTelemetryObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        return NullDisposable.Instance;
    }

    /// <inheritdoc/>
    public void Publish(AetherNetNodeEvent e)      { }
    /// <inheritdoc/>
    public void Publish(AetherNetTransportEvent e) { }
    /// <inheritdoc/>
    public void Publish(AetherNetRouteEvent e)     { }
    /// <inheritdoc/>
    public void Publish(AetherNetSecurityEvent e)  { }
    /// <inheritdoc/>
    public void Publish(AetherNetNetworkEvent e)   { }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();
        public void Dispose() { }
    }
}

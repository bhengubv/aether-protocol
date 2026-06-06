// SPDX-License-Identifier: MIT

using AetherMesh.Constants;

namespace AetherMesh.DependencyInjection;

/// <summary>
/// Root configuration for <c>services.AddAetherMeshProtocol(...)</c>. Bind from
/// <c>IConfiguration</c> via the standard Options pattern, e.g.:
/// <code>
/// services.AddAetherMeshProtocol(opts => configuration.GetSection("Aether").Bind(opts));
/// </code>
///
/// Defaults are chosen to match <see cref="ProtocolConstants"/> so a host that
/// registers Aether with no explicit configuration gets the same behaviour as
/// the bundled sample console.
/// </summary>
public sealed class AetherMeshOptions
{
    /// <summary>
    /// The local node's UHID. Required for the messaging/transport stack;
    /// services that need it (transports, mesh-sender adapters) read it from
    /// here at registration time.
    /// </summary>
    public string LocalUhid { get; set; } = "";

    /// <summary>Routing-layer tunables.</summary>
    public RoutingOptionsBag Routing { get; } = new();

    /// <summary>DTN store-and-forward tunables.</summary>
    public DtnOptionsBag Dtn { get; } = new();

    /// <summary>Signal Protocol identity tunables.</summary>
    public SignalOptionsBag Signal { get; } = new();

    /// <summary>Messaging layer tunables.</summary>
    public MessagingOptionsBag Messaging { get; } = new();
}

/// <summary>
/// Routing-layer health and behaviour thresholds. Naming uses <c>Bag</c>
/// suffix to disambiguate from the existing <c>AetherMesh.Routing</c> types.
/// </summary>
public sealed class RoutingOptionsBag
{
    /// <summary>
    /// Routing table size at which the routing health check transitions to
    /// degraded. Default 10 000 — well above any real-world mesh that fits
    /// in a single process, intended to catch routing-state leaks.
    /// </summary>
    public int DegradedTableSize { get; set; } = 10_000;

    /// <summary>
    /// Routing table size at which the routing health check transitions to
    /// unhealthy. Default 50 000 — clear evidence of a leak.
    /// </summary>
    public int UnhealthyTableSize { get; set; } = 50_000;
}

/// <summary>DTN store-and-forward tunables.</summary>
public sealed class DtnOptionsBag
{
    /// <summary>
    /// Maximum number of bundles a node will hold in custody. Mirrors
    /// <see cref="ProtocolConstants.DtnMaxBundlesPerNode"/> — the health
    /// check uses this value as the unhealthy threshold.
    /// </summary>
    public int MaxBundles { get; set; } = ProtocolConstants.DtnMaxBundlesPerNode;

    /// <summary>
    /// Fraction of <see cref="MaxBundles"/> at which the DTN health check
    /// transitions to degraded. Default 0.8 — leaves headroom before custody
    /// refusal kicks in.
    /// </summary>
    public double DegradedFraction { get; set; } = 0.8;
}

/// <summary>Signal Protocol identity tunables.</summary>
public sealed class SignalOptionsBag
{
    /// <summary>
    /// Active session count at which the Signal health check transitions
    /// to degraded. Default 1 000 — flags potential session leaks while
    /// staying well above any reasonable peer count for a single node.
    /// </summary>
    public int DegradedSessionCount { get; set; } = 1_000;

    /// <summary>
    /// Available one-time pre-key pool size at which the Signal health
    /// check transitions to unhealthy. Below this floor, responder X3DH
    /// will start failing for new initiators. Default 10.
    /// </summary>
    public int MinAvailableOpks { get; set; } = 10;
}

/// <summary>Messaging layer tunables.</summary>
public sealed class MessagingOptionsBag
{
    /// <summary>
    /// Outbox queue depth at which the messaging health check transitions
    /// to degraded. Default 100. Above this, two consecutive samples that
    /// are both growing flip the check to unhealthy.
    /// </summary>
    public int DegradedOutboxSize { get; set; } = 100;

    /// <summary>
    /// Maximum send attempts before a queued message transitions to
    /// <c>Failed</c>. Mirrors <c>MessagingOptions.MaxRetries</c>.
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>If true, fall back to DTN store-and-forward when no live route exists.</summary>
    public bool EnableDtnFallback { get; set; } = true;

    /// <summary>If true, fall back to a registered backend client when no mesh route exists.</summary>
    public bool EnableBackendRelay { get; set; } = true;

    /// <summary>If true, send delivery acks for every received data packet.</summary>
    public bool SendDeliveryAcks { get; set; } = true;
}

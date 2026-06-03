// SPDX-License-Identifier: MIT

using Aether.Extensibility;
using Aether.Reputation;
using Microsoft.Extensions.DependencyInjection;

namespace Aether.DependencyInjection;

/// <summary>
/// Fluent builder returned by
/// <see cref="AetherProtocolServiceCollectionExtensions.AddAetherProtocol"/>.
/// Each capability is opt-in: hosts that need only routing get only routing,
/// hosts that need the full stack chain everything together.
///
/// Canonical full-stack wiring:
/// <code>
/// services.AddAetherProtocol(opts =&gt; opts.LocalUhid = "aether:alice:01")
///         .AddSignalProtocol()
///         .AddRouting()
///         .AddDtn()
///         .AddSosBroadcast()
///         .AddMessaging()
///         .AddStreaming()
///         .AddWatchTogether()
///         .AddVideoCall()
///         .AddGroupVideo()
///         .AddVoice()
///         .AddGroupVoice()
///         .AddContent()
///         .AddReputation()
///         .AddGossip()
///         .AddHandshake()
///         .AddInProcessTransport("aether:alice:01")
///         .AddHealthChecks();
/// </code>
/// </summary>
public interface IAetherProtocolBuilder
{
    /// <summary>The underlying service collection — exposed so adopters can register their own seam implementations.</summary>
    IServiceCollection Services { get; }

    /// <summary>
    /// Register <c>SignalProtocolService</c> (and the <c>PacketSigningService</c>
    /// that depends on it) as singletons. Once added, hosts can resolve
    /// <c>ISignalProtocolService</c> and <c>IPacketSigningService</c>.
    /// </summary>
    IAetherProtocolBuilder AddSignalProtocol();

    /// <summary>
    /// Register <c>RoutingService</c> as a singleton. Requires the host to
    /// have registered an <c>IMeshSender</c> implementation already (the
    /// transport adapter). The default <c>InMemoryRouteStore</c> is used
    /// unless the host has registered an <c>IRouteStore</c>.
    /// </summary>
    IAetherProtocolBuilder AddRouting();

    /// <summary>
    /// Register <c>DtnService</c> as a singleton. Requires <c>IMeshSender</c>;
    /// uses <c>InMemoryDtnBundleStore</c> by default.
    /// </summary>
    IAetherProtocolBuilder AddDtn();

    /// <summary>
    /// Register <c>SosBroadcastService</c> as a singleton. Requires <c>IMeshSender</c>.
    /// </summary>
    IAetherProtocolBuilder AddSosBroadcast();

    /// <summary>
    /// Register <c>MessagingService</c> + <c>SignalMessageEnvelopeCipher</c>
    /// as singletons. Requires both <see cref="AddSignalProtocol"/> and
    /// <see cref="AddRouting"/> to have been called first; throws
    /// <see cref="InvalidOperationException"/> otherwise (with a clear message).
    /// </summary>
    IAetherProtocolBuilder AddMessaging();

    /// <summary>
    /// Register the in-process transport adapter for the given local UHID.
    /// Wires <c>IMeshSender</c> via a thin transport-bridge so routing/DTN/messaging
    /// can run end-to-end without an external transport. Suitable for tests
    /// and demos; not for production. The transport adapter is registered as
    /// a singleton.
    /// </summary>
    IAetherProtocolBuilder AddInProcessTransport(string localUhid);

    /// <summary>
    /// Register the four protocol-level <c>IHealthCheck</c> implementations
    /// (routing, DTN, Signal, messaging-outbox) with the host's
    /// <c>IHealthChecksBuilder</c>. The host must have already called
    /// <c>services.AddHealthChecks()</c> before reaching this method;
    /// otherwise the registrations are a no-op.
    /// </summary>
    IAetherProtocolBuilder AddHealthChecks();

    /// <summary>
    /// Register <c>HandshakeService</c> as a singleton. Requires <c>IMeshSender</c>.
    /// Wires the protocol-version + capability negotiation entry point that
    /// runs on first contact with each peer (<c>PacketType.Hello</c> /
    /// <c>PacketType.HelloAck</c>). Once added, hosts can resolve
    /// <c>IHandshakeService</c>.
    /// </summary>
    IAetherProtocolBuilder AddHandshake();

    /// <summary>
    /// Register <c>InMemoryNodeReputationService</c> as a singleton
    /// <c>INodeReputationService</c>. Once added, the reputation score is
    /// automatically plumbed into <c>RoutingService</c>, <c>PacketSigningService</c>,
    /// and <c>DtnService</c> as an optional dependency at resolve time.
    /// </summary>
    IAetherProtocolBuilder AddReputation();

    /// <summary>
    /// Register <c>BehavioralAnomalyDetector</c> as a singleton
    /// <c>IAnomalyDetector</c>. Requires <see cref="AddReputation"/> to have
    /// been called first; throws <see cref="InvalidOperationException"/> otherwise.
    /// </summary>
    IAetherProtocolBuilder AddAnomalyDetector(Action<AnomalyDetectorOptions>? configure = null);

    /// <summary>
    /// Register <c>ReputationGossipService</c> as a singleton
    /// <c>IReputationGossipService</c>. Requires both <see cref="AddReputation"/>
    /// and <see cref="AddSignalProtocol"/> to have been called first; throws
    /// <see cref="InvalidOperationException"/> otherwise.
    /// </summary>
    IAetherProtocolBuilder AddGossip();

    // ── Media layer ───────────────────────────────────────────────────────────

    /// <summary>
    /// Register <c>StreamingService</c> as a singleton <c>IStreamingService</c>.
    /// Requires <see cref="AddRouting"/>. Picks up <c>IAetherIncentiveProvider</c>
    /// automatically if registered by the host (e.g. SDPKT).
    /// </summary>
    IAetherProtocolBuilder AddStreaming();

    /// <summary>
    /// Register <c>WatchTogetherService</c> as a singleton <c>IWatchTogetherService</c>.
    /// Requires <see cref="AddRouting"/>. Picks up <c>IAetherIncentiveProvider</c>
    /// automatically if registered (ChipIn requires a concrete implementation).
    /// </summary>
    IAetherProtocolBuilder AddWatchTogether();

    /// <summary>
    /// Register <c>VideoCallService</c> as a singleton <c>IVideoCallService</c>.
    /// Requires <see cref="AddRouting"/>.
    /// </summary>
    IAetherProtocolBuilder AddVideoCall();

    /// <summary>
    /// Register <c>GroupVideoService</c> as a singleton <c>IGroupVideoService</c>.
    /// Requires <see cref="AddRouting"/>.
    /// </summary>
    IAetherProtocolBuilder AddGroupVideo();

    /// <summary>
    /// Register <c>VoiceCallService</c> as a singleton <c>IVoiceCallService</c>.
    /// Requires <see cref="AddRouting"/>.
    /// </summary>
    IAetherProtocolBuilder AddVoice();

    /// <summary>
    /// Register <c>GroupVoiceCallService</c> as a singleton <c>IGroupVoiceCallService</c>.
    /// Requires <see cref="AddRouting"/>. Picks up <c>IGroupKeyProvider</c> automatically
    /// if registered by the host.
    /// </summary>
    IAetherProtocolBuilder AddGroupVoice();

    /// <summary>
    /// Register <c>ContentService</c> as a singleton <c>IContentService</c>.
    /// Requires <see cref="AddRouting"/>. Uses <c>InMemoryContentStore</c> by default
    /// unless the host has registered an <c>IContentStore</c> beforehand.
    /// Picks up <c>IAetherIncentiveProvider</c> automatically if registered.
    /// </summary>
    IAetherProtocolBuilder AddContent();

    // ── Extensibility ─────────────────────────────────────────────────────────

    /// <summary>
    /// Register <typeparamref name="T"/> as a singleton <see cref="IAetherTelemetryObserver"/>.
    /// Multiple observers may be registered; the <see cref="AetherTelemetryBus"/> singleton
    /// resolves all of them and fans out every publish call. Calling this does NOT replace
    /// other already-registered observers — use multiple calls to build up the subscriber set.
    /// </summary>
    IAetherProtocolBuilder AddTelemetry<T>() where T : class, IAetherTelemetryObserver;

    /// <summary>
    /// Register an existing <see cref="IAetherTelemetryObserver"/> instance. The instance
    /// is added to the bus alongside any type-based registrations. Idempotent — if the
    /// same instance reference is passed twice only one registration is created.
    /// </summary>
    IAetherProtocolBuilder AddTelemetry(IAetherTelemetryObserver observer);

    /// <summary>
    /// Replace the default <see cref="NullAetherAiProvider"/> with <typeparamref name="T"/>.
    /// Used by CircleAI and BhenguAI host packages to wire their provider into Aether's
    /// route-suggestion, transport-biasing, and threat-assessment hooks.
    /// </summary>
    IAetherProtocolBuilder AddCircleAI<T>() where T : class, IAetherAiProvider;

    /// <summary>
    /// Replace the default <see cref="NullAetherAiProvider"/> with an existing instance.
    /// </summary>
    IAetherProtocolBuilder AddCircleAI(IAetherAiProvider provider);

    /// <summary>
    /// Replace the default <see cref="NullBiometricProvider"/> with <typeparamref name="T"/>.
    /// Used by SDPKT and mobile-biometric host packages to gate sensitive mesh operations.
    /// </summary>
    IAetherProtocolBuilder AddBiometrics<T>() where T : class, IBiometricProvider;

    /// <summary>
    /// Replace the default <see cref="NullBiometricProvider"/> with an existing instance.
    /// </summary>
    IAetherProtocolBuilder AddBiometrics(IBiometricProvider provider);

    /// <summary>
    /// Replace the default <see cref="NullAetherContextMemory"/> with <typeparamref name="T"/>.
    /// Used by CircleAI / mempalace to give the AI layer durable semantic memory over mesh
    /// route and behaviour history.
    /// </summary>
    IAetherProtocolBuilder AddContextMemory<T>() where T : class, IAetherContextMemory;

    /// <summary>
    /// Replace the default <see cref="NullAetherContextMemory"/> with an existing instance.
    /// </summary>
    IAetherProtocolBuilder AddContextMemory(IAetherContextMemory memory);

    /// <summary>
    /// Replace the default <see cref="NullAetherSecurityAudit"/> with <typeparamref name="T"/>.
    /// Used by Claude-BugHunter and security monitoring packages to perform static and
    /// runtime vulnerability scanning over mesh packets and node behaviour.
    /// </summary>
    IAetherProtocolBuilder AddSecurityAudit<T>() where T : class, IAetherSecurityAudit;

    /// <summary>
    /// Replace the default <see cref="NullAetherSecurityAudit"/> with an existing instance.
    /// </summary>
    IAetherProtocolBuilder AddSecurityAudit(IAetherSecurityAudit auditor);

    // ── Phase-2 Extensions ────────────────────────────────────────────────────

    /// <summary>
    /// Register <c>InMemorySpaceService</c> as a singleton <c>ISpaceService</c>
    /// (aether-space Phase-2 extension — geo-pinned community noticeboards).
    /// Requires <see cref="AddContent"/> and <see cref="AddDtn"/> to have been
    /// called first; throws <see cref="InvalidOperationException"/> otherwise.
    /// </summary>
    IAetherProtocolBuilder AddSpace();

    /// <summary>
    /// Register <c>InMemoryForgeService</c> as a singleton <c>IForgeService</c>
    /// (aether-forge Phase-2 extension — mesh-native package cache proxy).
    /// Requires <see cref="AddContent"/> to have been called first.
    /// </summary>
    IAetherProtocolBuilder AddForge();

    /// <summary>
    /// Register <c>InMemoryVaultService</c> as a singleton <c>IVaultService</c>
    /// (aether-vault Phase-2 extension — erasure-coded distributed backup, k=10 m=4).
    /// Requires <see cref="AddContent"/> to have been called first.
    /// </summary>
    IAetherProtocolBuilder AddVault();

    /// <summary>
    /// Register <c>InMemoryPoVService</c> and <c>InMemoryMarketService</c> as singletons
    /// (aether-market Phase-2 extension — offline P2P commerce with PoV anti-Sybil trust).
    /// Requires <see cref="AddSpace"/> and <see cref="AddVault"/> to have been called first;
    /// throws <see cref="InvalidOperationException"/> otherwise.
    /// </summary>
    IAetherProtocolBuilder AddMarket();
}

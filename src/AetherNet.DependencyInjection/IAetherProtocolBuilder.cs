// SPDX-License-Identifier: MIT

using AetherNet.Extensibility;
using AetherNet.Reputation;
using Microsoft.Extensions.DependencyInjection;

namespace AetherNet.DependencyInjection;

/// <summary>
/// Fluent builder returned by
/// <see cref="AetherNetProtocolServiceCollectionExtensions.AddAetherNetProtocol"/>.
/// Each capability is opt-in: hosts that need only routing get only routing,
/// hosts that need the full stack chain everything together.
///
/// Canonical full-stack wiring:
/// <code>
/// services.AddAetherNetProtocol(opts =&gt; opts.LocalUhid = "aether:alice:01")
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
public interface IAetherNetProtocolBuilder
{
    /// <summary>The underlying service collection — exposed so adopters can register their own seam implementations.</summary>
    IServiceCollection Services { get; }

    /// <summary>
    /// Register <c>SignalProtocolService</c> (and the <c>PacketSigningService</c>
    /// that depends on it) as singletons. Once added, hosts can resolve
    /// <c>ISignalProtocolService</c> and <c>IPacketSigningService</c>.
    /// </summary>
    IAetherNetProtocolBuilder AddSignalProtocol();

    /// <summary>
    /// Register <c>RoutingService</c> as a singleton. Requires the host to
    /// have registered an <c>IMeshSender</c> implementation already (the
    /// transport adapter). The default <c>InMemoryRouteStore</c> is used
    /// unless the host has registered an <c>IRouteStore</c>.
    /// </summary>
    IAetherNetProtocolBuilder AddRouting();

    /// <summary>
    /// Register <c>DtnService</c> as a singleton. Requires <c>IMeshSender</c>;
    /// uses <c>InMemoryDtnBundleStore</c> by default.
    /// </summary>
    IAetherNetProtocolBuilder AddDtn();

    /// <summary>
    /// Register <c>SosBroadcastService</c> as a singleton. Requires <c>IMeshSender</c>.
    /// </summary>
    IAetherNetProtocolBuilder AddSosBroadcast();

    /// <summary>
    /// Register <c>HeartbeatService</c> as a singleton <c>IHeartbeatService</c>
    /// (<c>PacketType.Heartbeat</c> liveness beacons — periodic single-hop broadcast + per-peer
    /// liveness tracking). Requires <c>IMeshSender</c>.
    /// </summary>
    IAetherNetProtocolBuilder AddHeartbeat();

    /// <summary>
    /// Register <c>ChannelMessageService</c> as a singleton <c>IChannelMessageService</c>
    /// (<c>PacketType.ChannelMessage</c> named-channel pub/sub — subscribe, publish-flood, de-dup,
    /// re-flood). Requires <c>IMeshSender</c>.
    /// </summary>
    IAetherNetProtocolBuilder AddChannels();

    /// <summary>
    /// Register <c>ProfileService</c> as a singleton <c>IProfileService</c>
    /// (<c>PacketType.ProfileSync</c> directed peer-profile exchange + cache). Requires <c>IMeshSender</c>.
    /// </summary>
    IAetherNetProtocolBuilder AddProfiles();

    /// <summary>
    /// Register <c>VideoCallControlService</c> as a singleton <c>IVideoCallControlService</c>
    /// (<c>PacketType.VideoCall</c> directed call-control signalling — ring/accept/decline/hangup).
    /// Distinct from the media-plane <see cref="AddVideoCall"/> (SDP/ICE + frames). Requires <c>IMeshSender</c>.
    /// </summary>
    IAetherNetProtocolBuilder AddVideoCallControl();

    /// <summary>
    /// Register <c>PreKeyExchangeService</c> as a singleton <c>IPreKeyExchangeService</c>
    /// (<c>PacketType.PreKeyRequest</c>/<c>PreKeyResponse</c> directed pre-key bundle exchange over the
    /// mesh — the host feeds bundles in/out via ISignalProtocolService). Requires <c>IMeshSender</c>.
    /// </summary>
    IAetherNetProtocolBuilder AddPreKeyExchange();

    /// <summary>
    /// Register <c>MessagingService</c> + <c>SignalMessageEnvelopeCipher</c>
    /// as singletons. Requires both <see cref="AddSignalProtocol"/> and
    /// <see cref="AddRouting"/> to have been called first; throws
    /// <see cref="InvalidOperationException"/> otherwise (with a clear message).
    /// </summary>
    IAetherNetProtocolBuilder AddMessaging();

    /// <summary>
    /// Register the in-process transport adapter for the given local UHID.
    /// Wires <c>IMeshSender</c> via a thin transport-bridge so routing/DTN/messaging
    /// can run end-to-end without an external transport. Suitable for tests
    /// and demos; not for production. The transport adapter is registered as
    /// a singleton.
    /// </summary>
    IAetherNetProtocolBuilder AddInProcessTransport(string localUhid);

    /// <summary>
    /// Register the four protocol-level <c>IHealthCheck</c> implementations
    /// (routing, DTN, Signal, messaging-outbox) with the host's
    /// <c>IHealthChecksBuilder</c>. The host must have already called
    /// <c>services.AddHealthChecks()</c> before reaching this method;
    /// otherwise the registrations are a no-op.
    /// </summary>
    IAetherNetProtocolBuilder AddHealthChecks();

    /// <summary>
    /// Register <c>HandshakeService</c> as a singleton. Requires <c>IMeshSender</c>.
    /// Wires the protocol-version + capability negotiation entry point that
    /// runs on first contact with each peer (<c>PacketType.Hello</c> /
    /// <c>PacketType.HelloAck</c>). Once added, hosts can resolve
    /// <c>IHandshakeService</c>.
    /// </summary>
    IAetherNetProtocolBuilder AddHandshake();

    /// <summary>
    /// Register <c>InMemoryNodeReputationService</c> as a singleton
    /// <c>INodeReputationService</c>. Once added, the reputation score is
    /// automatically plumbed into <c>RoutingService</c>, <c>PacketSigningService</c>,
    /// and <c>DtnService</c> as an optional dependency at resolve time.
    /// </summary>
    IAetherNetProtocolBuilder AddReputation();

    /// <summary>
    /// Register <c>BehavioralAnomalyDetector</c> as a singleton
    /// <c>IAnomalyDetector</c>. Requires <see cref="AddReputation"/> to have
    /// been called first; throws <see cref="InvalidOperationException"/> otherwise.
    /// </summary>
    IAetherNetProtocolBuilder AddAnomalyDetector(Action<AnomalyDetectorOptions>? configure = null);

    /// <summary>
    /// Register <c>ReputationGossipService</c> as a singleton
    /// <c>IReputationGossipService</c>. Requires both <see cref="AddReputation"/>
    /// and <see cref="AddSignalProtocol"/> to have been called first; throws
    /// <see cref="InvalidOperationException"/> otherwise.
    /// </summary>
    IAetherNetProtocolBuilder AddGossip();

    /// <summary>
    /// Register <c>MeshTipService</c> as a singleton <c>IMeshTipService</c>
    /// (generic <c>PacketType.TipPacket</c> send/receive surface). Requires both
    /// <see cref="AddSignalProtocol"/> (the payload + envelope signing primitives)
    /// and <see cref="AddRouting"/> (next-hop discovery for delivery and relay) to
    /// have been called first; throws <see cref="InvalidOperationException"/> otherwise.
    /// Picks up <c>IAetherNetIncentiveProvider</c> automatically if the host has
    /// registered one — its <c>SettleMeshTipAsync</c> decides what an inbound tip is
    /// worth; a bare node settles nothing.
    /// </summary>
    IAetherNetProtocolBuilder AddMeshTip();

    /// <summary>
    /// Register the SDPKT-settlement tipping layer: <c>ITippingService</c>,
    /// <c>INodeReputationService</c>, <c>ITipperQoSService</c>, <c>TipEventHandler</c>,
    /// <c>IAetherRewardService</c>, the typed <c>IAetherApiClient</c> backend bridge,
    /// and the default in-memory tip/reward stores. Also registers
    /// <c>SdpktMeshTipSettlementProvider</c> as the <c>IAetherNetIncentiveProvider</c>
    /// so an inbound mesh tip (<c>PacketType.TipPacket</c>) settled through
    /// <c>IMeshTipService.HandleTipPacketAsync</c> → <c>SettleMeshTipAsync</c> is forwarded
    /// to the backend for SDPKT-wallet settlement. The host must supply an
    /// <c>ILocalNodeProvider</c> (the local node's UHID) and configure the
    /// <c>"AetherApi"</c> named <c>HttpClient</c> with the backend base address.
    ///
    /// <para>
    /// This is a universal wallet-client capability — any node with an SDPKT wallet can
    /// use it. Durable hosts register their own <c>IAetherTipStore</c> /
    /// <c>IAetherRewardStore</c> before this call to override the in-memory defaults.
    /// </para>
    /// </summary>
    IAetherNetProtocolBuilder AddTipping();

    // ── Media layer ───────────────────────────────────────────────────────────

    /// <summary>
    /// Register <c>StreamingService</c> as a singleton <c>IStreamingService</c>.
    /// Requires <see cref="AddRouting"/>. Picks up <c>IAetherNetIncentiveProvider</c>
    /// automatically if registered by the host (e.g. SDPKT).
    /// </summary>
    IAetherNetProtocolBuilder AddStreaming();

    /// <summary>
    /// Register <c>WatchTogetherService</c> as a singleton <c>IWatchTogetherService</c>.
    /// Requires <see cref="AddRouting"/>. Picks up <c>IAetherNetIncentiveProvider</c>
    /// automatically if registered (ChipIn requires a concrete implementation).
    /// </summary>
    IAetherNetProtocolBuilder AddWatchTogether();

    /// <summary>
    /// Register <c>VideoCallService</c> as a singleton <c>IVideoCallService</c>.
    /// Requires <see cref="AddRouting"/>.
    /// </summary>
    IAetherNetProtocolBuilder AddVideoCall();

    /// <summary>
    /// Register <c>GroupVideoService</c> as a singleton <c>IGroupVideoService</c>.
    /// Requires <see cref="AddRouting"/>.
    /// </summary>
    IAetherNetProtocolBuilder AddGroupVideo();

    /// <summary>
    /// Register <c>VoiceCallService</c> as a singleton <c>IVoiceCallService</c>.
    /// Requires <see cref="AddRouting"/>.
    /// </summary>
    IAetherNetProtocolBuilder AddVoice();

    /// <summary>
    /// Register <c>GroupVoiceCallService</c> as a singleton <c>IGroupVoiceCallService</c>.
    /// Requires <see cref="AddRouting"/>. Picks up <c>IGroupKeyProvider</c> automatically
    /// if registered by the host.
    /// </summary>
    IAetherNetProtocolBuilder AddGroupVoice();

    /// <summary>
    /// Register <c>ContentService</c> as a singleton <c>IContentService</c>.
    /// Requires <see cref="AddRouting"/>. Uses <c>InMemoryContentStore</c> by default
    /// unless the host has registered an <c>IContentStore</c> beforehand.
    /// Picks up <c>IAetherNetIncentiveProvider</c> automatically if registered.
    /// </summary>
    IAetherNetProtocolBuilder AddContent();

    /// <summary>
    /// Register <c>DirectoryService</c> as a singleton <c>IDirectoryService</c>.
    /// Requires <see cref="AddRouting"/>. The directory provides application-layer
    /// name → <c>ContentDescriptor</c> resolution (broadcast publish, query-by-name)
    /// so mesh-first fetchers can discover content by name without prior knowledge of
    /// its root hash. Added in v1.2.0 — closes Issue #60.
    /// </summary>
    IAetherNetProtocolBuilder AddDirectory();

    // ── Extensibility ─────────────────────────────────────────────────────────

    /// <summary>
    /// Register <typeparamref name="T"/> as a singleton <see cref="IAetherNetTelemetryObserver"/>.
    /// Multiple observers may be registered; the <see cref="AetherNetTelemetryBus"/> singleton
    /// resolves all of them and fans out every publish call. Calling this does NOT replace
    /// other already-registered observers — use multiple calls to build up the subscriber set.
    /// </summary>
    IAetherNetProtocolBuilder AddTelemetry<T>() where T : class, IAetherNetTelemetryObserver;

    /// <summary>
    /// Register an existing <see cref="IAetherNetTelemetryObserver"/> instance. The instance
    /// is added to the bus alongside any type-based registrations. Idempotent — if the
    /// same instance reference is passed twice only one registration is created.
    /// </summary>
    IAetherNetProtocolBuilder AddTelemetry(IAetherNetTelemetryObserver observer);

    /// <summary>
    /// Replace the default <see cref="NullAetherNetAiProvider"/> with <typeparamref name="T"/>.
    /// Used by CircleAI and BhenguAI host packages to wire their provider into Aether's
    /// route-suggestion, transport-biasing, and threat-assessment hooks.
    /// </summary>
    IAetherNetProtocolBuilder AddCircleAI<T>() where T : class, IAetherNetAiProvider;

    /// <summary>
    /// Replace the default <see cref="NullAetherNetAiProvider"/> with an existing instance.
    /// </summary>
    IAetherNetProtocolBuilder AddCircleAI(IAetherNetAiProvider provider);

    /// <summary>
    /// Replace the default <see cref="NullBiometricProvider"/> with <typeparamref name="T"/>.
    /// Used by SDPKT and mobile-biometric host packages to gate sensitive mesh operations.
    /// </summary>
    IAetherNetProtocolBuilder AddBiometrics<T>() where T : class, IBiometricProvider;

    /// <summary>
    /// Replace the default <see cref="NullBiometricProvider"/> with an existing instance.
    /// </summary>
    IAetherNetProtocolBuilder AddBiometrics(IBiometricProvider provider);

    /// <summary>
    /// Replace the default <see cref="NullAetherNetContextMemory"/> with <typeparamref name="T"/>.
    /// Used by CircleAI / mempalace to give the AI layer durable semantic memory over mesh
    /// route and behaviour history.
    /// </summary>
    IAetherNetProtocolBuilder AddContextMemory<T>() where T : class, IAetherNetContextMemory;

    /// <summary>
    /// Replace the default <see cref="NullAetherNetContextMemory"/> with an existing instance.
    /// </summary>
    IAetherNetProtocolBuilder AddContextMemory(IAetherNetContextMemory memory);

    /// <summary>
    /// Replace the default <see cref="NullAetherNetSecurityAudit"/> with <typeparamref name="T"/>.
    /// Used by Claude-BugHunter and security monitoring packages to perform static and
    /// runtime vulnerability scanning over mesh packets and node behaviour.
    /// </summary>
    IAetherNetProtocolBuilder AddSecurityAudit<T>() where T : class, IAetherNetSecurityAudit;

    /// <summary>
    /// Replace the default <see cref="NullAetherNetSecurityAudit"/> with an existing instance.
    /// </summary>
    IAetherNetProtocolBuilder AddSecurityAudit(IAetherNetSecurityAudit auditor);

    // ── Phase-2 Extensions ────────────────────────────────────────────────────

    /// <summary>
    /// Register <c>InMemorySpaceService</c> as a singleton <c>ISpaceService</c>
    /// (aether-space Phase-2 extension — geo-pinned community noticeboards).
    /// Requires <see cref="AddContent"/> and <see cref="AddDtn"/> to have been
    /// called first; throws <see cref="InvalidOperationException"/> otherwise.
    /// </summary>
    IAetherNetProtocolBuilder AddSpace();

    /// <summary>
    /// Register <c>InMemoryForgeService</c> as a singleton <c>IForgeService</c>
    /// (aether-forge Phase-2 extension — mesh-native package cache proxy).
    /// Requires <see cref="AddContent"/> to have been called first.
    /// </summary>
    IAetherNetProtocolBuilder AddForge();

    /// <summary>
    /// Register <c>InMemoryVaultService</c> as a singleton <c>IVaultService</c>
    /// (aether-vault Phase-2 extension — erasure-coded distributed backup, k=10 m=4).
    /// Requires <see cref="AddContent"/> to have been called first.
    /// </summary>
    IAetherNetProtocolBuilder AddVault();

    /// <summary>
    /// Register <c>InMemoryPoVService</c>, <c>PoVTokenExchangeService</c> (the on-mesh
    /// <c>PoVTokenExchange</c> = 43 handler) and <c>InMemoryMarketService</c> as singletons
    /// (aether-market Phase-2 extension — offline P2P commerce with PoV anti-Sybil trust). PoV tokens are
    /// signed and verified with real Ed25519 via the node identity key.
    /// Requires <see cref="AddSpace"/>, <see cref="AddVault"/> and <see cref="AddSignalProtocol"/> to
    /// have been called first; throws <see cref="InvalidOperationException"/> otherwise.
    /// </summary>
    IAetherNetProtocolBuilder AddMarket();
}

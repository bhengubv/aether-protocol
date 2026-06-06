// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;

namespace AetherMesh.Diagnostics;

/// <summary>
/// OpenTelemetry-compatible instrumentation surface for the Aether
/// reference protocol. Built on the BCL primitives only —
/// <see cref="System.Diagnostics.Metrics.Meter"/> and
/// <see cref="System.Diagnostics.ActivitySource"/> — so the libraries
/// have ZERO dependency on the OpenTelemetry SDK or any specific
/// exporter. Hosts that opt-in to OTel can simply do:
/// <code>
///   meterProviderBuilder.AddMeter(AetherMeshTelemetry.MeterName);
///   tracerProviderBuilder.AddSource(AetherMeshTelemetry.ActivitySourceName);
/// </code>
/// and immediately get visibility into every hot-path counter and
/// distributed trace published below.
///
/// <para>
/// Cost when no listener is attached: a <see cref="Counter{T}.Add(T)"/>
/// with no <c>MeterListener</c> subscribed degenerates to a single
/// volatile read + branch (effectively free), and
/// <see cref="ActivitySource.StartActivity(string, ActivityKind)"/>
/// returns <c>null</c> without allocating an <see cref="Activity"/>.
/// The hot paths therefore never allocate when telemetry is unobserved.
/// </para>
///
/// <para>
/// PII safety: all UHID-typed activity tags MUST be passed through
/// <c>AetherMesh.Security.Services.LogSanitizer.SanitizeUhid</c> before
/// being attached. The instruments and sources are public so callers
/// outside the assembly (test hosts, observability infra) can subscribe.
/// </para>
/// </summary>
public static class AetherMeshTelemetry
{
    /// <summary>
    /// Stable meter name used by every counter and histogram in this
    /// class. Hosts call <c>AddMeter("AetherMesh.Protocol")</c> on their
    /// OpenTelemetry MeterProvider to subscribe.
    /// </summary>
    public const string MeterName = "AetherMesh.Protocol";

    /// <summary>
    /// Semver-formatted meter version. Bumped only when the set of
    /// emitted instruments changes in a backward-incompatible way.
    /// </summary>
    public const string MeterVersion = "1.0.0";

    /// <summary>
    /// Stable activity source name. Hosts call
    /// <c>AddSource("AetherMesh.Protocol")</c> on their OpenTelemetry
    /// TracerProvider to subscribe.
    /// </summary>
    public const string ActivitySourceName = "AetherMesh.Protocol";

    /// <summary>
    /// Backing <see cref="Meter"/> for every counter / histogram
    /// declared on this class. Hosts subscribe to it by name; direct
    /// access is exposed for niche scenarios (e.g. enumerating
    /// instruments in tests).
    /// </summary>
    public static readonly Meter Meter = new(MeterName, MeterVersion);

    /// <summary>
    /// Backing <see cref="ActivitySource"/> for every span/activity
    /// started by Aether components. Hosts subscribe to it by name.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, MeterVersion);

    // ------------------------------------------------------------------
    // Crypto counters — Signal Protocol (X3DH + Double Ratchet)
    // ------------------------------------------------------------------

    /// <summary>Number of successful Signal-Protocol encrypt operations.</summary>
    public static readonly Counter<long> MessagesEncrypted = Meter.CreateCounter<long>(
        "aethermesh.messages.encrypted",
        unit: "{operation}",
        description: "Signal-Protocol encrypt operations (one per outbound application payload).");

    /// <summary>Number of successful Signal-Protocol decrypt operations.</summary>
    public static readonly Counter<long> MessagesDecrypted = Meter.CreateCounter<long>(
        "aethermesh.messages.decrypted",
        unit: "{operation}",
        description: "Signal-Protocol decrypt operations (one per inbound application payload).");

    /// <summary>Number of successful packet-signature validations.</summary>
    public static readonly Counter<long> SignaturesValidated = Meter.CreateCounter<long>(
        "aethermesh.signatures.validated",
        unit: "{operation}",
        description: "Packet signatures successfully validated (Ed25519 verify returned true).");

    /// <summary>Number of packets rejected at signature-verify time.</summary>
    public static readonly Counter<long> SignaturesRejected = Meter.CreateCounter<long>(
        "aethermesh.signatures.rejected",
        unit: "{operation}",
        description: "Packet signatures rejected: invalid Ed25519 signature.");

    /// <summary>Number of packets rejected because their nonce was already seen.</summary>
    public static readonly Counter<long> NoncesReplayed = Meter.CreateCounter<long>(
        "aethermesh.nonces.replayed",
        unit: "{operation}",
        description: "Packets rejected for nonce-replay (duplicate (source, nonce) pair within freshness window).");

    /// <summary>Number of packets rejected for stale or future timestamps.</summary>
    public static readonly Counter<long> StaleTimestampsRejected = Meter.CreateCounter<long>(
        "aethermesh.timestamps.stale",
        unit: "{operation}",
        description: "Packets rejected: timestamp outside the freshness window.");

    /// <summary>Number of Signal sessions established (initiator + responder X3DH).</summary>
    public static readonly Counter<long> SessionsEstablished = Meter.CreateCounter<long>(
        "aethermesh.sessions.established",
        unit: "{session}",
        description: "Signal-Protocol sessions established via X3DH (initiator and responder roles combined).");

    /// <summary>Number of Double-Ratchet DH-ratchet steps performed (receive side).</summary>
    public static readonly Counter<long> DhRatchetSteps = Meter.CreateCounter<long>(
        "aethermesh.ratchet.dh_steps",
        unit: "{step}",
        description: "Double-Ratchet DH-ratchet steps performed on receive (Signal §5.2).");

    // ------------------------------------------------------------------
    // Routing counters — AODV-inspired reactive routing
    // ------------------------------------------------------------------

    /// <summary>Number of RREQs originated by this node (FindRouteAsync flood).</summary>
    public static readonly Counter<long> RouteRequestsEmitted = Meter.CreateCounter<long>(
        "aethermesh.route.requests_emitted",
        unit: "{packet}",
        description: "Route Request packets originated by this node (FindRouteAsync triggered a flood).");

    /// <summary>Number of RREPs received that completed a pending discovery.</summary>
    public static readonly Counter<long> RouteRepliesReceived = Meter.CreateCounter<long>(
        "aethermesh.route.replies_received",
        unit: "{packet}",
        description: "Route Reply packets received that installed a forward route.");

    /// <summary>Number of route-cache lookups satisfied without a flood.</summary>
    public static readonly Counter<long> RouteCacheHits = Meter.CreateCounter<long>(
        "aethermesh.route.cache_hits",
        unit: "{lookup}",
        description: "FindRouteAsync calls satisfied by the in-memory or persisted route cache.");

    // ------------------------------------------------------------------
    // DTN counters — store-and-forward delay-tolerant networking
    // ------------------------------------------------------------------

    /// <summary>Number of bundles accepted into local custody.</summary>
    public static readonly Counter<long> DtnBundlesAccepted = Meter.CreateCounter<long>(
        "aethermesh.dtn.bundles_accepted",
        unit: "{bundle}",
        description: "DTN bundles accepted into local custody (forwarder role).");

    /// <summary>Number of bundles delivered to their final recipient (locally observed).</summary>
    public static readonly Counter<long> DtnBundlesDelivered = Meter.CreateCounter<long>(
        "aethermesh.dtn.bundles_delivered",
        unit: "{bundle}",
        description: "DTN bundles delivered to their final recipient (local node was the recipient or sender saw a receipt).");

    /// <summary>Number of bundles that expired before delivery.</summary>
    public static readonly Counter<long> DtnBundlesExpired = Meter.CreateCounter<long>(
        "aethermesh.dtn.bundles_expired",
        unit: "{bundle}",
        description: "DTN bundles whose TTL elapsed before delivery (ExpireStaleAsync).");

    // ------------------------------------------------------------------
    // SOS counters
    // ------------------------------------------------------------------

    /// <summary>Number of SOS broadcasts originated by this node.</summary>
    public static readonly Counter<long> SosBroadcasts = Meter.CreateCounter<long>(
        "aethermesh.sos.broadcasts",
        unit: "{broadcast}",
        description: "SOS broadcasts originated by this node (BroadcastSosAsync accepted by rate limiter).");

    /// <summary>Number of SOS rebroadcasts suppressed by the dedup cache.</summary>
    public static readonly Counter<long> SosRebroadcastsSuppressed = Meter.CreateCounter<long>(
        "aethermesh.sos.rebroadcasts_suppressed",
        unit: "{packet}",
        description: "Incoming SOS packets suppressed because the (broadcastId) was already seen.");

    // ------------------------------------------------------------------
    // Messaging counters — application-layer send pipeline
    // ------------------------------------------------------------------

    /// <summary>Number of application messages successfully sent (mesh/DTN/backend).</summary>
    public static readonly Counter<long> MessagingMessagesSent = Meter.CreateCounter<long>(
        "aethermesh.messaging.messages_sent",
        unit: "{message}",
        description: "Application messages whose ciphertext exited the local node (mesh / DTN / backend).");

    /// <summary>Number of messages queued because no Signal session existed yet.</summary>
    public static readonly Counter<long> MessagingMessagesQueued = Meter.CreateCounter<long>(
        "aethermesh.messaging.messages_queued",
        unit: "{message}",
        description: "Application messages queued in the outbox because no Signal session was established with the recipient.");

    /// <summary>Number of messages that fell back to DTN store-and-forward.</summary>
    public static readonly Counter<long> MessagingDtnFallback = Meter.CreateCounter<long>(
        "aethermesh.messaging.dtn_fallback",
        unit: "{message}",
        description: "Application messages handed off to DTN after no mesh route was available.");

    // ------------------------------------------------------------------
    // Latency histograms (milliseconds)
    // ------------------------------------------------------------------

    /// <summary>End-to-end latency of <c>EncryptAsync</c>.</summary>
    public static readonly Histogram<double> EncryptLatency = Meter.CreateHistogram<double>(
        "aethermesh.encrypt.latency",
        unit: "ms",
        description: "Wall-clock latency of Signal-Protocol EncryptAsync (chain-key advance + AES-GCM encrypt).");

    /// <summary>End-to-end latency of <c>DecryptAsync</c>.</summary>
    public static readonly Histogram<double> DecryptLatency = Meter.CreateHistogram<double>(
        "aethermesh.decrypt.latency",
        unit: "ms",
        description: "Wall-clock latency of Signal-Protocol DecryptAsync (DH-ratchet step if needed + AES-GCM decrypt).");

    /// <summary>End-to-end latency of <c>FindRouteAsync</c> (cache + discovery).</summary>
    public static readonly Histogram<double> RouteLookupLatency = Meter.CreateHistogram<double>(
        "aethermesh.route.lookup_latency",
        unit: "ms",
        description: "Wall-clock latency of FindRouteAsync (cache hit OR full RREQ/RREP discovery round trip).");

    /// <summary>End-to-end latency of packet sign / verify.</summary>
    public static readonly Histogram<double> SignVerifyLatency = Meter.CreateHistogram<double>(
        "aethermesh.sign.verify_latency",
        unit: "ms",
        description: "Wall-clock latency of SignPacketAsync or VerifyPacketAsync (Ed25519 + canonical-data build).");

    /// <summary>
    /// Sanitizes a UHID for safe attachment as an activity tag. Mirrors the
    /// scheme used by <c>AetherMesh.Security.Services.LogSanitizer.SanitizeUhid</c>:
    /// first 4 chars + "..." + 4 chars of a SHA-256 hash salted by the UTC
    /// date — correlatable within a day, opaque across days, never the full
    /// identifier.
    ///
    /// <para>
    /// Duplicated here (rather than referenced) because AetherMesh.Core sits
    /// underneath AetherMesh.Security in the dependency graph. The duplication is
    /// load-bearing: routing/DTN/SOS sit in Core but still need to obey the
    /// PII contract.
    /// </para>
    /// </summary>
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

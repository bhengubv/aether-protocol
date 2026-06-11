// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text.Json;
using AetherNet.Constants;
using AetherNet.Extensibility;
using AetherNet.Extensibility.Events;
using AetherNet.Protocol;
using AetherNet.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Handshake;

/// <summary>
/// Default <see cref="IHandshakeService"/> implementation. Tracks the peers
/// we've Hello'd, the peers we've finished negotiating with, and emits
/// events on completion / incompatibility.
///
/// <para>
/// Wire flow:
/// </para>
/// <code>
/// A → B   Hello       { min:1, max:2, caps:[X,Y,Z], impl:"…" }
/// A ← B   HelloAck    { min:1, max:2, caps:[X,Y],   impl:"…" }
/// </code>
///
/// <para>
/// Negotiation rules:
/// </para>
/// <list type="bullet">
///   <item>Negotiated version = <c>min(ourMax, theirMax)</c>.</item>
///   <item>If <c>min(ourMax,theirMax) &lt; max(ourMin,theirMin)</c> the ranges
///   do not overlap → fire <see cref="IncompatiblePeer"/>, refuse to lock in.</item>
///   <item>Locked-in capability set = <c>ourCaps ∩ theirCaps</c>.</item>
/// </list>
/// </summary>
public sealed class HandshakeService : IHandshakeService
{
    /// <summary>Default capability tags advertised by this implementation.</summary>
    public static readonly IReadOnlySet<string> DefaultCapabilities = new HashSet<string>(StringComparer.Ordinal)
    {
        "signal-x3dh",
        "double-ratchet",
        "dtn-custody",
        "sos",
        "voice",
        "stream",
    };

    /// <summary>Default implementation banner emitted in our Hello/HelloAck.</summary>
    public const string DefaultImplementation = "aether/2";

    private readonly IMeshSender _sender;
    private readonly ILogger<HandshakeService> _logger;
    private readonly byte _ourMinVersion;
    private readonly byte _ourMaxVersion;
    private readonly IReadOnlySet<string> _ourCapabilities;
    private readonly string _ourImplementation;
    private readonly IAetherNetTelemetry? _telemetry;
    private readonly IBiometricProvider _biometricProvider;

    // Peers we've already sent a Hello to, to suppress duplicate sends.
    private readonly ConcurrentDictionary<string, byte> _helloSent = new(StringComparer.Ordinal);

    // Peers we've finished negotiating with.
    private readonly ConcurrentDictionary<string, PeerCapabilities> _negotiated = new(StringComparer.Ordinal);

    public event EventHandler<PeerCapabilities>? PeerNegotiated;
    public event EventHandler<IncompatiblePeerEventArgs>? IncompatiblePeer;

    /// <summary>
    /// Construct a handshake service. Defaults match this codebase: we speak
    /// versions 1..<see cref="ProtocolConstants.CurrentProtocolVersion"/> and
    /// advertise <see cref="DefaultCapabilities"/>.
    /// </summary>
    public HandshakeService(
        IMeshSender sender,
        ILogger<HandshakeService>? logger = null,
        byte? ourMinVersion = null,
        byte? ourMaxVersion = null,
        IReadOnlySet<string>? ourCapabilities = null,
        string? ourImplementation = null,
        IAetherNetTelemetry? telemetry = null,
        IBiometricProvider? biometricProvider = null)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _logger = logger ?? NullLogger<HandshakeService>.Instance;
        _ourMinVersion = ourMinVersion ?? 1;
        _ourMaxVersion = ourMaxVersion ?? ProtocolConstants.CurrentProtocolVersion;
        if (_ourMinVersion > _ourMaxVersion)
            throw new ArgumentException(
                $"ourMinVersion ({_ourMinVersion}) cannot exceed ourMaxVersion ({_ourMaxVersion}).",
                nameof(ourMinVersion));
        _ourCapabilities = ourCapabilities ?? DefaultCapabilities;
        _ourImplementation = ourImplementation ?? DefaultImplementation;
        _telemetry = telemetry;
        _biometricProvider = biometricProvider ?? NullBiometricProvider.Instance;
    }

    public async Task InitiateAsync(string peerUhid, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(peerUhid);
        if (string.Equals(peerUhid, _sender.LocalUhid, StringComparison.Ordinal)) return;

        // Suppress duplicate Hellos.
        if (!_helloSent.TryAdd(peerUhid, 0))
            return;

        var hello = BuildPacket(PacketType.Hello, peerUhid);
        var delivered = await _sender.SendAsync(hello, peerUhid, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Hello sent to {Peer} delivered={Delivered}", peerUhid, delivered);
    }

    public async Task HandleHelloAsync(MeshPacket helloPacket, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(helloPacket);
        if (helloPacket.Type != PacketType.Hello)
            throw new ArgumentException($"Expected Hello, got {helloPacket.Type}", nameof(helloPacket));

        if (string.IsNullOrEmpty(helloPacket.SourceUhid)) return;
        if (string.Equals(helloPacket.SourceUhid, _sender.LocalUhid, StringComparison.Ordinal)) return;

        var theirs = TryDeserialize(helloPacket);
        if (theirs is null)
        {
            _logger.LogWarning("Hello from {Peer} has malformed payload — ignoring",
                helloPacket.SourceUhid);
            return;
        }

        if (!TryNegotiate(helloPacket.SourceUhid, theirs, out var negotiated))
            return; // IncompatiblePeer already fired by TryNegotiate

        _negotiated[helloPacket.SourceUhid] = negotiated;
        PeerNegotiated?.Invoke(this, negotiated);
        _telemetry?.Publish(new AetherNetNodeEvent(
            helloPacket.SourceUhid,
            AetherNetNodeEventKind.Joined,
            new AetherNetNodeHealth(TrustScore: 1.0, IsReachable: true,
                Latency: TimeSpan.Zero, HopCount: 1),
            DateTimeOffset.UtcNow));
        _logger.LogInformation(
            "Hello accepted from {Peer} → version={Ver} caps=[{Caps}] impl={Impl}",
            helloPacket.SourceUhid,
            negotiated.NegotiatedVersion,
            string.Join(",", negotiated.Capabilities),
            negotiated.ImplementationVersion);

        // Reply with HelloAck — even if we already sent them an unprompted Hello,
        // the spec is symmetric and the ack carries our own range/caps.
        var ack = BuildPacket(PacketType.HelloAck, helloPacket.SourceUhid);
        var delivered = await _sender.SendAsync(ack, helloPacket.SourceUhid, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("HelloAck sent to {Peer} delivered={Delivered}",
            helloPacket.SourceUhid, delivered);
    }

    public Task HandleHelloAckAsync(MeshPacket helloAckPacket, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(helloAckPacket);
        if (helloAckPacket.Type != PacketType.HelloAck)
            throw new ArgumentException($"Expected HelloAck, got {helloAckPacket.Type}", nameof(helloAckPacket));

        if (string.IsNullOrEmpty(helloAckPacket.SourceUhid)) return Task.CompletedTask;
        if (string.Equals(helloAckPacket.SourceUhid, _sender.LocalUhid, StringComparison.Ordinal))
            return Task.CompletedTask;

        var theirs = TryDeserialize(helloAckPacket);
        if (theirs is null)
        {
            _logger.LogWarning("HelloAck from {Peer} has malformed payload — ignoring",
                helloAckPacket.SourceUhid);
            return Task.CompletedTask;
        }

        if (!TryNegotiate(helloAckPacket.SourceUhid, theirs, out var negotiated))
            return Task.CompletedTask; // IncompatiblePeer already fired

        _negotiated[helloAckPacket.SourceUhid] = negotiated;
        PeerNegotiated?.Invoke(this, negotiated);
        _telemetry?.Publish(new AetherNetNodeEvent(
            helloAckPacket.SourceUhid,
            AetherNetNodeEventKind.Joined,
            new AetherNetNodeHealth(TrustScore: 1.0, IsReachable: true,
                Latency: TimeSpan.Zero, HopCount: 1),
            DateTimeOffset.UtcNow));
        _logger.LogInformation(
            "HelloAck received from {Peer} → version={Ver} caps=[{Caps}] impl={Impl}",
            helloAckPacket.SourceUhid,
            negotiated.NegotiatedVersion,
            string.Join(",", negotiated.Capabilities),
            negotiated.ImplementationVersion);

        return Task.CompletedTask;
    }

    public Task<PeerCapabilities?> GetPeerCapabilitiesAsync(
        string peerUhid,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(peerUhid);
        return Task.FromResult(_negotiated.TryGetValue(peerUhid, out var caps) ? caps : null);
    }

    public Task RenegotiateAsync(string peerUhid, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(peerUhid);
        _negotiated.TryRemove(peerUhid, out _);
        _helloSent.TryRemove(peerUhid, out _);
        _logger.LogInformation("Cleared cached capabilities for {Peer}; next contact will re-Hello", peerUhid);
        return Task.CompletedTask;
    }

    public IReadOnlyList<PeerCapabilities> GetAllNegotiated()
        => _negotiated.Values.ToArray();

    /// <inheritdoc/>
    public async Task<BiometricVerificationResult> VerifyCoPresenceAsync(
        byte[]            localFaceFrameRgbHwc,
        int               width,
        int               height,
        FaceEmbedding     referenceEmbedding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(referenceEmbedding);

        if (!_biometricProvider.IsAvailable)
        {
            _logger.LogDebug("VerifyCoPresence: biometric provider unavailable — returning Failed");
            return BiometricVerificationResult.Failed;
        }

        // Detect the dominant face in the live frame.
        var faces = await _biometricProvider.DetectAsync(
            localFaceFrameRgbHwc, width, height, maxFaces: 1, cancellationToken)
            .ConfigureAwait(false);

        if (faces.Count == 0)
        {
            _logger.LogDebug("VerifyCoPresence: no face detected in live frame — returning Failed");
            return BiometricVerificationResult.Failed;
        }

        var detected = faces[0];
        if (!detected.IsConfident)
        {
            _logger.LogDebug(
                "VerifyCoPresence: detection confidence {Score:F2} below threshold 0.50 — returning Failed",
                detected.DetectionScore);
            return BiometricVerificationResult.Failed;
        }

        // Compare the detected face to the peer's reference embedding.
        var verifyResult = await _biometricProvider.VerifyAsync(
            referenceEmbedding, detected.Embedding, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        _logger.LogDebug(
            "VerifyCoPresence: verified={Verified} similarity={Similarity:F3}",
            verifyResult.Verified, verifyResult.Similarity);

        return verifyResult;
    }

    /// <summary>
    /// Backward-compat: install a "v1, no caps" record for a peer that never
    /// replied to our Hello within the timeout window. Hosts call this from
    /// their own timer / heartbeat loop. Idempotent — if the peer has since
    /// replied with a HelloAck, the existing record wins.
    /// </summary>
    public void AssumeLegacyV1(string peerUhid)
    {
        ArgumentException.ThrowIfNullOrEmpty(peerUhid);
        if (string.Equals(peerUhid, _sender.LocalUhid, StringComparison.Ordinal)) return;

        var fallback = new PeerCapabilities(
            peerUhid,
            NegotiatedVersion: 1,
            Capabilities: new HashSet<string>(StringComparer.Ordinal),
            ImplementationVersion: string.Empty,
            NegotiatedAt: DateTimeOffset.UtcNow);

        var added = _negotiated.GetOrAdd(peerUhid, fallback);
        if (ReferenceEquals(added, fallback))
        {
            PeerNegotiated?.Invoke(this, fallback);
            _logger.LogWarning(
                "No HelloAck from {Peer} after timeout — assuming protocol v1 / no advertised capabilities",
                peerUhid);
        }
    }

    private MeshPacket BuildPacket(PacketType type, string destinationUhid)
    {
        var payload = new HelloPayload
        {
            MinVersion = _ourMinVersion,
            MaxVersion = _ourMaxVersion,
            Capabilities = _ourCapabilities.ToList(),
            Implementation = _ourImplementation,
        };

        return new MeshPacket
        {
            Type = type,
            SourceUhid = _sender.LocalUhid,
            DestinationUhid = destinationUhid,
            Ttl = 1, // direct hop only — handshake never relays
            Priority = 0,
            ProtocolVersion = _ourMaxVersion,
            Payload = JsonSerializer.SerializeToUtf8Bytes(payload, HelloPayloadJson.Options),
        };
    }

    private HelloPayload? TryDeserialize(MeshPacket packet)
    {
        if (packet.Payload is null || packet.Payload.Length == 0) return null;
        try
        {
            return JsonSerializer.Deserialize<HelloPayload>(packet.Payload, HelloPayloadJson.Options);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Handshake payload from {Peer} could not be parsed", packet.SourceUhid);
            return null;
        }
    }

    private bool TryNegotiate(string peerUhid, HelloPayload theirs, out PeerCapabilities negotiated)
    {
        negotiated = null!;

        if (theirs.MinVersion > theirs.MaxVersion)
        {
            _logger.LogWarning(
                "Handshake from {Peer} announces inverted range min={Min} > max={Max} — refusing",
                peerUhid, theirs.MinVersion, theirs.MaxVersion);
            FireIncompatible(peerUhid, theirs, "inverted version range");
            return false;
        }

        // Overlap check: highest min must be ≤ lowest max.
        var overlapMin = Math.Max(_ourMinVersion, theirs.MinVersion);
        var overlapMax = Math.Min(_ourMaxVersion, theirs.MaxVersion);
        if (overlapMin > overlapMax)
        {
            FireIncompatible(peerUhid, theirs,
                $"no version overlap (ours={_ourMinVersion}..{_ourMaxVersion}, theirs={theirs.MinVersion}..{theirs.MaxVersion})");
            return false;
        }

        // Pick the highest mutually-supported version.
        var chosenVersion = (byte)overlapMax;

        // Capability intersection (case-sensitive, ordinal — capability names
        // are wire constants, not human strings).
        var theirCaps = theirs.Capabilities ?? new List<string>();
        var intersection = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cap in theirCaps)
        {
            if (!string.IsNullOrEmpty(cap) && _ourCapabilities.Contains(cap))
                intersection.Add(cap);
        }

        negotiated = new PeerCapabilities(
            peerUhid,
            chosenVersion,
            intersection,
            theirs.Implementation ?? string.Empty,
            DateTimeOffset.UtcNow);
        return true;
    }

    private void FireIncompatible(string peerUhid, HelloPayload theirs, string reason)
    {
        _logger.LogWarning(
            "Incompatible peer {Peer}: {Reason}",
            peerUhid, reason);
        IncompatiblePeer?.Invoke(this,
            new IncompatiblePeerEventArgs(
                peerUhid,
                theirs.MinVersion,
                theirs.MaxVersion,
                _ourMinVersion,
                _ourMaxVersion,
                reason));
    }
}

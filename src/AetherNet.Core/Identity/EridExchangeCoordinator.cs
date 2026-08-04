// SPDX-License-Identifier: MIT

using AetherNet.Privacy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Identity;

/// <summary>
/// Composes the two halves of the ERID exchange that otherwise ship built-but-unconnected: the transport
/// (<see cref="IEridAnnounceService"/>, packet type 56) and the crypto/directory. On an established
/// session it frames this node's secret routing key (<see cref="EridAnnouncementCodec"/>), seals it
/// through the <see cref="IControlPayloadCipher"/> (the same Signal session used for messages), and sends
/// it; on an inbound announcement it opens the payload and records the peer's routing key in the
/// <see cref="EridDirectory"/> — so an established relationship can resolve that peer's rotating wire ERID
/// while an outsider cannot.
///
/// <para>Additive and off-wire: this populates the in-memory directory only. The stable UHID still ships
/// on the wire until the (capability-gated) cutover — the coordinator changes no serialized bytes itself.</para>
/// </summary>
public sealed class EridExchangeCoordinator : IDisposable
{
    private readonly IEridAnnounceService _announce;
    private readonly EridDirectory _directory;
    private readonly IControlPayloadCipher _cipher;
    private readonly byte[] _myRoutingKey;
    private readonly int _epochSeconds;
    private readonly int _eridLength;
    private readonly ILogger _logger;

    public EridExchangeCoordinator(
        IEridAnnounceService announce,
        EridDirectory directory,
        IControlPayloadCipher cipher,
        byte[] myRoutingKey,
        int epochSeconds = EphemeralRoutingId.DefaultEpochSeconds,
        int eridLength = EphemeralRoutingId.DefaultLength,
        ILogger<EridExchangeCoordinator>? logger = null)
    {
        _announce = announce ?? throw new ArgumentNullException(nameof(announce));
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _cipher = cipher ?? throw new ArgumentNullException(nameof(cipher));
        ArgumentNullException.ThrowIfNull(myRoutingKey);
        if (myRoutingKey.Length == 0)
            throw new ArgumentException("myRoutingKey cannot be empty.", nameof(myRoutingKey));
        _myRoutingKey = (byte[])myRoutingKey.Clone();
        _epochSeconds = epochSeconds;
        _eridLength = eridLength;
        _logger = logger ?? NullLogger<EridExchangeCoordinator>.Instance;

        // Close the inbound half of the gap: type-56 packets surfaced by the transport now flow through.
        _announce.AnnounceReceived += OnAnnounceReceived;
    }

    /// <summary>
    /// Seal this node's routing key to <paramref name="peerUhid"/> and send it. Returns false (nothing
    /// sent) when there is no session — the routing key never leaves except inside the session. A host
    /// calls this on session establishment (e.g. from <c>HandshakeService.PeerNegotiated</c> once the
    /// <c>erid-routing</c> capability is advertised at the cutover).
    /// </summary>
    public async Task<bool> AnnounceToAsync(string peerUhid, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(peerUhid);
        if (!_cipher.HasSession(peerUhid))
        {
            _logger.LogDebug("ERID announce to {Peer} skipped: no session", peerUhid);
            return false;
        }

        var frame = EridAnnouncementCodec.Encode(_myRoutingKey, _epochSeconds, _eridLength);
        var sealedFrame = await _cipher.EncryptAsync(peerUhid, frame, cancellationToken).ConfigureAwait(false);
        if (sealedFrame is null)
        {
            _logger.LogDebug("ERID announce to {Peer} skipped: session not ready", peerUhid);
            return false;
        }

        return await _announce.SendAnnounceAsync(peerUhid, sealedFrame, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Open an inbound (sealed) announcement from <paramref name="fromUhid"/> and, if it is a well-formed
    /// ERID announcement, record the peer's routing key. Returns true when a routing key was recorded.
    /// </summary>
    public async Task<bool> ProcessInboundAsync(string fromUhid, byte[] sealedAnnouncement, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(fromUhid) || sealedAnnouncement is null || sealedAnnouncement.Length == 0)
            return false;

        var plaintext = await _cipher.DecryptAsync(fromUhid, sealedAnnouncement, cancellationToken).ConfigureAwait(false);
        if (plaintext is null)
            return false;

        if (!EridAnnouncementCodec.TryDecode(plaintext, out var routingKey, out _, out _))
            return false;

        _directory.RememberPeer(fromUhid, routingKey);
        _logger.LogDebug("ERID directory learned peer {Peer} ({Count} known)", fromUhid, _directory.KnownPeerCount);
        return true;
    }

    private async void OnAnnounceReceived(object? sender, EridAnnounceReceived e)
    {
        try
        {
            await ProcessInboundAsync(e.FromUhid, e.EncryptedAnnouncement).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ERID inbound announcement from {Peer} failed to process", e.FromUhid);
        }
    }

    public void Dispose() => _announce.AnnounceReceived -= OnAnnounceReceived;
}

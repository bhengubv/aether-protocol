// SPDX-License-Identifier: MIT

using AetherNet.Constants;
using AetherNet.Protocol;
using AetherNet.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Identity;

/// <summary>Event args: an inbound ERID announcement (still Signal-encrypted) plus the peer that sent it.</summary>
public sealed class EridAnnounceReceived : EventArgs
{
    /// <summary>The packet body — a Signal <c>EncryptedPayload</c> whose plaintext is an <see cref="EridAnnouncementCodec"/> frame.</summary>
    public byte[] EncryptedAnnouncement { get; init; } = Array.Empty<byte>();

    /// <summary>UHID of the peer that sent the announcement.</summary>
    public string FromUhid { get; init; } = string.Empty;
}

/// <summary>
/// Binds <see cref="PacketType.EridAnnounce"/> (56) to the mesh: a node shares its rotating-address
/// routing key with an established peer by sending the (already Signal-encrypted) announcement directly.
/// Transport only — the plaintext framing (<see cref="EridAnnouncementCodec"/>) and the encryption
/// (ISignalProtocolService) are done by the host/EridExchangeService; this service just carries the
/// opaque encrypted blob as a directed packet and surfaces inbound ones via <see cref="AnnounceReceived"/>.
/// </summary>
public interface IEridAnnounceService
{
    /// <summary>Raised when an ERID announcement arrives from a peer (payload still encrypted).</summary>
    event EventHandler<EridAnnounceReceived>? AnnounceReceived;

    /// <summary>Send an encrypted ERID announcement directly to <paramref name="peerUhid"/>. Returns delivery success.</summary>
    Task<bool> SendAnnounceAsync(string peerUhid, byte[] encryptedAnnouncement, CancellationToken cancellationToken = default);

    /// <summary>Process an inbound <see cref="PacketType.EridAnnounce"/>: raise <see cref="AnnounceReceived"/>. False on wrong type or empty body.</summary>
    Task<bool> HandleAsync(MeshPacket packet, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class EridAnnounceService : IEridAnnounceService
{
    private readonly IMeshSender _sender;
    private readonly ILogger<EridAnnounceService> _logger;

    public event EventHandler<EridAnnounceReceived>? AnnounceReceived;

    public EridAnnounceService(IMeshSender sender, ILogger<EridAnnounceService>? logger = null)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _logger = logger ?? NullLogger<EridAnnounceService>.Instance;
    }

    /// <inheritdoc />
    public async Task<bool> SendAnnounceAsync(string peerUhid, byte[] encryptedAnnouncement, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(peerUhid);
        ArgumentNullException.ThrowIfNull(encryptedAnnouncement);
        if (encryptedAnnouncement.Length == 0)
            throw new ArgumentException("encryptedAnnouncement cannot be empty.", nameof(encryptedAnnouncement));

        var packet = new MeshPacket
        {
            Type = PacketType.EridAnnounce,
            SourceUhid = _sender.LocalUhid,
            DestinationUhid = peerUhid,
            Ttl = ProtocolConstants.DefaultTtl,
            Payload = encryptedAnnouncement,
        };
        var delivered = await _sender.SendAsync(packet, peerUhid, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("ERID announce → {Peer} ({Bytes} B) delivered={Delivered}", peerUhid, encryptedAnnouncement.Length, delivered);
        return delivered;
    }

    /// <inheritdoc />
    public Task<bool> HandleAsync(MeshPacket packet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.Type != PacketType.EridAnnounce)
            return Task.FromResult(false);
        if (packet.Payload is null || packet.Payload.Length == 0)
            return Task.FromResult(false);

        AnnounceReceived?.Invoke(this, new EridAnnounceReceived
        {
            EncryptedAnnouncement = packet.Payload,
            FromUhid = packet.SourceUhid,
        });
        return Task.FromResult(true);
    }
}

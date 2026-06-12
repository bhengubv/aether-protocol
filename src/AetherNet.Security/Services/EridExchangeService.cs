// SPDX-License-Identifier: MIT

using AetherNet.Identity;
using AetherNet.Security.Models;

namespace AetherNet.Security.Services;

/// <summary>
/// Orchestrates the in-session ERID exchange — the live half of the rotating-address handshake.
/// On one side it frames this node's secret routingKey and seals it for a peer we already share a
/// Signal session with; on the other it takes a payload already decrypted out of the session and, if
/// it is an ERID announcement, records the peer's routingKey in the <see cref="EridDirectory"/> so
/// we can resolve that peer's rotating wire address.
///
/// <para>Transport stays the host's concern: the host decides WHEN to announce (e.g. just after a
/// session establishes) and routes decrypted payloads in. This service holds only the ERID-specific
/// seal/encode and decode/remember logic, so the exchange is testable against real crypto without a
/// running dispatch loop.</para>
/// </summary>
public sealed class EridExchangeService
{
    private readonly EridDirectory _directory;
    private readonly ISignalProtocolService _signal;
    private readonly byte[] _myRoutingKey;
    private readonly int _epochSeconds;
    private readonly int _eridLength;

    public EridExchangeService(
        EridDirectory directory,
        ISignalProtocolService signal,
        byte[] myRoutingKey,
        int epochSeconds = EphemeralRoutingId.DefaultEpochSeconds,
        int eridLength = EphemeralRoutingId.DefaultLength)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
        ArgumentNullException.ThrowIfNull(myRoutingKey);
        if (myRoutingKey.Length == 0)
            throw new ArgumentException("myRoutingKey cannot be empty.", nameof(myRoutingKey));
        _myRoutingKey = (byte[])myRoutingKey.Clone();
        _epochSeconds = epochSeconds;
        _eridLength = eridLength;
    }

    /// <summary>
    /// Build the encrypted ERID announcement for <paramref name="peerUhid"/> — our routingKey framed
    /// by <see cref="EridAnnouncementCodec"/> and sealed in the Signal session. Returns null when no
    /// session exists yet (nothing to ride inside); the host transmits the returned payload.
    /// </summary>
    public async Task<EncryptedPayload?> CreateAnnouncementAsync(string peerUhid, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(peerUhid);
        if (!_signal.HasSession(peerUhid)) return null;
        var frame = EridAnnouncementCodec.Encode(_myRoutingKey, _epochSeconds, _eridLength);
        return await _signal.EncryptAsync(peerUhid, frame, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Inspect a payload already DECRYPTED out of the session with <paramref name="peerUhid"/>. If it
    /// is a well-formed ERID announcement, record the peer's routingKey and return true; otherwise
    /// return false (it was some other in-session message — not an error).
    /// </summary>
    public bool TryProcessInbound(string peerUhid, ReadOnlySpan<byte> decryptedPayload)
    {
        if (string.IsNullOrEmpty(peerUhid)) return false;
        if (!EridAnnouncementCodec.TryDecode(decryptedPayload, out var routingKey, out _, out _))
            return false;
        _directory.RememberPeer(peerUhid, routingKey);
        return true;
    }
}

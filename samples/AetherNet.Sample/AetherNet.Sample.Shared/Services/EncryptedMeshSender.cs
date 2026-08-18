// SPDX-License-Identifier: MIT

using AetherNet.Messaging;
using AetherNet.Models;
using AetherNet.Protocol;
using AetherNet.Routing;
using AetherNet.Security.Services;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// Seals a packet's payload before it goes on the air, and refuses to send it if it cannot.
///
/// <para>
/// The voice service was shipping its signalling as plain JSON and its frames as raw bytes. Chat has
/// ridden the Signal ratchet since the mock was removed; a call is at least as private as a message,
/// and anyone within radio range could otherwise reconstruct who rang whom, when, for how long, and —
/// with the codec named helpfully in the offer — what was said.
/// </para>
///
/// <para>
/// This wraps the ordinary sender rather than changing the protocol library, because
/// <see cref="VoiceCallService"/> already takes an <see cref="IMeshSender"/>. The call state machine,
/// the signalling schema and the jitter buffer stay exactly as they are, and everything they hand out
/// goes through here on the way to the radio.
/// </para>
///
/// <para>
/// <b>It fails closed.</b> No session with that peer, or encryption throws, and the packet is dropped
/// rather than sent in clear — a call that does not connect is a far smaller problem than one that
/// quietly broadcasts.
/// </para>
/// </summary>
public sealed class EncryptedMeshSender : IMeshSender
{
    private readonly IMeshSender _inner;
    private readonly ISignalProtocolService _signal;
    private readonly Action<string>? _trace;

    /// <summary>
    /// The per-call media key, once a call has one. Frames use this instead of the ratchet.
    ///
    /// <para>
    /// Signalling stays on the ratchet — it is a handful of messages and wants that protection. Media
    /// cannot: fifty frames a second outrun a ratchet within a second. See <see cref="CallMediaCipher"/>.
    /// </para>
    /// </summary>
    public CallMediaCipher? Media { get; set; }

    public EncryptedMeshSender(IMeshSender inner, ISignalProtocolService signal, Action<string>? trace = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
        _trace = trace;
    }

    public string LocalUhid => _inner.LocalUhid;
    public string? LocalGeohash => _inner.LocalGeohash;
    public IReadOnlyList<PeerInfo> GetConnectedPeers() => _inner.GetConnectedPeers();

    public async Task<bool> SendAsync(MeshPacket packet, string nextHopUhid, CancellationToken cancellationToken = default)
    {
        var sealedPacket = await SealAsync(packet, cancellationToken).ConfigureAwait(false);
        return sealedPacket is not null &&
               await _inner.SendAsync(sealedPacket, nextHopUhid, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> BroadcastAsync(MeshPacket packet, CancellationToken cancellationToken = default)
    {
        var sealedPacket = await SealAsync(packet, cancellationToken).ConfigureAwait(false);
        return sealedPacket is null ? 0 : await _inner.BroadcastAsync(sealedPacket, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Seal the payload for the packet's destination, or return null to drop it.
    ///
    /// <para>
    /// A packet with no destination cannot be sealed — there is nobody to seal it <i>to</i> — so it is
    /// dropped rather than broadcast in clear. Voice is always addressed to one person; that is the
    /// whole shape of a 1:1 call.
    /// </para>
    /// </summary>
    private async Task<MeshPacket?> SealAsync(MeshPacket packet, CancellationToken cancellationToken)
    {
        // Media goes under the call's own key. Doing it here rather than in the caller means every
        // path out — including the ones inside the protocol library — is covered by the same rule.
        if (packet.Type == PacketType.VoiceCall)
            return SealMedia(packet);

        var peer = packet.DestinationUhid;
        if (string.IsNullOrEmpty(peer))
        {
            _trace?.Invoke("voice out DROPPED — no destination to encrypt to");
            return null;
        }

        if (!_signal.HasSession(peer))
        {
            _trace?.Invoke($"voice out DROPPED — no session with {peer}");
            return null;
        }

        try
        {
            var payload = packet.Payload ?? Array.Empty<byte>();
            var sealedPayload = await _signal.EncryptAsync(peer, payload, cancellationToken).ConfigureAwait(false);

            return new MeshPacket
            {
                Id = packet.Id,
                Type = packet.Type,
                SourceUhid = packet.SourceUhid,
                DestinationUhid = packet.DestinationUhid,
                Ttl = packet.Ttl,
                Priority = packet.Priority,
                Payload = EncryptedPayloadCodec.Serialize(sealedPayload),
            };
        }
        catch (Exception ex)
        {
            _trace?.Invoke($"voice out DROPPED — could not encrypt for {peer}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Seal a media frame under the call's key. Dropped if the call has no key yet — a frame in clear
    /// is not a lesser evil than a frame not sent.
    /// </summary>
    private MeshPacket? SealMedia(MeshPacket packet)
    {
        if (Media is null)
        {
            _trace?.Invoke("voice frame DROPPED — the call has no media key");
            return null;
        }

        return new MeshPacket
        {
            Id = packet.Id,
            Type = packet.Type,
            SourceUhid = packet.SourceUhid,
            DestinationUhid = packet.DestinationUhid,
            Ttl = packet.Ttl,
            Priority = packet.Priority,
            Payload = Media.Seal(packet.Payload ?? Array.Empty<byte>()),
        };
    }

    /// <summary>
    /// Open a media frame. Null when it will not open — a lost or tampered frame, either way nothing
    /// to play.
    /// </summary>
    public MeshPacket? OpenMedia(MeshPacket packet)
    {
        if (Media is null || packet.Payload is null) return null;
        if (Media.Open(packet.Payload) is not { } plain) return null;

        return new MeshPacket
        {
            Id = packet.Id,
            Type = packet.Type,
            SourceUhid = packet.SourceUhid,
            DestinationUhid = packet.DestinationUhid,
            Ttl = packet.Ttl,
            Priority = packet.Priority,
            Payload = plain,
        };
    }

    /// <summary>
    /// Open a packet sealed by the other end. Returns null when it will not open, which is the same
    /// answer as "this was not for us" — either way there is nothing to act on.
    /// </summary>
    public static async Task<MeshPacket?> UnsealAsync(
        MeshPacket packet,
        ISignalProtocolService signal,
        string peerTag,
        CancellationToken cancellationToken = default,
        Action<string>? why = null)
    {
        if (packet.Payload is null || packet.Payload.Length == 0) return null;

        try
        {
            var sealedPayload = EncryptedPayloadCodec.Deserialize(packet.Payload);
            var plain = await signal.DecryptAsync(peerTag, sealedPayload, cancellationToken).ConfigureAwait(false);

            return new MeshPacket
            {
                Id = packet.Id,
                Type = packet.Type,
                SourceUhid = packet.SourceUhid,
                DestinationUhid = packet.DestinationUhid,
                Ttl = packet.Ttl,
                Priority = packet.Priority,
                Payload = plain,
            };
        }
        catch (Exception ex)
        {
            // Say what went wrong. Swallowing this is why three separate theories about why calls
            // would not connect all had to be tested on hardware — the failure was silent, so every
            // explanation looked equally plausible.
            why?.Invoke($"{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}

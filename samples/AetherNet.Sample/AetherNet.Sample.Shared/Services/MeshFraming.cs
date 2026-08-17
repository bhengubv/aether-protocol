// SPDX-License-Identifier: MIT

using System.Text;
using AetherNet.Identity;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// How bytes are cut up and put back together on a short-range radio.
/// <para>
/// This is protocol, not platform: the same framing has to hold on Android today and on any other
/// radio head later, and none of it needs a phone to be exercised. Keeping it here — rather than
/// inside a platform's transport, where it lived and quietly drifted — is what lets it be tested at
/// all, and what stops each new head re-deriving it slightly differently.
/// </para>
/// </summary>
public static class MeshFraming
{
    /// <summary>[0x01][utf8 tag] — says who just connected.</summary>
    public const byte FrameHandshake = 0x01;

    /// <summary>[0x02][msgId][idxLo idxHi][cntLo cntHi][payload] — one slice of a larger message.</summary>
    public const byte FrameFragment = 0x02;

    /// <summary>[0x03] — "still there?", which the other side must answer.</summary>
    public const byte FramePing = 0x03;

    /// <summary>[0x04] — "yes".</summary>
    public const byte FramePong = 0x04;

    /// <summary>Bytes of fragment header before the payload begins.</summary>
    public const int FragmentHeaderLength = 6;

    /// <summary>
    /// The largest value a single Bluetooth attribute may carry, whatever the MTU claims.
    /// <para>
    /// A 517-byte MTU tempts a 514-byte write, and Android refuses every one of them — silently, as
    /// a <c>false</c> return — so nothing ever reaches the other phone. The limit is the protocol's,
    /// not the connection's.
    /// </para>
    /// </summary>
    public const int MaxAttributeValue = 512;

    /// <summary>How much payload fits in one frame at a given MTU.</summary>
    public static int UsablePayload(int mtu)
    {
        var attribute = Math.Min(Math.Max(mtu, 23) - 3, MaxAttributeValue);   // 3 bytes of ATT header
        return Math.Max(1, attribute - FragmentHeaderLength);
    }

    /// <summary>Cut a message into frames that will actually be accepted by the radio.</summary>
    public static IReadOnlyList<byte[]> Fragment(byte[] data, int mtu, byte messageId)
    {
        ArgumentNullException.ThrowIfNull(data);

        var usable = UsablePayload(mtu);
        var count = Math.Max(1, (data.Length + usable - 1) / usable);
        var frames = new List<byte[]>(count);

        for (var i = 0; i < count; i++)
        {
            var offset = i * usable;
            var length = Math.Min(usable, data.Length - offset);
            var frame = new byte[FragmentHeaderLength + length];

            frame[0] = FrameFragment;
            frame[1] = messageId;
            frame[2] = (byte)(i & 0xFF);
            frame[3] = (byte)((i >> 8) & 0xFF);
            frame[4] = (byte)(count & 0xFF);
            frame[5] = (byte)((count >> 8) & 0xFF);
            Buffer.BlockCopy(data, offset, frame, FragmentHeaderLength, length);

            frames.Add(frame);
        }

        return frames;
    }

    /// <summary>
    /// The opening frame: a rotating address, never an identity.
    /// <para>
    /// This used to carry the AetherTag, which meant anyone within radio range could enumerate and
    /// follow every phone in a room, forever, without connecting to one — the privacy threat model's
    /// first critical finding. What goes out now is an <see cref="EphemeralRoutingId"/>: derived from
    /// a routing key, changing every epoch, and unlinkable across epochs to anyone who does not hold
    /// that key. Who you actually are is revealed later, inside the encrypted session.
    /// </para>
    /// </summary>
    /// <param name="routingKey">
    /// This node's routing key — <c>ISignalProtocolService.DeriveEridRoutingKey()</c>, which is
    /// domain-separated from the identity secret so the secret itself never leaves the service.
    /// </param>
    public static byte[] Handshake(byte[] routingKey, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(routingKey);

        var seconds = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        return HandshakeFor(EphemeralRoutingId.Derive(routingKey, seconds));
    }

    /// <summary>
    /// Build the opening frame from an already-derived rotating address.
    /// <para>
    /// Refuses an AetherTag outright. The rule — the long-term identity is never on the wire in clear
    /// — is worth more than a comment reminding someone of it, so the frame builder will not carry
    /// one even if a caller asks.
    /// </para>
    /// </summary>
    public static byte[] HandshakeFor(string ephemeralId)
    {
        ArgumentException.ThrowIfNullOrEmpty(ephemeralId);

        if (AetherNetTag.TryParse(ephemeralId, out _))
            throw new ArgumentException(
                "That is a long-term AetherTag. It must never go on the wire in clear — pass a rotating routing id.",
                nameof(ephemeralId));

        var id = Encoding.UTF8.GetBytes(ephemeralId);
        var frame = new byte[1 + id.Length];
        frame[0] = FrameHandshake;
        id.CopyTo(frame, 1);
        return frame;
    }

    /// <summary>
    /// Read the peer's rotating address out of a handshake frame, or null if this is not one. It
    /// names where to send, not who they are — the identity arrives inside the session.
    /// </summary>
    public static string? ReadHandshake(byte[] frame)
    {
        if (frame is not { Length: > 1 } || frame[0] != FrameHandshake) return null;
        return Encoding.UTF8.GetString(frame, 1, frame.Length - 1);
    }

    /// <summary>
    /// Puts fragments back together. Returns the whole message once the last piece lands, and null
    /// until then. Out-of-order arrival is normal on a radio and is not an error.
    /// </summary>
    public sealed class Reassembler
    {
        /// <summary>
        /// How long a partly-arrived message is kept before its pieces are thrown away.
        ///
        /// <para>
        /// A message's fragments follow each other within milliseconds, so anything still waiting much
        /// later belongs to a send that was abandoned — a dropped link, usually. It has to be discarded
        /// rather than kept, because the fragment header names a message with a single byte and those
        /// ids come round again: a leftover piece is otherwise available to "complete" a later message
        /// that reuses the id, producing a message spliced from two different ones. That does not look
        /// like a lost frame, it looks like a broken session — the bytes deserialize into nonsense or
        /// fail their authentication tag.
        /// </para>
        ///
        /// <para>Generous enough that an ordinary slow send is never cut short.</para>
        /// </summary>
        public static readonly TimeSpan FragmentLifetime = TimeSpan.FromSeconds(20);

        /// <summary>
        /// Guarded because Bluetooth callbacks are not serialised onto one thread — fragments for
        /// different messages arrive concurrently, and an unguarded dictionary either corrupts its own
        /// bookkeeping or throws inside a callback, where a lost frame is lost silently.
        /// </summary>
        private readonly object _gate = new();

        private readonly Dictionary<byte, Slot> _inFlight = new();

        public byte[]? Accept(byte[] frame, DateTime? nowUtc = null)
        {
            if (frame is not { Length: >= FragmentHeaderLength } || frame[0] != FrameFragment) return null;

            var id = frame[1];
            var index = frame[2] | (frame[3] << 8);
            var count = frame[4] | (frame[5] << 8);
            if (count <= 0 || index < 0 || index >= count) return null;

            var now = nowUtc ?? DateTime.UtcNow;

            lock (_gate)
            {
                // A slot that has been sitting too long belongs to a send nobody finished. Its pieces
                // must not be lying around to be mistaken for part of this message.
                if (_inFlight.TryGetValue(id, out var existing) &&
                    (existing.Pieces.Length != count || now - existing.StartedUtc > FragmentLifetime))
                {
                    _inFlight.Remove(id);
                    existing = null;
                }

                if (existing is null)
                {
                    existing = new Slot(new byte[count][], now);
                    _inFlight[id] = existing;
                }

                existing.Pieces[index] = frame[FragmentHeaderLength..];
                if (existing.Pieces.Any(p => p is null)) return null;

                _inFlight.Remove(id);
                return existing.Pieces.SelectMany(p => p!).ToArray();
            }
        }

        /// <param name="StartedUtc">When the first piece of this message landed.</param>
        private sealed record Slot(byte[]?[] Pieces, DateTime StartedUtc);
    }
}

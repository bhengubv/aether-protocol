// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Security.Cryptography;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// Encrypts voice frames with <b>one key for the whole call</b>, rather than advancing a ratchet
/// fifty times a second.
///
/// <para>
/// Voice went through the Signal double ratchet at first, exactly like a chat message, and it failed
/// on the first call: <c>payload would not open</c>, for every frame. That was not a bug to patch. A
/// ratchet advances per message and tolerates a small reordering window; a 50 Hz stream over a lossy
/// radio outruns it within a second and the two sides are irrecoverably out of step. Watched on device
/// 2026-08-17 — the answering phone streamed happily while the caller could not open a single frame.
/// </para>
///
/// <para>
/// So this is the shape SRTP settled on decades ago, for the same reason. The session establishes
/// <b>one</b> master secret; each direction derives its own key from it; every frame is sealed with
/// AES-GCM under a nonce taken from a counter carried in the clear alongside it. There is no ordering
/// dependency, so a lost frame costs exactly that frame — not the remainder of the call.
/// </para>
///
/// <para>
/// A counter in the clear is not a leak: it says how many frames have gone by, which anyone watching
/// the radio can count anyway. What it buys is the ability to decrypt frame 41 having never seen 40.
/// </para>
/// </summary>
public sealed class CallMediaCipher : IDisposable
{
    /// <summary>AES-256-GCM.</summary>
    public const int KeyBytes = 32;

    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int CounterBytes = 4;

    /// <summary>
    /// Each direction gets its own key, derived from the shared master with a different label.
    ///
    /// <para>
    /// Not an optimisation — a necessity. Reusing one key both ways means both phones would eventually
    /// seal a different frame under the same key and nonce, which is the one thing AES-GCM must never
    /// do: it leaks the keystream and forges become possible. Two labels, two keys, no collision.
    /// </para>
    /// </summary>
    private const string CallerToAnswerer = "aether-voice-caller-to-answerer-v1";
    private const string AnswererToCaller = "aether-voice-answerer-to-caller-v1";

    /// <summary>
    /// And video gets its own pair again, for the same reason one direction does.
    ///
    /// <para>
    /// Audio and video share a call and a master secret, but they must not share a cipher. The nonce
    /// is a per-cipher counter, so two tracks sealing through one instance would be safe on the
    /// nonce and broken on the replay window — the receiver keeps the highest counter it has seen,
    /// and two interleaved streams would each look like the other replaying old frames. Half of every
    /// track would be discarded, silently, as an attack.
    /// </para>
    /// </summary>
    private const string VideoCallerToAnswerer = "aether-video-caller-to-answerer-v1";
    private const string VideoAnswererToCaller = "aether-video-answerer-to-caller-v1";

    private readonly AesGcm _send;
    private readonly AesGcm _receive;
    private uint _counter;
    private long _highestSeen = -1;
    private bool _disposed;

    /// <param name="master">The per-call secret, exchanged once inside the session.</param>
    /// <param name="iAmTheCaller">Which of the two directions this phone sends on.</param>
    /// <param name="video">
    /// True for the video track. One call has two of these — one per track — because they must not
    /// share a counter; see <see cref="VideoCallerToAnswerer"/>.
    /// </param>
    public CallMediaCipher(ReadOnlySpan<byte> master, bool iAmTheCaller, bool video = false)
    {
        if (master.Length != KeyBytes)
            throw new ArgumentException($"A call key is {KeyBytes} bytes.", nameof(master));

        var outbound = video
            ? (iAmTheCaller ? VideoCallerToAnswerer : VideoAnswererToCaller)
            : (iAmTheCaller ? CallerToAnswerer : AnswererToCaller);
        var inbound = video
            ? (iAmTheCaller ? VideoAnswererToCaller : VideoCallerToAnswerer)
            : (iAmTheCaller ? AnswererToCaller : CallerToAnswerer);

        var mine = Derive(master, outbound);
        var theirs = Derive(master, inbound);

        _send = new AesGcm(mine, TagBytes);
        _receive = new AesGcm(theirs, TagBytes);

        CryptographicOperations.ZeroMemory(mine);
        CryptographicOperations.ZeroMemory(theirs);
    }


    /// <summary>A fresh master secret for one call.</summary>
    public static byte[] NewMasterKey() => RandomNumberGenerator.GetBytes(KeyBytes);

    /// <summary>
    /// The same thing for a call with more than two people in it.
    ///
    /// <para>
    /// "Caller" and "answerer" stop meaning anything past two, so the direction labels are replaced by
    /// <b>who is speaking</b>. Every participant seals under a key derived from their own AetherTag,
    /// and everyone else opens their stream under a key derived from that same tag. One master secret,
    /// N distinct keys, and no pair ever has to agree on a role.
    /// </para>
    ///
    /// <para>
    /// This is what keeps the property that matters. AES-GCM must never seal two different frames
    /// under the same key and nonce; the nonce is a per-instance counter, so two people sealing under
    /// one key would collide within seconds of both talking. Deriving from the sender guarantees no
    /// two participants ever share a sealing key, however many of them there are.
    /// </para>
    ///
    /// <para>
    /// One instance holds one direction each way: what I send, and what ONE other person sends. A call
    /// with five people therefore holds four of these — each with its own replay window, for exactly
    /// the reason audio and video need separate ones.
    /// </para>
    /// </summary>
    /// <param name="master">The per-call secret, minted once and sent to each member in their session.</param>
    /// <param name="myTag">This device, whose frames this instance seals.</param>
    /// <param name="theirTag">The one other participant whose frames this instance opens.</param>
    /// <param name="video">True for the video track, which keeps its own counter.</param>
    public static CallMediaCipher ForGroup(
        ReadOnlySpan<byte> master, string myTag, string theirTag, bool video = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(myTag);
        ArgumentException.ThrowIfNullOrEmpty(theirTag);

        // Nobody is in a group call with themselves. Allowing it would hand one instance the same key
        // to seal and to open with, which is the collision this whole scheme exists to prevent.
        if (string.Equals(myTag, theirTag, StringComparison.Ordinal))
            throw new ArgumentException("A participant cannot be their own peer in a group call.", nameof(theirTag));

        return new CallMediaCipher(master, GroupLabel(myTag, video), GroupLabel(theirTag, video));
    }

    /// <summary>
    /// One participant's label on one track.
    ///
    /// <para>
    /// The tag is inside the label rather than being the whole of it, so a label can never collide
    /// with the two 1:1 direction labels above however odd a tag is.
    /// </para>
    /// </summary>
    private static string GroupLabel(string tag, bool video) =>
        (video ? "aether-group-video-v1-" : "aether-group-voice-v1-") + tag;

    /// <summary>The shared construction, once the two labels have been decided.</summary>
    private CallMediaCipher(ReadOnlySpan<byte> master, string outboundLabel, string inboundLabel)
    {
        if (master.Length != KeyBytes)
            throw new ArgumentException($"A call key is {KeyBytes} bytes.", nameof(master));

        var mine = Derive(master, outboundLabel);
        var theirs = Derive(master, inboundLabel);

        _send = new AesGcm(mine, TagBytes);
        _receive = new AesGcm(theirs, TagBytes);

        CryptographicOperations.ZeroMemory(mine);
        CryptographicOperations.ZeroMemory(theirs);
    }

    private static byte[] Derive(ReadOnlySpan<byte> master, string label)
    {
        var key = new byte[KeyBytes];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, master, key,
            salt: ReadOnlySpan<byte>.Empty, info: System.Text.Encoding.UTF8.GetBytes(label));
        return key;
    }

    /// <summary>
    /// Seal one frame: <c>[4-byte counter][ciphertext][tag]</c>.
    /// </summary>
    public byte[] Seal(ReadOnlySpan<byte> frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var counter = _counter++;
        var output = new byte[CounterBytes + frame.Length + TagBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(0, CounterBytes), counter);

        Span<byte> nonce = stackalloc byte[NonceBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(nonce[..CounterBytes], counter);

        // The counter is authenticated as associated data as well as being the nonce, so it cannot be
        // rewritten in flight to make one frame masquerade as another.
        _send.Encrypt(nonce, frame,
            output.AsSpan(CounterBytes, frame.Length),
            output.AsSpan(CounterBytes + frame.Length, TagBytes),
            output.AsSpan(0, CounterBytes));

        return output;
    }

    /// <summary>
    /// Open one frame, or null if it will not open — which for voice is ordinary and not worth a fuss.
    ///
    /// <para>
    /// A frame whose counter has already been seen, or which is far behind the newest, is refused. That
    /// is cheap replay protection: without it, anything recorded off the air could be played back into
    /// a live call.
    /// </para>
    /// </summary>
    public byte[]? Open(ReadOnlySpan<byte> sealedFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (sealedFrame.Length < CounterBytes + TagBytes) return null;

        var counter = BinaryPrimitives.ReadUInt32LittleEndian(sealedFrame[..CounterBytes]);
        if (!IsFresh(counter)) return null;

        var bodyLength = sealedFrame.Length - CounterBytes - TagBytes;
        var plaintext = new byte[bodyLength];

        Span<byte> nonce = stackalloc byte[NonceBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(nonce[..CounterBytes], counter);

        try
        {
            _receive.Decrypt(nonce,
                sealedFrame.Slice(CounterBytes, bodyLength),
                sealedFrame[(CounterBytes + bodyLength)..],
                plaintext,
                sealedFrame[..CounterBytes]);
        }
        catch (CryptographicException)
        {
            return null;
        }

        if (counter > _highestSeen) _highestSeen = counter;
        return plaintext;
    }

    /// <summary>
    /// Is this counter new enough to accept?
    ///
    /// <para>
    /// A window rather than a strict increase, because frames genuinely arrive out of order on a mesh
    /// and insisting on order would throw away good audio. Anything older than the window is either a
    /// replay or so late it is useless.
    /// </para>
    /// </summary>
    private bool IsFresh(uint counter)
    {
        const int window = 64;         // ~1.3 seconds at 20ms frames
        return counter > _highestSeen - window;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _send.Dispose();
        _receive.Dispose();
    }
}

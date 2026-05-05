// SPDX-License-Identifier: MIT

using System.Text;
using Aether.Core.Tests.Fakes;
using Aether.Messaging;
using Aether.Messaging.Models;
using Aether.Models;
using Aether.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aether.Core.Tests;

/// <summary>
/// Tests for <see cref="MessagingService"/>'s optional Brotli payload compression.
///
/// Compression sits on the plaintext side of the cipher: <c>SendAsync</c> prepends
/// a 1-byte flag (0x00 = uncompressed, 0x01 = brotli) and optionally compresses
/// the body before encryption; <c>HandleAsync</c> strips the flag and decompresses
/// after decryption. The flag is always present on the plaintext, so the wire
/// envelope is unchanged from the cipher's perspective.
/// </summary>
public class MessagingCompressionTests
{
    private const string Local = "local-uhid";
    private const string Remote = "remote-uhid";

    // ─── Round-trip — both compressible and incompressible inputs ─

    [Fact]
    public async Task RoundTrip_HighlyCompressiblePayload_RecoversOriginalPlaintext()
    {
        // 4 KB of zeros — Brotli should crush this to a tiny frame.
        var plaintext = new byte[4096];
        await AssertRoundTripAsync(plaintext);
    }

    [Fact]
    public async Task RoundTrip_RandomPayload_RecoversOriginalPlaintext()
    {
        // 4 KB of random bytes — high entropy, Brotli will likely make it larger,
        // so the service should fall back to flag=0x00. Round-trip still works.
        var plaintext = new byte[4096];
        new Random(42).NextBytes(plaintext);
        await AssertRoundTripAsync(plaintext);
    }

    // ─── Compression actually shrinks compressible payloads ──────

    [Fact]
    public async Task HighlyCompressiblePayload_ProducesCipherInputSmallerThanRaw()
    {
        // Ship 4 KB of zeros and confirm the bytes the cipher receives (which are
        // also the bytes that go on the wire when paired with a passthrough cipher)
        // are well under half the raw plaintext size.
        var (sender, recordingCipher) = NewSendingPair(compressionEnabled: true);
        var plaintext = new byte[4096]; // all zeros — perfect for Brotli

        await sender.SendAsync(new MeshMessage { RecipientUhid = Remote }, plaintext);

        var framed = recordingCipher.LastEncryptedPlaintext;
        Assert.NotNull(framed);
        Assert.Equal(0x01, framed![0]); // brotli flag set
        var bodyLength = framed.Length - 1;
        // 4096 zero bytes compress to a few dozen bytes — assert < 0.5 ratio.
        Assert.True(bodyLength < plaintext.Length / 2,
            $"compressed body {bodyLength} should be less than half of raw {plaintext.Length}");
    }

    // ─── Compressed-was-bigger fallback for already-compressed payloads ─

    [Fact]
    public async Task AlreadyCompressedPayload_FallsBackToUncompressedFlag()
    {
        // High-entropy random bytes mimic already-compressed content (audio,
        // video, encrypted blobs). Brotli's framing overhead means the output
        // is >= input, so the service should fall back to flag=0x00.
        var (sender, recordingCipher) = NewSendingPair(compressionEnabled: true);
        var plaintext = new byte[4096];
        new Random(1337).NextBytes(plaintext);

        await sender.SendAsync(new MeshMessage { RecipientUhid = Remote }, plaintext);

        var framed = recordingCipher.LastEncryptedPlaintext;
        Assert.NotNull(framed);
        Assert.Equal(0x00, framed![0]); // fell back to uncompressed
        Assert.Equal(plaintext.Length + 1, framed.Length); // raw payload + 1 flag byte
        Assert.Equal(plaintext, framed.Skip(1).ToArray());
    }

    // ─── Below-threshold payloads bypass compression entirely ────

    [Fact]
    public async Task PayloadBelowMinSize_NeverAttemptsCompression()
    {
        var (sender, recordingCipher) = NewSendingPair(
            compressionEnabled: true,
            minSizeBytes: 256);
        var plaintext = new byte[100]; // < 256, all zeros (would compress superbly if attempted)

        await sender.SendAsync(new MeshMessage { RecipientUhid = Remote }, plaintext);

        var framed = recordingCipher.LastEncryptedPlaintext;
        Assert.NotNull(framed);
        Assert.Equal(0x00, framed![0]);
        // Raw payload is preserved verbatim — compression was not even attempted.
        Assert.Equal(plaintext.Length + 1, framed.Length);
        Assert.Equal(plaintext, framed.Skip(1).ToArray());
    }

    // ─── Compression disabled — flag is always 0x00 ──────────────

    [Fact]
    public async Task CompressionDisabled_AlwaysShipsFlagZeroAndRawPayload()
    {
        var (sender, recordingCipher) = NewSendingPair(compressionEnabled: false);
        // Highly compressible payload that *would* normally take flag 0x01.
        var plaintext = new byte[4096];

        await sender.SendAsync(new MeshMessage { RecipientUhid = Remote }, plaintext);

        var framed = recordingCipher.LastEncryptedPlaintext;
        Assert.NotNull(framed);
        Assert.Equal(0x00, framed![0]);
        Assert.Equal(plaintext.Length + 1, framed.Length);
    }

    // ─── Unknown flag — the receiver drops and logs ──────────────

    [Fact]
    public async Task ReceivedPayloadWithUnknownFlag_IsDroppedSilently()
    {
        // Force the cipher to yield a "decrypted" buffer whose first byte is 0x99 —
        // a flag value the service doesn't recognise. The service must drop the
        // message and never raise MessageReceived.
        var sender = new FakeMeshSender(Local);
        var routing = new FakeRoutingService();
        var cipher = new RecordingCipher();
        var ciphertext = new byte[] { 1, 2, 3 };
        cipher.SetDecryptResult(Remote, ciphertext, framedPlaintext: new byte[] { 0x99, 0xAA, 0xBB });
        var svc = new MessagingService(sender, routing, cipher: cipher,
            logger: NullLogger<MessagingService>.Instance);

        var fired = false;
        svc.MessageReceived += (_, _) => fired = true;

        await svc.HandleAsync(new MeshPacket
        {
            Type = PacketType.Data,
            SourceUhid = Remote,
            DestinationUhid = Local,
            Payload = ciphertext,
        });

        Assert.False(fired);
        // No delivery ack went on the wire either.
        Assert.Empty(sender.Unicasts);
        Assert.Empty(sender.Broadcasts);
    }

    // ─── Fixture binding — flag=0x00 carries the raw plaintext ───

    [Fact]
    public async Task ReceivedPayloadWithFlagZero_DeliversRawPlaintextAsIs()
    {
        // Encode a known plaintext with flag=0x00 manually and confirm the
        // service strips the flag byte cleanly and surfaces the original bytes.
        var sender = new FakeMeshSender(Local);
        var routing = new FakeRoutingService();
        var cipher = new RecordingCipher();
        var expected = Encoding.UTF8.GetBytes("hello world");
        var framed = new byte[] { 0x00 }.Concat(expected).ToArray();
        var ciphertext = new byte[] { 7, 7, 7 };
        cipher.SetDecryptResult(Remote, ciphertext, framed);
        var svc = new MessagingService(sender, routing, cipher: cipher,
            logger: NullLogger<MessagingService>.Instance);

        MeshMessage? observed = null;
        svc.MessageReceived += (_, m) => observed = m;

        await svc.HandleAsync(new MeshPacket
        {
            Type = PacketType.Data,
            SourceUhid = Remote,
            DestinationUhid = Local,
            Payload = ciphertext,
        });

        Assert.NotNull(observed);
        Assert.Equal(expected, observed!.EncryptedContent);
    }

    // ─── Helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Pairs a sender-side <see cref="MessagingService"/> against a receiver-side
    /// service that share one <see cref="LoopbackCipher"/>. The cipher's "encrypt"
    /// stashes the framed plaintext and returns it unchanged as the ciphertext;
    /// "decrypt" hands back the same buffer. This lets the test pump bytes from
    /// SendAsync through HandleAsync end-to-end without depending on Signal.
    /// </summary>
    private static async Task AssertRoundTripAsync(byte[] plaintext)
    {
        var senderTransport = new FakeMeshSender(Local);
        var routing = new FakeRoutingService();
        routing.SetRoute(Remote, nextHop: "neighbour");
        var loopback = new LoopbackCipher();

        var senderSvc = new MessagingService(senderTransport, routing, cipher: loopback,
            logger: NullLogger<MessagingService>.Instance);

        // Receiver-side service uses Remote as its local node so the inbound packet
        // is treated as "for me". Routing isn't needed on the receive path beyond
        // the optional ack; we leave the table empty so the ack falls back to broadcast.
        var receiverTransport = new FakeMeshSender(Remote);
        var receiverSvc = new MessagingService(receiverTransport, new FakeRoutingService(),
            cipher: loopback,
            logger: NullLogger<MessagingService>.Instance);

        MeshMessage? observed = null;
        receiverSvc.MessageReceived += (_, m) => observed = m;

        var sent = await senderSvc.SendAsync(new MeshMessage { RecipientUhid = Remote }, plaintext);
        Assert.True(sent);

        var unicast = Assert.Single(senderTransport.Unicasts);

        // Re-target the packet at the receiver and feed it in.
        var inbound = new MeshPacket
        {
            Id = unicast.Packet.Id,
            Type = PacketType.Data,
            SourceUhid = Local,
            DestinationUhid = Remote,
            Payload = unicast.Packet.Payload,
            Priority = unicast.Packet.Priority,
        };
        await receiverSvc.HandleAsync(inbound);

        Assert.NotNull(observed);
        Assert.Equal(plaintext, observed!.EncryptedContent);
    }

    /// <summary>
    /// Builds a <see cref="MessagingService"/> wired to a <see cref="RecordingCipher"/>
    /// so the test can inspect exactly what bytes the cipher was asked to encrypt.
    /// </summary>
    private static (MessagingService Service, RecordingCipher Cipher) NewSendingPair(
        bool compressionEnabled,
        int? minSizeBytes = null)
    {
        var sender = new FakeMeshSender(Local);
        var routing = new FakeRoutingService();
        routing.SetRoute(Remote, nextHop: "neighbour");
        var cipher = new RecordingCipher();
        var options = new MessagingOptions
        {
            Compression = new CompressionOptions
            {
                Enabled = compressionEnabled,
                MinSizeBytes = minSizeBytes ?? 256,
            },
        };
        var svc = new MessagingService(sender, routing, cipher: cipher,
            options: options,
            logger: NullLogger<MessagingService>.Instance);
        return (svc, cipher);
    }

    /// <summary>
    /// Cipher that records the plaintext it was asked to encrypt and echoes it back
    /// as the "ciphertext". Lets tests inspect the framing without faking encryption.
    /// </summary>
    private sealed class RecordingCipher : IMessageEnvelopeCipher
    {
        private readonly List<(string Sender, byte[] Ciphertext, byte[] Plaintext)> _decrypts = new();
        public byte[]? LastEncryptedPlaintext { get; private set; }

        public void SetDecryptResult(string senderUhid, byte[] ciphertext, byte[] framedPlaintext)
            => _decrypts.Add((senderUhid, ciphertext, framedPlaintext));

        public Task<byte[]?> EncryptAsync(string recipientUhid, byte[] plaintext, CancellationToken cancellationToken = default)
        {
            LastEncryptedPlaintext = plaintext;
            // Echo the plaintext as the "ciphertext" so the wire payload IS the framed plaintext.
            return Task.FromResult<byte[]?>(plaintext);
        }

        public Task<byte[]?> DecryptAsync(string senderUhid, byte[] ciphertext, CancellationToken cancellationToken = default)
        {
            foreach (var (s, c, p) in _decrypts)
            {
                if (string.Equals(s, senderUhid, StringComparison.Ordinal) && c.SequenceEqual(ciphertext))
                    return Task.FromResult<byte[]?>(p);
            }
            return Task.FromResult<byte[]?>(null);
        }

        public bool HasSession(string peerUhid) => true;
    }

    /// <summary>
    /// Cipher that's its own inverse: encrypt and decrypt are identity functions.
    /// Useful for end-to-end round-trip tests where we want to drive bytes through
    /// SendAsync and HandleAsync without depending on Signal-level state.
    /// </summary>
    private sealed class LoopbackCipher : IMessageEnvelopeCipher
    {
        public Task<byte[]?> EncryptAsync(string recipientUhid, byte[] plaintext, CancellationToken cancellationToken = default)
            => Task.FromResult<byte[]?>(plaintext);

        public Task<byte[]?> DecryptAsync(string senderUhid, byte[] ciphertext, CancellationToken cancellationToken = default)
            => Task.FromResult<byte[]?>(ciphertext);

        public bool HasSession(string peerUhid) => true;
    }
}

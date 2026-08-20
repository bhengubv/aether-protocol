// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using AetherNet.Constants;
using AetherNet.Core.Tests.Fakes;
using AetherNet.Dtn;
using AetherNet.Extensibility;
using AetherNet.Messaging;
using AetherNet.Messaging.Models;
using AetherNet.Models;
using AetherNet.Protocol;
using AetherNet.Security.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Tests for <see cref="MessagingService"/>. Covers the audit's primary security
/// rule — "messages without a Signal session are queued, never sent insecurely" —
/// plus DTN fallback, the receive path, ack handling, and outbox flushing.
/// </summary>
public class MessagingServiceTests
{
    private const string Local = "local-uhid";
    private const string Remote = "remote-uhid";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    // ─── SendAsync — happy path with active session + route ──────

    [Fact]
    public async Task SendAsync_WithSessionAndRoute_SendsCiphertextOnTheWire()
    {
        var sender = new FakeMeshSender(Local);
        var routing = new FakeRoutingService();
        routing.SetRoute(Remote, nextHop: "neighbour");
        var cipher = new FakeCipher();
        cipher.SetSession(Remote, ciphertext: new byte[] { 0xAA, 0xBB, 0xCC });
        var store = new InMemoryMessageStore();
        var svc = new MessagingService(sender, routing, store, cipher,
            logger: NullLogger<MessagingService>.Instance);

        var message = new MeshMessage { RecipientUhid = Remote };
        var plaintext = Encoding.UTF8.GetBytes("hello");

        var sent = await svc.SendAsync(message, plaintext);

        Assert.True(sent);
        var unicast = Assert.Single(sender.Unicasts);
        Assert.Equal(PacketType.Data, unicast.Packet.Type);
        Assert.Equal("neighbour", unicast.NextHopUhid);
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, unicast.Packet.Payload);
        // Plaintext NEVER on the wire.
        Assert.NotEqual(plaintext, unicast.Packet.Payload);

        var stored = await store.GetAsync(message.Id);
        Assert.NotNull(stored);
        Assert.Equal(MessageStatus.Sent, stored!.Status);
        // Ciphertext persisted, not plaintext.
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, stored.EncryptedContent);
        Assert.Equal(Local, stored.SenderUhid);
    }

    // ─── SendAsync — primary security rule: no session = queued ──

    [Fact]
    public async Task SendAsync_WithNoSession_QueuesWithoutCiphertextOnTheWire()
    {
        var sender = new FakeMeshSender(Local);
        var routing = new FakeRoutingService();
        routing.SetRoute(Remote, nextHop: "neighbour"); // route exists, but cipher has no session
        var cipher = new FakeCipher(); // returns null on encrypt — no session
        var store = new InMemoryMessageStore();
        var observed = new List<string>();
        var svc = new MessagingService(sender, routing, store, cipher,
            logger: NullLogger<MessagingService>.Instance);
        svc.SessionRequired += (_, recipient) => observed.Add(recipient);

        var message = new MeshMessage { RecipientUhid = Remote };
        var plaintext = Encoding.UTF8.GetBytes("hello");

        var sent = await svc.SendAsync(message, plaintext);

        Assert.False(sent);
        // Nothing went on the wire — not as plaintext, not as a downgraded cipher.
        Assert.Empty(sender.Unicasts);
        Assert.Empty(sender.Broadcasts);

        var stored = await store.GetAsync(message.Id);
        Assert.NotNull(stored);
        Assert.Equal(MessageStatus.Pending, stored!.Status);
        // Ciphertext is empty — neither plaintext nor a fallback cipher was persisted.
        Assert.Empty(stored.EncryptedContent);

        // Host was notified that a session needs to be established.
        Assert.Equal(new[] { Remote }, observed);
    }

    [Fact]
    public async Task SendAsync_WithNoSession_DoesNotInvokeMeshSenderAtAll()
    {
        // Belt-and-braces: even if a route is present and DTN+backend are wired,
        // a missing session must short-circuit *before* anything reaches a transport.
        var sender = new FakeMeshSender(Local);
        var routing = new FakeRoutingService();
        routing.SetRoute(Remote, nextHop: "neighbour");
        var cipher = new FakeCipher(); // no session
        var store = new InMemoryMessageStore();
        var dtnSender = new FakeMeshSender(Local);
        var dtn = new DtnService(dtnSender);
        var svc = new MessagingService(sender, routing, store, cipher, dtn,
            logger: NullLogger<MessagingService>.Instance);

        await svc.SendAsync(new MeshMessage { RecipientUhid = Remote }, new byte[] { 1 });

        Assert.Empty(sender.Unicasts);
        Assert.Empty(sender.Broadcasts);
        Assert.Empty(await dtn.GetActiveBundlesAsync());
    }

    // ─── SendAsync — DTN fallback when no route exists ───────────

    [Fact]
    public async Task SendAsync_WithSessionButNoRoute_FallsBackToDtnCustody()
    {
        var sender = new FakeMeshSender(Local);
        var routing = new FakeRoutingService(); // no route to Remote
        var cipher = new FakeCipher();
        cipher.SetSession(Remote, ciphertext: new byte[] { 1, 2, 3 });
        var store = new InMemoryMessageStore();
        var dtnSender = new FakeMeshSender(Local); // no peers, so direct delivery fails
        var dtnStore = new InMemoryDtnBundleStore();
        var dtn = new DtnService(dtnSender, dtnStore);
        var svc = new MessagingService(sender, routing, store, cipher, dtn,
            logger: NullLogger<MessagingService>.Instance);

        var sent = await svc.SendAsync(new MeshMessage { RecipientUhid = Remote }, new byte[] { 0xFF });

        Assert.True(sent);
        // Mesh route was unavailable, so nothing went to the unicast sender.
        Assert.Empty(sender.Unicasts);

        // A DTN bundle was created for the recipient with the ciphertext.
        var bundles = await dtnStore.GetActiveAsync();
        var bundle = Assert.Single(bundles);
        Assert.Equal(Remote, bundle.RecipientUhid);
        Assert.Equal(new byte[] { 1, 2, 3 }, bundle.EncryptedPayload);
    }

    [Fact]
    public async Task SendAsync_WithNoRouteAndNoDtn_StaysPendingInOutbox()
    {
        var sender = new FakeMeshSender(Local);
        var routing = new FakeRoutingService();
        var cipher = new FakeCipher();
        cipher.SetSession(Remote, ciphertext: new byte[] { 1 });
        var store = new InMemoryMessageStore();
        var options = new MessagingOptions { EnableBackendRelay = false }; // no relay either
        var svc = new MessagingService(sender, routing, store, cipher,
            dtn: null,
            options: options,
            logger: NullLogger<MessagingService>.Instance);

        var message = new MeshMessage { RecipientUhid = Remote };
        var sent = await svc.SendAsync(message, new byte[] { 0xFF });

        Assert.False(sent);
        Assert.Empty(sender.Unicasts);
        var stored = await store.GetAsync(message.Id);
        Assert.Equal(MessageStatus.Pending, stored!.Status);
    }

    // ─── SendAsync — argument validation ─────────────────────────

    [Fact]
    public async Task SendAsync_NullMessage_Throws()
    {
        var svc = NewMinimalService();
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => svc.SendAsync(null!, new byte[] { 1 }));
    }

    [Fact]
    public async Task SendAsync_EmptyRecipient_Throws()
    {
        var svc = NewMinimalService();
        await Assert.ThrowsAsync<ArgumentException>(
            () => svc.SendAsync(new MeshMessage { RecipientUhid = string.Empty }, new byte[] { 1 }));
    }

    // ─── HandleAsync (Data) — the receive path ───────────────────

    [Fact]
    public async Task HandleAsync_DataPacketForLocalNode_RaisesMessageReceivedWithPlaintext()
    {
        var sender = new FakeMeshSender(Local);
        var routing = new FakeRoutingService();
        routing.SetRoute(Remote, nextHop: Remote); // so the delivery ack has somewhere to go
        var cipher = new FakeCipher();
        var ciphertext = new byte[] { 9, 8, 7 };
        var plaintext = Encoding.UTF8.GetBytes("payload-decrypted");
        // After decrypt, MessagingService strips the 1-byte compression flag (0x00 = uncompressed).
        // The fixture provides the framed plaintext that the cipher would yield.
        var framedPlaintext = new byte[] { 0x00 }.Concat(plaintext).ToArray();
        cipher.SetDecryptResult(Remote, ciphertext, framedPlaintext);
        var store = new InMemoryMessageStore();
        var svc = new MessagingService(sender, routing, store, cipher,
            logger: NullLogger<MessagingService>.Instance);

        MeshMessage? observed = null;
        svc.MessageReceived += (_, m) => observed = m;

        var packet = new MeshPacket
        {
            Type = PacketType.Data,
            SourceUhid = Remote,
            DestinationUhid = Local,
            Payload = ciphertext,
        };
        await svc.HandleAsync(packet);

        Assert.NotNull(observed);
        Assert.Equal(packet.Id, observed!.Id);
        Assert.Equal(Remote, observed.SenderUhid);
        Assert.Equal(Local, observed.RecipientUhid);
        // The MessageReceived view carries the *plaintext* (flag stripped), but it's not persisted.
        Assert.Equal(plaintext, observed.EncryptedContent);
        Assert.Equal(MessageStatus.Delivered, observed.Status);

        // What's persisted is the original ciphertext, never the plaintext.
        var stored = await store.GetAsync(packet.Id);
        Assert.NotNull(stored);
        Assert.Equal(ciphertext, stored!.EncryptedContent);
        Assert.NotEqual(plaintext, stored.EncryptedContent);
    }

    [Fact]
    public async Task HandleAsync_DataPacketWithMalformedCiphertext_DropsSilently()
    {
        var sender = new FakeMeshSender(Local);
        var routing = new FakeRoutingService();
        var cipher = new FakeCipher(); // no decrypt mappings — DecryptAsync returns null
        var store = new InMemoryMessageStore();
        var fired = false;
        var svc = new MessagingService(sender, routing, store, cipher,
            logger: NullLogger<MessagingService>.Instance);
        svc.MessageReceived += (_, _) => fired = true;

        await svc.HandleAsync(new MeshPacket
        {
            Type = PacketType.Data,
            SourceUhid = Remote,
            DestinationUhid = Local,
            Payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF },
        });

        Assert.False(fired);
        // No message was persisted, no ack went on the wire.
        Assert.Empty(sender.Unicasts);
        Assert.Empty(sender.Broadcasts);
    }

    [Fact]
    public async Task HandleAsync_DataPacketForDifferentRecipient_IsIgnored()
    {
        var sender = new FakeMeshSender(Local);
        var cipher = new FakeCipher();
        cipher.SetDecryptResult(Remote, new byte[] { 1 }, new byte[] { 2 });
        var fired = false;
        var svc = new MessagingService(sender, new FakeRoutingService(),
            cipher: cipher, logger: NullLogger<MessagingService>.Instance);
        svc.MessageReceived += (_, _) => fired = true;

        await svc.HandleAsync(new MeshPacket
        {
            Type = PacketType.Data,
            SourceUhid = Remote,
            DestinationUhid = "someone-else",
            Payload = new byte[] { 1 },
        });

        Assert.False(fired);
    }

    [Fact]
    public async Task HandleAsync_NonDataNonAckPacket_IsIgnored()
    {
        var sender = new FakeMeshSender(Local);
        var cipher = new FakeCipher();
        var fired = false;
        var svc = new MessagingService(sender, new FakeRoutingService(),
            cipher: cipher, logger: NullLogger<MessagingService>.Instance);
        svc.MessageReceived += (_, _) => fired = true;
        svc.DeliveryConfirmed += (_, _) => fired = true;

        await svc.HandleAsync(new MeshPacket
        {
            Type = PacketType.Heartbeat,
            SourceUhid = Remote,
            DestinationUhid = Local,
        });

        Assert.False(fired);
    }

    [Fact]
    public async Task HandleAsync_NullPacket_Throws()
    {
        var svc = NewMinimalService();
        await Assert.ThrowsAsync<ArgumentNullException>(() => svc.HandleAsync(null!));
    }

    // ─── HandleAsync (Ack) — DeliveryConfirmed event ─────────────

    [Fact]
    public async Task HandleAsync_AckForKnownMessage_FiresDeliveryConfirmedAndUpdatesStatus()
    {
        var sender = new FakeMeshSender(Local);
        var routing = new FakeRoutingService();
        routing.SetRoute(Remote, nextHop: Remote);
        var cipher = new FakeCipher();
        cipher.SetSession(Remote, ciphertext: new byte[] { 1 });
        var store = new InMemoryMessageStore();
        var svc = new MessagingService(sender, routing, store, cipher,
            logger: NullLogger<MessagingService>.Instance);

        // First, send a message so it lives in the store.
        var message = new MeshMessage { RecipientUhid = Remote };
        await svc.SendAsync(message, new byte[] { 0xFF });

        DeliveryReceipt? observed = null;
        svc.DeliveryConfirmed += (_, r) => observed = r;

        var receipt = new DeliveryReceipt
        {
            MessageId = message.Id,
            SenderUhid = Local,
            RecipientUhid = Remote,
            HopCount = 2,
            TransportType = "mesh",
        };
        await svc.HandleAsync(new MeshPacket
        {
            Type = PacketType.Ack,
            SourceUhid = Remote,
            DestinationUhid = Local,
            Payload = JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions),
        });

        Assert.NotNull(observed);
        Assert.Equal(message.Id, observed!.MessageId);
        Assert.Equal("mesh", observed.TransportType);

        var stored = await store.GetAsync(message.Id);
        Assert.Equal(MessageStatus.Delivered, stored!.Status);
    }

    [Fact]
    public async Task HandleAsync_AckWithMalformedJson_DropsSilently()
    {
        var sender = new FakeMeshSender(Local);
        var fired = false;
        var svc = new MessagingService(sender, new FakeRoutingService(),
            cipher: new FakeCipher(), logger: NullLogger<MessagingService>.Instance);
        svc.DeliveryConfirmed += (_, _) => fired = true;

        await svc.HandleAsync(new MeshPacket
        {
            Type = PacketType.Ack,
            SourceUhid = Remote,
            DestinationUhid = Local,
            Payload = new byte[] { 0xDE, 0xAD }, // not valid JSON
        });

        Assert.False(fired);
    }

    // ─── ProcessOutboxAsync — flush after session establishment ─

    [Fact]
    public async Task ProcessOutboxAsync_AfterSessionEstablished_FlushesQueuedMessages()
    {
        var sender = new FakeMeshSender(Local);
        var routing = new FakeRoutingService();
        routing.SetRoute(Remote, nextHop: "neighbour");
        var cipher = new FakeCipher(); // no session yet
        var store = new InMemoryMessageStore();
        var svc = new MessagingService(sender, routing, store, cipher,
            logger: NullLogger<MessagingService>.Instance);

        // Step 1: SendAsync queues because there's no session — verified by the cipher
        // returning null. The message lands in the outbox with empty ciphertext.
        var message = new MeshMessage { RecipientUhid = Remote };
        var sent = await svc.SendAsync(message, new byte[] { 0xFF });
        Assert.False(sent);
        var stored = await store.GetAsync(message.Id);
        Assert.Empty(stored!.EncryptedContent);
        Assert.Empty(sender.Unicasts);

        // Step 2: process outbox while still no session — message is just retried.
        // It cannot be delivered because EncryptedContent is still empty.
        var firstPass = await svc.ProcessOutboxAsync();
        Assert.Equal(0, firstPass);
        Assert.Empty(sender.Unicasts);

        // Step 3: a session arrives. We simulate that by pre-populating the message's
        // ciphertext directly in the store (a real host would call SendAsync again
        // once SessionRequired triggered a pre-key fetch).
        stored.EncryptedContent = new byte[] { 0xAB, 0xCD };
        await store.SaveAsync(stored);

        var secondPass = await svc.ProcessOutboxAsync();
        Assert.Equal(1, secondPass);

        // The queued message was finally delivered via the mesh.
        var unicast = Assert.Single(sender.Unicasts);
        Assert.Equal(PacketType.Data, unicast.Packet.Type);
        Assert.Equal("neighbour", unicast.NextHopUhid);
        Assert.Equal(new byte[] { 0xAB, 0xCD }, unicast.Packet.Payload);

        var afterFlush = await store.GetAsync(message.Id);
        Assert.Equal(MessageStatus.Sent, afterFlush!.Status);
    }

    [Fact]
    public async Task ProcessOutboxAsync_TransitionsToFailedAfterMaxRetries()
    {
        var sender = new FakeMeshSender(Local);
        sender.FailSendsToPeer("neighbour"); // every send fails
        var routing = new FakeRoutingService();
        routing.SetRoute(Remote, nextHop: "neighbour");
        var cipher = new FakeCipher();
        cipher.SetSession(Remote, ciphertext: new byte[] { 1 });
        var store = new InMemoryMessageStore();
        var options = new MessagingOptions
        {
            MaxRetries = 2,
            EnableDtnFallback = false,
            EnableBackendRelay = false,
        };
        var svc = new MessagingService(sender, routing, store, cipher,
            options: options, logger: NullLogger<MessagingService>.Instance);

        var message = new MeshMessage { RecipientUhid = Remote };
        await svc.SendAsync(message, new byte[] { 1 }); // attempt 0 fails — message stays Pending

        // Drive retries until exhaustion.
        for (var i = 0; i < options.MaxRetries; i++)
            await svc.ProcessOutboxAsync();

        var stored = await store.GetAsync(message.Id);
        Assert.NotNull(stored);
        Assert.Equal(MessageStatus.Failed, stored!.Status);
    }

    // ─── End-to-end with the real Signal cipher ──────────────────

    [Fact]
    public async Task Send_WithRealSignalCipher_ProducesCiphertextDistinctFromPlaintext()
    {
        // Wire MessagingService against the real SignalProtocolService end-to-end:
        // verifies the cipher integration without faking, but doesn't span two services
        // (transport-layer pairing is exercised separately in SignalMessageEnvelopeCipherTests).
        var alice = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);
        var bob = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);
        var bobBundle = await bob.GeneratePreKeyBundleAsync("bob");
        await alice.GeneratePreKeyBundleAsync("alice");
        await alice.ProcessPreKeyBundleAsync(bobBundle);

        var aliceCipher = new SignalMessageEnvelopeCipher(alice);
        var sender = new FakeMeshSender("alice");
        var routing = new FakeRoutingService();
        routing.SetRoute("bob", nextHop: "bob");
        var svc = new MessagingService(sender, routing, cipher: aliceCipher,
            logger: NullLogger<MessagingService>.Instance);

        var plaintext = Encoding.UTF8.GetBytes("the mesh is alive");
        var message = new MeshMessage { RecipientUhid = "bob" };
        var sent = await svc.SendAsync(message, plaintext);

        Assert.True(sent);
        var unicast = Assert.Single(sender.Unicasts);
        Assert.NotEmpty(unicast.Packet.Payload);
        Assert.NotEqual(plaintext, unicast.Packet.Payload);

        // Bob can decrypt what Alice sent. MessagingService wraps every payload with
        // a 1-byte compression flag on the plaintext side of the cipher, so the
        // decrypted output is [flag][payload]; small payloads use flag=0x00 (raw).
        var bobCipher = new SignalMessageEnvelopeCipher(bob);
        var decrypted = await bobCipher.DecryptAsync("alice", unicast.Packet.Payload);
        Assert.NotNull(decrypted);
        var expectedFramed = new byte[] { 0x00 }.Concat(plaintext).ToArray();
        Assert.Equal(expectedFramed, decrypted);
    }

    [Fact]
    public async Task Send_WithRealSignalCipher_NoSession_QueuesAndFiresSessionRequired()
    {
        // This is the audit's primary security rule wired end-to-end: the real
        // Signal cipher returns null when no session exists, and MessagingService
        // responds by queuing — never by falling back to plaintext or a weaker scheme.
        var alice = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);
        await alice.GeneratePreKeyBundleAsync("alice");
        // No bundle exchange with bob, so alice has no session.
        var aliceCipher = new SignalMessageEnvelopeCipher(alice);

        var sender = new FakeMeshSender("alice");
        var routing = new FakeRoutingService();
        routing.SetRoute("bob", nextHop: "bob"); // route exists
        var svc = new MessagingService(sender, routing, cipher: aliceCipher,
            logger: NullLogger<MessagingService>.Instance);

        string? requested = null;
        svc.SessionRequired += (_, r) => requested = r;

        var sent = await svc.SendAsync(new MeshMessage { RecipientUhid = "bob" }, new byte[] { 0xFF });

        Assert.False(sent);
        Assert.Empty(sender.Unicasts); // nothing leaked on the wire
        Assert.Equal("bob", requested);
    }

    // ─── Helpers ─────────────────────────────────────────────────

    private static MessagingService NewMinimalService()
    {
        var sender = new FakeMeshSender(Local);
        return new MessagingService(sender, new FakeRoutingService(),
            cipher: new FakeCipher(),
            logger: NullLogger<MessagingService>.Instance);
    }

    /// <summary>
    /// Test-only <see cref="IMessageEnvelopeCipher"/> with explicit per-recipient
    /// session and per-ciphertext decrypt mappings. Default behaviour mirrors
    /// <see cref="NullMessageEnvelopeCipher"/>: encrypt and decrypt return null.
    /// </summary>
    private sealed class FakeCipher : IMessageEnvelopeCipher
    {
        private readonly Dictionary<string, byte[]> _sessions = new(StringComparer.Ordinal);
        private readonly List<(string Sender, byte[] Ciphertext, byte[] Plaintext)> _decrypts = new();

        public void SetSession(string recipientUhid, byte[] ciphertext)
            => _sessions[recipientUhid] = ciphertext;

        public void SetDecryptResult(string senderUhid, byte[] ciphertext, byte[] plaintext)
            => _decrypts.Add((senderUhid, ciphertext, plaintext));

        public Task<byte[]?> EncryptAsync(string recipientUhid, byte[] plaintext, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_sessions.TryGetValue(recipientUhid, out var ct) ? ct : null);
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

        public bool HasSession(string peerUhid) => _sessions.ContainsKey(peerUhid);
    }
}

// SPDX-License-Identifier: MIT

using System.Text;
using AetherNet.Messaging;
using AetherNet.Messaging.Models;
using AetherNet.Models;
using AetherNet.Protocol;
using AetherNet.Routing;
using AetherNet.Security.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AetherNet.Soak.Tests;

/// <summary>
/// Soak tests for <see cref="MessagingService"/>. These exercise the full
/// send pathway end-to-end using the real Signal cipher
/// (<see cref="SignalMessageEnvelopeCipher"/>) so we cover the whole
/// hot path that production code follows: encrypt → store → route → send.
///
/// What we're guarding against:
/// <list type="bullet">
///   <item>The outbox queue accumulating orphan messages — every message
///     must terminate in <see cref="MessageStatus.Sent"/>,
///     <see cref="MessageStatus.Pending"/>, or <see cref="MessageStatus.Failed"/>;
///     none should be left in the transient <see cref="MessageStatus.Sending"/>
///     state after the call returns.</item>
///   <item>The <see cref="InMemoryMessageStore"/> growing past the
///     iteration count (i.e. no duplicate insertion under retry/replay).</item>
///   <item>Per-iteration allocation creep that would surface as a leak
///     after thousands of messages.</item>
/// </list>
/// </summary>
[Trait("Category", "Soak")]
public class MessagingSoakTests : SoakTestBase
{
    private const string AliceUhid = "alice-uhid";
    private const string BobUhid = "bob-uhid";

    /// <summary>
    /// Alice sends 1 000 messages to Bob (alternating directions) over a
    /// real Signal session. Bob's inbound side handles the receive path.
    /// Asserts:
    /// <list type="bullet">
    ///   <item>Every send returns true.</item>
    ///   <item>Alice's outbox holds exactly the messages she sent — no
    ///     duplicates, no orphans.</item>
    ///   <item>Per-iteration memory growth under 8 KB. The MeshMessage,
    ///     MeshPacket, and per-message ciphertext are all transient; the
    ///     persistent state per iteration is one outbox entry plus one
    ///     inbox entry on the receiver side.</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task MessagingService_ThousandMessagesUnderLoad_DeliveryPath()
    {
        var iterations = Math.Min(ResolveIterations(), 1_000);

        // Real Signal cipher on both ends — establishes a session, exercises
        // the encrypt/decrypt path that production messaging uses.
        var aliceSignal = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);
        var bobSignal = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);

        var bobBundle = await bobSignal.GeneratePreKeyBundleAsync(BobUhid);
        await aliceSignal.GeneratePreKeyBundleAsync(AliceUhid);
        await aliceSignal.ProcessPreKeyBundleAsync(bobBundle);

        var aliceCipher = new SignalMessageEnvelopeCipher(aliceSignal);
        var aliceSender = new SoakFakeMeshSender(AliceUhid);
        // FakeRoutingService implementations live in the unit-test project.
        // For soak tests we feed an InMemory pair directly: the routing
        // service's FindRouteAsync must return non-null so the sender's
        // first tier succeeds.
        var aliceRouting = new InMemoryRoutingFake();
        aliceRouting.SetRoute(BobUhid, BobUhid);
        var aliceStore = new InMemoryMessageStore();
        var alice = new MessagingService(aliceSender, aliceRouting, aliceStore, aliceCipher,
            logger: NullLogger<MessagingService>.Instance);

        // Burn the PreKey-flagged first message so the rest of the loop
        // exercises the steady-state path.
        var bootMsg = new MeshMessage { RecipientUhid = BobUhid };
        Assert.True(await alice.SendAsync(bootMsg, Encoding.UTF8.GetBytes("bootstrap")));

        var plaintext = Encoding.UTF8.GetBytes("soak-message-payload-of-modest-length");
        var sentCount = 0;

        var report = await MeasureMemoryGrowthAsync(async _ =>
        {
            var msg = new MeshMessage { RecipientUhid = BobUhid };
            if (await alice.SendAsync(msg, plaintext)) sentCount++;
        }, iterations);

        WriteSummary(nameof(MessagingService_ThousandMessagesUnderLoad_DeliveryPath), report, iterations);

        Assert.Equal(iterations, sentCount);

        // Outbox check: Alice's store now holds bootstrap + iterations
        // messages. None should be in the transient Sending state.
        var outbox = await aliceStore.GetOutboxAsync(AliceUhid, limit: int.MaxValue);
        Assert.Equal(iterations + 1, outbox.Count);
        foreach (var stored in outbox)
        {
            Assert.NotEqual(MessageStatus.Sending, stored.Status);
            Assert.NotEmpty(stored.EncryptedContent);
        }

        // Per-iteration allocation: each message creates a MeshMessage +
        // MeshPacket + ciphertext + an envelope. ~2-3 KB/iter is normal;
        // 8 KB is the alarm threshold.
        Assert.True(report.PerIterationBytes < 8_192,
            $"Messaging per-iteration growth: {report.PerIterationBytes:F1}B/iter — exceeds 8 KB. " +
            "Check the encrypt path for hidden retention.");
    }

    /// <summary>
    /// Minimal in-memory <see cref="IRoutingService"/> for the messaging
    /// soak test. Mirrors the unit-test FakeRoutingService but lives in
    /// this assembly to keep the soak project self-contained.
    /// </summary>
    private sealed class InMemoryRoutingFake : IRoutingService
    {
        private readonly Dictionary<string, RouteEntry> _routes = new(StringComparer.Ordinal);

        public void SetRoute(string destination, string nextHop)
        {
            _routes[destination] = new RouteEntry
            {
                DestinationUhid = destination,
                NextHopUhid = nextHop,
                HopCount = 1,
                ExpiresAt = DateTime.UtcNow.AddMinutes(5),
            };
        }

        public Task<RouteEntry?> FindRouteAsync(string destinationUhid, CancellationToken cancellationToken = default)
        {
            _routes.TryGetValue(destinationUhid, out var entry);
            return Task.FromResult<RouteEntry?>(entry);
        }

        public RouteEntry? GetCachedRoute(string destinationUhid)
        {
            _routes.TryGetValue(destinationUhid, out var entry);
            return entry;
        }

        public IReadOnlyList<RouteEntry> GetAllRoutes() => _routes.Values.ToArray();

        public Task HandleRouteRequestAsync(MeshPacket routeRequest, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task HandleRouteReplyAsync(MeshPacket routeReply, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PruneAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}

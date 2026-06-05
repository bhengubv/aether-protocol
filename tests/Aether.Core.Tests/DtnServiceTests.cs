// SPDX-License-Identifier: MIT

using System.Text.Json;
using AetherMesh.Constants;
using AetherMesh.Core.Tests.Fakes;
using AetherMesh.Dtn;
using AetherMesh.Models;
using AetherMesh.Protocol;
using Xunit;

namespace AetherMesh.Core.Tests;

public class DtnServiceTests
{
    private const string Local = "local-uhid";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static (DtnService svc, FakeMeshSender sender, InMemoryDtnBundleStore store) NewService(
        string localUhid = Local,
        string? localGeohash = null)
    {
        var sender = new FakeMeshSender(localUhid, localGeohash);
        var store = new InMemoryDtnBundleStore();
        var svc = new DtnService(sender, store);
        return (svc, sender, store);
    }

    private static MeshPacket BuildBundlePacketFor(DtnBundle bundle, string sourceUhid)
    {
        return new MeshPacket
        {
            Type = PacketType.DtnBundle,
            SourceUhid = sourceUhid,
            DestinationUhid = bundle.RecipientUhid,
            Payload = JsonSerializer.SerializeToUtf8Bytes(bundle, JsonOptions),
            Ttl = ProtocolConstants.DtnTtl,
        };
    }

    // ─── CreateBundle ──────────────────────────────────────────

    [Fact]
    public async Task CreateBundle_PersistsBundleAndAttemptsDelivery()
    {
        var (svc, _, store) = NewService();

        var bundle = await svc.CreateBundleAsync("recipient", new byte[] { 1, 2, 3 });

        Assert.NotNull(bundle);
        Assert.Equal("recipient", bundle.RecipientUhid);
        Assert.Equal(BundleStatus.Pending, bundle.Status); // no peer connected, stayed pending
        Assert.Single(await store.GetActiveAsync());
    }

    [Fact]
    public async Task CreateBundle_WithDirectPeer_DeliversImmediately()
    {
        var (svc, sender, store) = NewService();
        sender.AddPeer(new PeerInfo
        {
            Uhid = "recipient",
            Capabilities = NodeCapabilities.DtnCarrier,
        });

        var bundle = await svc.CreateBundleAsync("recipient", new byte[] { 1, 2, 3 });

        Assert.Equal(BundleStatus.Delivered, bundle.Status);
        // Bundle was sent unicast to the recipient.
        Assert.Contains(sender.Unicasts, u => u.NextHopUhid == "recipient");
    }

    // ─── HandleAsync — DtnBundle ──────────────────────────────

    [Fact]
    public async Task HandleAsync_AsRecipient_MarksDeliveredAndSendsReceipt()
    {
        var (svc, sender, store) = NewService();

        var bundle = new DtnBundle
        {
            SenderUhid = "sender",
            RecipientUhid = Local,
            EncryptedPayload = new byte[] { 9 },
            Priority = BundlePriority.Normal,
        };
        await svc.HandleAsync(BuildBundlePacketFor(bundle, "sender"));

        var stored = await store.GetAsync(bundle.Id);
        Assert.NotNull(stored);
        Assert.Equal(BundleStatus.Delivered, stored!.Status);

        Assert.Contains(sender.Unicasts, u =>
            u.Packet.Type == PacketType.DtnDeliveryReceipt && u.NextHopUhid == "sender");
    }

    [Fact]
    public async Task HandleAsync_NotRecipientWithCapacity_AcceptsCustody()
    {
        var (svc, sender, store) = NewService();

        var bundle = new DtnBundle
        {
            SenderUhid = "alice",
            RecipientUhid = "bob",
            EncryptedPayload = new byte[] { 1 },
        };
        await svc.HandleAsync(BuildBundlePacketFor(bundle, "alice"));

        var stored = await store.GetAsync(bundle.Id);
        Assert.NotNull(stored);
        Assert.Equal(BundleStatus.InCustody, stored!.Status);
        Assert.Equal(1, stored.HopCount);

        // Acknowledge custody back to the previous hop.
        var ack = sender.Unicasts.FirstOrDefault(u =>
            u.Packet.Type == PacketType.DtnCustodyAck && u.NextHopUhid == "alice");
        Assert.NotNull(ack.Packet);
    }

    [Fact]
    public async Task HandleAsync_AtCapacity_RefusesCustody()
    {
        var (svc, sender, store) = NewService();
        // Pre-fill the store to exactly the cap.
        for (var i = 0; i < ProtocolConstants.DtnMaxBundlesPerNode; i++)
        {
            await store.SaveAsync(new DtnBundle
            {
                SenderUhid = "x",
                RecipientUhid = "y",
                Status = BundleStatus.InCustody,
                ExpiresAt = DateTime.UtcNow.AddHours(1),
            });
        }
        sender.Clear();

        var newBundle = new DtnBundle { SenderUhid = "alice", RecipientUhid = "bob" };
        await svc.HandleAsync(BuildBundlePacketFor(newBundle, "alice"));

        // Should send a custody-ack with accepted=false.
        var ack = sender.Unicasts.FirstOrDefault(u => u.Packet.Type == PacketType.DtnCustodyAck);
        Assert.NotNull(ack.Packet);
        var ackBody = JsonSerializer.Deserialize<JsonElement>(ack.Packet.Payload, JsonOptions);
        Assert.False(ackBody.GetProperty("accepted").GetBoolean());
    }

    // ─── HandleAsync — DtnCustodyAck ──────────────────────────

    [Fact]
    public async Task HandleAsync_PositiveCustodyAck_IncrementsCopyCount()
    {
        var (svc, _, store) = NewService();

        var bundle = await svc.CreateBundleAsync("recipient", new byte[] { 1 });
        var initialCopies = bundle.CopyCount;

        var ackPayload = JsonSerializer.SerializeToUtf8Bytes(
            new { bundle_id = bundle.Id, accepted = true }, JsonOptions);
        await svc.HandleAsync(new MeshPacket
        {
            Type = PacketType.DtnCustodyAck,
            SourceUhid = "carrier",
            DestinationUhid = Local,
            Payload = ackPayload,
        });

        var stored = await store.GetAsync(bundle.Id);
        Assert.Equal(initialCopies + 1, stored!.CopyCount);
    }

    [Fact]
    public async Task HandleAsync_NegativeCustodyAck_DoesNotIncrement()
    {
        var (svc, _, store) = NewService();
        var bundle = await svc.CreateBundleAsync("recipient", new byte[] { 1 });
        var initialCopies = bundle.CopyCount;

        var ackPayload = JsonSerializer.SerializeToUtf8Bytes(
            new { bundle_id = bundle.Id, accepted = false }, JsonOptions);
        await svc.HandleAsync(new MeshPacket
        {
            Type = PacketType.DtnCustodyAck,
            SourceUhid = "carrier",
            DestinationUhid = Local,
            Payload = ackPayload,
        });

        var stored = await store.GetAsync(bundle.Id);
        Assert.Equal(initialCopies, stored!.CopyCount);
    }

    // ─── HandleAsync — DtnDeliveryReceipt ────────────────────

    [Fact]
    public async Task HandleAsync_DeliveryReceipt_MarksBundleDeliveredAndFiresEvent()
    {
        var (svc, _, store) = NewService();
        var bundle = await svc.CreateBundleAsync("recipient", new byte[] { 1 });

        DtnDeliveryReceipt? observed = null;
        svc.BundleDelivered += (_, r) => observed = r;

        var receipt = new DtnDeliveryReceipt
        {
            BundleId = bundle.Id,
            RecipientUhid = "recipient",
            TotalHops = 3,
            TotalCustodyTransfers = 2,
        };
        await svc.HandleAsync(new MeshPacket
        {
            Type = PacketType.DtnDeliveryReceipt,
            SourceUhid = "recipient",
            DestinationUhid = Local,
            Payload = JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions),
        });

        var stored = await store.GetAsync(bundle.Id);
        Assert.Equal(BundleStatus.Delivered, stored!.Status);
        Assert.NotNull(observed);
        Assert.Equal(3, observed!.TotalHops);
    }

    // ─── ExpireStale ─────────────────────────────────────────

    [Fact]
    public async Task ExpireStaleAsync_FlipsStatusForExpiredBundles()
    {
        var (svc, _, store) = NewService();
        await store.SaveAsync(new DtnBundle
        {
            SenderUhid = "alice",
            RecipientUhid = "bob",
            ExpiresAt = DateTime.UtcNow.AddMinutes(-5),
            Status = BundleStatus.Pending,
        });
        var freshId = Guid.NewGuid();
        await store.SaveAsync(new DtnBundle
        {
            Id = freshId,
            SenderUhid = "alice",
            RecipientUhid = "bob",
            ExpiresAt = DateTime.UtcNow.AddHours(1),
            Status = BundleStatus.Pending,
        });

        var expired = await svc.ExpireStaleAsync();

        Assert.Equal(1, expired);
        var fresh = await store.GetAsync(freshId);
        Assert.Equal(BundleStatus.Pending, fresh!.Status);
    }

    // ─── RunDeliveryScan ─────────────────────────────────────

    [Fact]
    public async Task RunDeliveryScan_ReplicatesToEligibleCarriers()
    {
        var (svc, sender, store) = NewService();
        sender.AddPeer(new PeerInfo
        {
            Uhid = "carrier-1",
            Capabilities = NodeCapabilities.DtnCarrier,
            ReliabilityScore = 0.9,
        });
        sender.AddPeer(new PeerInfo
        {
            Uhid = "carrier-2",
            Capabilities = NodeCapabilities.DtnCarrier,
            ReliabilityScore = 0.5,
        });

        var bundle = new DtnBundle
        {
            SenderUhid = Local,
            RecipientUhid = "far-recipient",
            EncryptedPayload = new byte[] { 7 },
            Priority = BundlePriority.Normal,
            Status = BundleStatus.Pending,
            CopyCount = 1,
            MaxCopies = 3,
        };
        await store.SaveAsync(bundle);

        await svc.RunDeliveryScanAsync();

        // Both carriers should have received a unicast.
        Assert.Contains(sender.Unicasts, u =>
            u.NextHopUhid == "carrier-1" && u.Packet.Type == PacketType.DtnBundle);
        Assert.Contains(sender.Unicasts, u =>
            u.NextHopUhid == "carrier-2" && u.Packet.Type == PacketType.DtnBundle);

        var stored = await store.GetAsync(bundle.Id);
        Assert.True(stored!.CopyCount >= 2);
    }
}

// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using AetherNet.Models;
using AetherNet.Privacy;
using AetherNet.Profiles;
using AetherNet.Protocol;
using AetherNet.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Unit tests for <see cref="ProfileService"/> (PacketType.ProfileSync). The profile payload is PII, so it
/// is sealed to the recipient via <see cref="IControlPayloadCipher"/> — the wire never carries cleartext,
/// and no session means no send. A reversible fake cipher stands in for the real Signal envelope here.
/// </summary>
public sealed class ProfileSyncTests
{
    private sealed class FakeMeshSender : IMeshSender
    {
        public string LocalUhid { get; set; } = "aether:local:01";
        public List<(MeshPacket Packet, string NextHop)> Sends { get; } = [];

        public IReadOnlyList<PeerInfo> GetConnectedPeers() => Array.Empty<PeerInfo>();
        public Task<bool> SendAsync(MeshPacket packet, string nextHopUhid, CancellationToken ct = default)
        {
            Sends.Add((packet, nextHopUhid));
            return Task.FromResult(true);
        }
        public Task<int> BroadcastAsync(MeshPacket packet, CancellationToken ct = default) => Task.FromResult(0);
    }

    // Reversible stand-in for the Signal envelope: XOR 0xFF is its own inverse, so the wire bytes are not
    // the plaintext yet round-trip exactly. (Real encryption is covered by SignalMessageEnvelopeCipherTests.)
    private sealed class FakeControlCipher : IControlPayloadCipher
    {
        public static byte[] Seal(byte[] data)
        {
            var r = new byte[data.Length];
            for (var i = 0; i < data.Length; i++) r[i] = (byte)(data[i] ^ 0xFF);
            return r;
        }

        public Task<byte[]?> EncryptAsync(string recipientUhid, byte[] plaintext, CancellationToken ct = default)
            => Task.FromResult<byte[]?>(Seal(plaintext));
        public Task<byte[]?> DecryptAsync(string senderUhid, byte[] ciphertext, CancellationToken ct = default)
            => Task.FromResult<byte[]?>(Seal(ciphertext));
        public bool HasSession(string peerUhid) => true;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static ProfileService Build(FakeMeshSender sender)
        => new(sender, new FakeControlCipher(), NullLogger<ProfileService>.Instance);

    private static MeshPacket ProfilePacket(string uhid, string name, string avatar, string status, long updatedAtMs) => new()
    {
        Type = PacketType.ProfileSync,
        SourceUhid = uhid,
        DestinationUhid = "aether:local:01",
        Payload = FakeControlCipher.Seal(JsonSerializer.SerializeToUtf8Bytes(new ProfileSyncPayload
        {
            Uhid = uhid,
            DisplayName = name,
            AvatarRef = avatar,
            StatusMessage = status,
            UpdatedAtMs = updatedAtMs,
        }, JsonOpts)),
    };

    // ─── Payload format parity (unchanged by encryption — the inner plaintext is still this JSON) ───

    [Theory]
    [InlineData("aether:alice:01", "Alice", "blake3:abc", "available", 1700000000000L,
        "{\"uhid\":\"aether:alice:01\",\"display_name\":\"Alice\",\"avatar_ref\":\"blake3:abc\",\"status_message\":\"available\",\"updated_at_ms\":1700000000000}")]
    [InlineData("n", "", "", "", 0L,
        "{\"uhid\":\"n\",\"display_name\":\"\",\"avatar_ref\":\"\",\"status_message\":\"\",\"updated_at_ms\":0}")]
    public void ProfileSyncPayload_SerializesToCanonicalBytes(
        string uhid, string name, string avatar, string status, long updatedAtMs, string expected)
    {
        var payload = new ProfileSyncPayload
        {
            Uhid = uhid,
            DisplayName = name,
            AvatarRef = avatar,
            StatusMessage = status,
            UpdatedAtMs = updatedAtMs,
        };
        var json = Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts));
        Assert.Equal(expected, json);
    }

    // ─── Sealed send ───

    [Fact]
    public async Task PublishProfileTo_SealsPayload_NoCleartextPiiOnWire()
    {
        var sender = new FakeMeshSender { LocalUhid = "aether:alice:01" };
        var svc = Build(sender);
        svc.SetLocalProfile("Alice", "blake3:abc", "available");

        var ok = await svc.PublishProfileToAsync("aether:bob:02");

        Assert.True(ok);
        var sent = Assert.Single(sender.Sends);
        Assert.Equal(PacketType.ProfileSync, sent.Packet.Type);
        Assert.Equal("aether:bob:02", sent.NextHop);

        // The wire payload is sealed — it is neither the plaintext JSON nor does it leak the PII.
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(svc.GetLocalProfile(), JsonOpts);
        Assert.NotEqual(plaintext, sent.Packet.Payload);
        Assert.DoesNotContain("Alice", Encoding.UTF8.GetString(sent.Packet.Payload));

        // …and it round-trips back to the real profile once opened.
        var opened = JsonSerializer.Deserialize<ProfileSyncPayload>(FakeControlCipher.Seal(sent.Packet.Payload), JsonOpts)!;
        Assert.Equal("aether:alice:01", opened.Uhid);
        Assert.Equal("Alice", opened.DisplayName);
    }

    [Fact]
    public async Task PublishProfileTo_WithNoSession_SendsNothing()
    {
        var sender = new FakeMeshSender { LocalUhid = "aether:alice:01" };
        var svc = new ProfileService(sender, new NullControlPayloadCipher(), NullLogger<ProfileService>.Instance);
        svc.SetLocalProfile("Alice", "blake3:abc", "available");

        var ok = await svc.PublishProfileToAsync("aether:bob:02");

        Assert.False(ok);
        Assert.Empty(sender.Sends); // never falls back to cleartext
    }

    // ─── Sealed receive ───

    [Fact]
    public async Task Handle_CachesPeerProfileAndRaisesEvent()
    {
        var svc = Build(new FakeMeshSender { LocalUhid = "aether:local:01" });
        ProfileSyncPayload? updated = null;
        svc.ProfileUpdated += (_, e) => updated = e;

        var ok = await svc.HandleAsync(ProfilePacket("aether:bob:02", "Bob", "blake3:xyz", "busy", 1700000000000L));

        Assert.True(ok);
        Assert.NotNull(updated);
        Assert.Equal("Bob", updated!.DisplayName);

        var cached = svc.GetProfile("aether:bob:02");
        Assert.NotNull(cached);
        Assert.Equal("busy", cached!.StatusMessage);
        Assert.Single(svc.GetKnownProfiles());
    }

    [Fact]
    public async Task Handle_RefreshesExistingProfile()
    {
        var svc = Build(new FakeMeshSender());
        await svc.HandleAsync(ProfilePacket("aether:bob:02", "Bob", "", "here", 1000L));
        await svc.HandleAsync(ProfilePacket("aether:bob:02", "Bob", "", "away", 2000L));

        var cached = svc.GetProfile("aether:bob:02");
        Assert.Equal("away", cached!.StatusMessage);
        Assert.Single(svc.GetKnownProfiles());
    }

    [Fact]
    public async Task Handle_OwnProfile_IsIgnored()
    {
        var svc = Build(new FakeMeshSender { LocalUhid = "aether:local:01" });
        var ok = await svc.HandleAsync(ProfilePacket("aether:local:01", "Me", "", "", 1L));
        Assert.False(ok);
        Assert.Empty(svc.GetKnownProfiles());
    }

    [Fact]
    public async Task Handle_WrongPacketType_ReturnsFalse()
    {
        var svc = Build(new FakeMeshSender());
        var pkt = ProfilePacket("aether:bob:02", "Bob", "", "", 1L);
        pkt.Type = PacketType.Data;
        Assert.False(await svc.HandleAsync(pkt));
    }

    [Fact]
    public async Task Handle_UndecryptablePayload_IsDropped()
    {
        // A receiver with no session cannot open the sealed payload — it is dropped, not cached.
        var svc = new ProfileService(new FakeMeshSender(), new NullControlPayloadCipher(), NullLogger<ProfileService>.Instance);
        var ok = await svc.HandleAsync(ProfilePacket("aether:bob:02", "Bob", "", "busy", 1L));
        Assert.False(ok);
        Assert.Empty(svc.GetKnownProfiles());
    }
}

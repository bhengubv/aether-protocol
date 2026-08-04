// SPDX-License-Identifier: MIT

using System.Text;
using AetherNet.Identity;
using AetherNet.Models;
using AetherNet.Privacy;
using AetherNet.Protocol;
using AetherNet.Routing;
using Xunit;

namespace AetherNet.Core.Tests.Identity;

/// <summary>
/// The ERID exchange composed end-to-end. The primitives were built and unit-tested in isolation but
/// never connected; the coordinator wires the transport (packet 56) to the session cipher + directory,
/// so an established peer learns our rotating routing key and can resolve our ERID — an outsider cannot.
/// </summary>
public sealed class EridExchangeCoordinatorTests
{
    private const long Now = 1_700_000_000L;

    private sealed class CapturingSender : IMeshSender
    {
        public CapturingSender(string uhid) => LocalUhid = uhid;
        public string LocalUhid { get; }
        public List<MeshPacket> Sends { get; } = [];
        public IReadOnlyList<PeerInfo> GetConnectedPeers() => Array.Empty<PeerInfo>();
        public Task<bool> SendAsync(MeshPacket packet, string nextHopUhid, CancellationToken ct = default)
        {
            Sends.Add(packet);
            return Task.FromResult(true);
        }
        public Task<int> BroadcastAsync(MeshPacket packet, CancellationToken ct = default) => Task.FromResult(0);
    }

    // Symmetric stand-in for a shared Signal session — XOR 0xFF seals on one node and opens on the other.
    // (Real X3DH/Double-Ratchet is covered by SignalMessageEnvelopeCipherTests + EridExchangeServiceTests.)
    private sealed class SymmetricCipher : IControlPayloadCipher
    {
        private readonly bool _hasSession;
        public SymmetricCipher(bool hasSession = true) => _hasSession = hasSession;
        private static byte[] Xor(byte[] d)
        {
            var r = new byte[d.Length];
            for (var i = 0; i < d.Length; i++) r[i] = (byte)(d[i] ^ 0xFF);
            return r;
        }
        public Task<byte[]?> EncryptAsync(string r, byte[] p, CancellationToken ct = default)
            => Task.FromResult<byte[]?>(_hasSession ? Xor(p) : null);
        public Task<byte[]?> DecryptAsync(string s, byte[] c, CancellationToken ct = default)
            => Task.FromResult<byte[]?>(Xor(c));
        public bool HasSession(string peer) => _hasSession;
    }

    private sealed record Node(EridExchangeCoordinator Coord, CapturingSender Sender, EridAnnounceService Announce, EridDirectory Dir, byte[] Key);

    private static Node MakeNode(string uhid, IControlPayloadCipher cipher)
    {
        var key = EphemeralRoutingId.DeriveRoutingKey(Encoding.UTF8.GetBytes("secret:" + uhid));
        var sender = new CapturingSender(uhid);
        var announce = new EridAnnounceService(sender);
        var dir = new EridDirectory(key);
        var coord = new EridExchangeCoordinator(announce, dir, cipher, key);
        return new Node(coord, sender, announce, dir, key);
    }

    [Fact]
    public async Task Announce_ThenOpen_LetsPeerResolveOurRotatingErid()
    {
        var cipher = new SymmetricCipher();
        var a = MakeNode("A", cipher);
        var b = MakeNode("B", cipher);

        var sent = await a.Coord.AnnounceToAsync("B");

        Assert.True(sent);
        var packet = Assert.Single(a.Sender.Sends);
        Assert.Equal(PacketType.EridAnnounce, packet.Type);
        Assert.Equal("A", packet.SourceUhid);
        Assert.NotEqual(a.Key, packet.Payload); // the routing key is sealed, not in the clear

        var learned = await b.Coord.ProcessInboundAsync("A", packet.Payload);

        Assert.True(learned);
        Assert.Equal(1, b.Dir.KnownPeerCount);
        // B (an established peer) can compute A's current ERID and reverse-resolve it.
        Assert.Equal(a.Dir.MyErid(Now), b.Dir.EridForPeer("A", Now));
        Assert.Equal("A", b.Dir.ResolvePeer(a.Dir.MyErid(Now), Now));
    }

    [Fact]
    public async Task InboundPacket_ViaAnnounceHandler_FlowsThroughToTheDirectory()
    {
        var cipher = new SymmetricCipher();
        var a = MakeNode("A", cipher);
        var b = MakeNode("B", cipher);

        await a.Coord.AnnounceToAsync("B");
        var packet = a.Sender.Sends[0];
        packet.SourceUhid = "A";

        // Feed the wire packet into B's transport — the coordinator's subscription must carry it through.
        await b.Announce.HandleAsync(packet);
        await Task.Delay(50); // let the event-driven inbound handler settle

        Assert.Equal(1, b.Dir.KnownPeerCount);
        Assert.Equal("A", b.Dir.ResolvePeer(a.Dir.MyErid(Now), Now));
    }

    [Fact]
    public async Task Announce_WithNoSession_SendsNothing()
    {
        var cipher = new SymmetricCipher(hasSession: false);
        var a = MakeNode("A", cipher);

        var sent = await a.Coord.AnnounceToAsync("B");

        Assert.False(sent);
        Assert.Empty(a.Sender.Sends); // the routing key never leaves except inside a session
    }
}

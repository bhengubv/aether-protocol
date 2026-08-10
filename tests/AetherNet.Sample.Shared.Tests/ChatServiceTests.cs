// SPDX-License-Identifier: MIT

using AetherNet.PreKeys;
using AetherNet.Sample.Shared.Data;
using AetherNet.Sample.Shared.Services;
using AetherNet.Security.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AetherNet.Sample.Shared.Tests;

/// <summary>
/// A radio that hands whatever one node sends straight to the other — the two phones, without the
/// Bluetooth. Everything above it (Signal, pre-key exchange, storage) is the real implementation.
/// </summary>
internal sealed class LoopbackRadio : IRadioMesh
{
    public LoopbackRadio? Peer { get; set; }

    public string LocalTag { get; set; } = "";
    public IReadOnlyList<RadioInfo> Radios { get; } = new[] { new RadioInfo("Loopback", true) };
    public string SelectedRadio => "Loopback";
    public bool IsSupported => true;
    public bool IsLinked => Peer is not null;
    public string? PeerTag => Peer?.LocalTag;
    public IReadOnlyList<string> Log { get; } = Array.Empty<string>();

    public event Action? Changed;
    public event Action<byte[]>? PacketReceived;

    public void SelectRadio(string name) { }
    public void Link() => Changed?.Invoke();
    public Task SendTestAsync(string text) => Task.CompletedTask;
    public void Stop() { }

    public Task<bool> SendPacketAsync(byte[] packetBytes)
    {
        if (Peer is null) return Task.FromResult(false);
        Peer.PacketReceived?.Invoke(packetBytes);
        return Task.FromResult(true);
    }
}

/// <summary>
/// Chat has to be real: encrypted with Signal, carried over the radio, and readable only by the
/// intended device. These drive two full nodes through a loopback radio, so a passing test means the
/// whole path works — X3DH over the mesh, double-ratchet encryption, delivery, storage.
/// </summary>
public sealed class ChatServiceTests
{
    private sealed record Node(ChatService Chat, AetherStore Store, IIdentityService Me, LoopbackRadio Radio);

    private static (Node A, Node B) Pair()
    {
        var radioA = new LoopbackRadio();
        var radioB = new LoopbackRadio();
        radioA.Peer = radioB;
        radioB.Peer = radioA;

        Node Build(LoopbackRadio radio)
        {
            var store = AetherStore.InMemory();
            var me = new IdentityService(new FakeVault(), store);
            radio.LocalTag = me.AetherTag;
            var signal = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);
            var preKeys = new PreKeyExchangeService(new RadioMeshSender(me.AetherTag, radio));
            // The pre-key exchange has its own packet types; the chat service pumps them in from the radio.
            return new Node(new ChatService(store, me, signal, preKeys, radio), store, me, radio);
        }

        return (Build(radioA), Build(radioB));
    }

    /// <summary>
    /// Kick off the handshake and wait for it. X3DH is a real round trip over the radio — request,
    /// reply, then the local key agreement — so it completes shortly after the call, not during it.
    /// </summary>
    private static async Task Connect(Node from, Node to)
    {
        await from.Chat.EnsureSessionAsync(to.Me.AetherTag);
        for (var i = 0; i < 100 && !from.Chat.IsSecure(to.Me.AetherTag); i++)
            await Task.Delay(20);
        Assert.True(from.Chat.IsSecure(to.Me.AetherTag), "the secure session never came up");
    }

    /// <summary>Give an in-flight message time to land on the other node.</summary>
    private static async Task Settle() => await Task.Delay(120);

    [Fact]
    public async Task Message_CrossesEncrypted_AndArrivesReadable()
    {
        var (a, b) = Pair();

        await Connect(a, b);
        await a.Chat.SendAsync(b.Me.AetherTag, "Power's out on our side too");
        await Settle();

        // B received and decrypted it.
        var received = b.Chat.Conversation(a.Me.AetherTag);
        Assert.Single(received);
        Assert.Equal("Power's out on our side too", received[0].Body);
        Assert.False(received[0].Mine);
        Assert.Equal(ChatMessage.Received, received[0].State);

        // A has it as sent, not stuck pending.
        var sent = a.Chat.Conversation(b.Me.AetherTag);
        Assert.Single(sent);
        Assert.True(sent[0].Mine);
        Assert.Equal(ChatMessage.Sent, sent[0].State);
    }

    [Fact]
    public async Task BothDirections_Work()
    {
        var (a, b) = Pair();

        await Connect(a, b);
        await a.Chat.SendAsync(b.Me.AetherTag, "you around?");
        await Settle();
        await b.Chat.SendAsync(a.Me.AetherTag, "ja I'm here");
        await Settle();

        Assert.Equal(2, a.Chat.Conversation(b.Me.AetherTag).Count);
        Assert.Equal(2, b.Chat.Conversation(a.Me.AetherTag).Count);
        Assert.Equal("ja I'm here", a.Chat.Conversation(b.Me.AetherTag)[^1].Body);
    }

    [Fact]
    public async Task WithoutASession_MessageIsHeldNotSentInTheClear()
    {
        var (a, b) = Pair();
        a.Radio.Peer = null;    // nothing can reach the other phone, so no session can form

        await a.Chat.SendAsync(b.Me.AetherTag, "secret");
        await Settle();

        var mine = a.Chat.Conversation(b.Me.AetherTag);
        Assert.Single(mine);
        Assert.Equal(ChatMessage.Pending, mine[0].State);   // held, never transmitted unprotected
        Assert.Empty(b.Chat.Conversation(a.Me.AetherTag));
    }

    [Fact]
    public async Task PendingMessages_FlushOnceTheSessionExists()
    {
        var (a, b) = Pair();
        var wire = a.Radio.Peer;
        a.Radio.Peer = null;

        await a.Chat.SendAsync(b.Me.AetherTag, "sent while offline");
        await Settle();
        Assert.Equal(ChatMessage.Pending, a.Chat.Conversation(b.Me.AetherTag)[0].State);

        a.Radio.Peer = wire;                                  // the phones come back into range
        await Connect(a, b);
        await a.Chat.FlushAsync(b.Me.AetherTag);
        await Settle();

        Assert.Equal(ChatMessage.Sent, a.Chat.Conversation(b.Me.AetherTag)[0].State);
        Assert.Equal("sent while offline", b.Chat.Conversation(a.Me.AetherTag)[0].Body);
    }

    [Fact]
    public async Task Session_IsSecureOnlyAfterTheHandshake()
    {
        var (a, b) = Pair();
        Assert.False(a.Chat.IsSecure(b.Me.AetherTag));

        await Connect(a, b);

        Assert.True(a.Chat.IsSecure(b.Me.AetherTag));
    }

    [Fact]
    public async Task Conversation_SurvivesRestart()
    {
        var (a, b) = Pair();
        await Connect(a, b);
        await a.Chat.SendAsync(b.Me.AetherTag, "keep me");
        await Settle();

        // A new ChatService over the same database is what a restart looks like.
        var reopened = new ChatService(
            a.Store, a.Me,
            new SignalProtocolService(NullLogger<SignalProtocolService>.Instance),
            new PreKeyExchangeService(new RadioMeshSender(a.Me.AetherTag, a.Radio)),
            a.Radio);

        Assert.Equal("keep me", reopened.Conversation(b.Me.AetherTag)[0].Body);
    }

    [Fact]
    public async Task ChatList_ShowsTheLatestMessagePerPerson()
    {
        var (a, b) = Pair();
        await Connect(a, b);
        await a.Chat.SendAsync(b.Me.AetherTag, "first");
        await Settle();
        await a.Chat.SendAsync(b.Me.AetherTag, "second");
        await Settle();

        var latest = a.Chat.Latest();
        Assert.Single(latest);
        Assert.Equal("second", latest[0].Body);
    }
}

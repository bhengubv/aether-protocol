// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Data;
using AetherNet.Sample.Shared.Services;
using AetherNet.Sample.Tests.Fakes;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// Handing out pre-key bundles, and why one per run is not enough.
///
/// <para>
/// A bundle carries a <b>one-time</b> pre-key. Establishing the responder side of a session consumes
/// it, and a second message naming the same id is refused outright — "already consumed, or never
/// generated". So a node that generates its bundle once at startup can complete exactly one handshake
/// with a given peer, ever. The first one works, and every repair after it dies at the far end.
/// </para>
///
/// <para>
/// That is what a P30 Lite and merlin were caught doing on 2026-08-13: dropping the broken session,
/// asking for a fresh bundle, getting the same stale one back, and repeating every forty seconds
/// without ever recovering. The bundle has to be fresh for each requester, which is also what a real
/// Signal server does — it holds many one-time keys and hands each out once.
/// </para>
/// </summary>
public class PreKeyFreshnessTests
{
    private const string Me = "KXJB7-MN2P4";
    private const string Them = "DY5CF-84G9T";

    private sealed class Rig : IDisposable
    {
        public AetherStore Store { get; } = AetherStore.InMemory();
        public FakeSignalProtocol Signal { get; } = new();
        public FakePreKeyExchange PreKeys { get; } = new();
        public FakeRadioMesh Radio { get; } = new(Me);
        public ChatService Chat { get; }

        public Rig() => Chat = new ChatService(Store, new FakeIdentity(Me), Signal, PreKeys, Radio);

        public void Dispose() => Store.Dispose();
    }

    private static async Task<bool> Eventually(Func<bool> condition, int withinMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(withinMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(10);
        }
        return condition();
    }

    // ── A bundle is published before anyone can ask ───────────────────────────

    [Fact]
    public async Task A_bundle_is_published_so_a_peer_can_start_a_session()
    {
        using var rig = new Rig();

        await rig.Chat.EnsureSessionAsync(Them);

        Assert.NotNull(rig.PreKeys.GetLocalBundle());
    }

    /// <summary>Ordinary traffic must not burn through one-time keys for no reason.</summary>
    [Fact]
    public async Task Sending_messages_does_not_keep_regenerating_the_bundle()
    {
        using var rig = new Rig();
        rig.Signal.OpenSessionWith(Them);
        rig.Radio.Link();

        await rig.Chat.SendAsync(Them, "one");
        await rig.Chat.SendAsync(Them, "two");
        await rig.Chat.SendAsync(Them, "three");

        Assert.Equal(1, rig.Signal.BundlesGenerated);
    }

    // ── Every repair gets a live one-time key ─────────────────────────────────

    /// <summary>Two phones, so the unreadable payload that triggers a repair is a real one.</summary>
    private sealed class Pair : IDisposable
    {
        public Rig A { get; } = new();
        public AetherStore StoreB { get; } = AetherStore.InMemory();
        public FakeSignalProtocol SignalB { get; } = new();
        public FakePreKeyExchange PreKeysB { get; } = new();
        public FakeRadioMesh RadioB { get; } = new(Them);
        public ChatService ChatB { get; }

        public Pair()
        {
            ChatB = new ChatService(StoreB, new FakeIdentity(Them), SignalB, PreKeysB, RadioB);
            A.Radio.Peer = RadioB;
            RadioB.Peer = A.Radio;
            A.Signal.OpenSessionWith(Them);
            SignalB.OpenSessionWith(Me);
            A.Radio.Link();
            RadioB.Link();
        }

        public void Dispose() { A.Dispose(); StoreB.Dispose(); }
    }

    /// <summary>
    /// The one this exists for. After a session is repaired, the peer will build a new session against
    /// our bundle — and if that bundle names a one-time key we already consumed, the peer's first
    /// message is refused and the repair achieves nothing.
    /// </summary>
    [Fact]
    public async Task A_repair_publishes_a_fresh_bundle()
    {
        using var pair = new Pair();
        await pair.A.Chat.EnsureSessionAsync(Them);
        var afterFirstHandshake = pair.A.Signal.BundlesGenerated;

        pair.A.Signal.RatchetBroken = true;
        await pair.ChatB.SendAsync(Me, "A will not be able to open this");

        Assert.True(await Eventually(() => pair.A.Signal.BundlesGenerated > afterFirstHandshake),
            "the repair offers the peer a one-time key that has already been used");
    }

    /// <summary>
    /// The peer answers a bundle request by handing over what we published. That copy has to be the
    /// fresh one, or the requester builds a session against a one-time key that is already spent.
    /// </summary>
    [Fact]
    public async Task The_published_bundle_is_the_fresh_one_after_a_repair()
    {
        using var pair = new Pair();
        await pair.A.Chat.EnsureSessionAsync(Them);
        var first = pair.A.PreKeys.GetLocalBundle();

        pair.A.Signal.RatchetBroken = true;
        await pair.ChatB.SendAsync(Me, "A will not be able to open this");
        await Eventually(() => pair.A.Signal.BundlesGenerated > 1);

        Assert.NotSame(first, pair.A.PreKeys.GetLocalBundle());
    }
}

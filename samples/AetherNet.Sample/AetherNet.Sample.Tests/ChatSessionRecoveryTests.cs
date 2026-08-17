// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Data;
using AetherNet.Sample.Shared.Services;
using AetherNet.Sample.Tests.Fakes;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// What the chat layer does when the secure session with someone has quietly stopped working.
///
/// <para>
/// The failure is silent by nature: the link is up, packets arrive, and every one of them fails its
/// authentication tag because the two ratchets no longer agree. Left alone the conversation never
/// recovers — the broken session is exactly what stops a fresh handshake being adopted, so it blocks
/// its own repair. Two phones sat like that for an afternoon on 2026-08-13 over a link that was fine.
/// </para>
///
/// <para>
/// <see cref="SessionRepair"/> holds the rules. These are about the chat layer following them: letting
/// the dead session go, leading or waiting as its own tag decides, and getting the backlog moving once
/// a new session is up.
/// </para>
/// </summary>
public class ChatSessionRecoveryTests
{
    /// <summary>Ordinally below <see cref="Higher"/>, so a phone with this tag leads the handshake.</summary>
    private const string Lower = "AAAAA-AAAAA";
    private const string Higher = "ZZZZZ-ZZZZZ";

    /// <summary>
    /// Two phones on one radio, each with its own everything — the only way to produce a payload that
    /// is genuinely unreadable rather than one hand-built to look that way.
    /// </summary>
    private sealed class Pair : IDisposable
    {
        public AetherStore StoreA { get; } = AetherStore.InMemory();
        public AetherStore StoreB { get; } = AetherStore.InMemory();
        public FakeSignalProtocol SignalA { get; } = new();
        public FakeSignalProtocol SignalB { get; } = new();
        public FakePreKeyExchange PreKeysA { get; } = new();
        public FakePreKeyExchange PreKeysB { get; } = new();
        public FakeRadioMesh RadioA { get; }
        public FakeRadioMesh RadioB { get; }
        public ChatService ChatA { get; }
        public ChatService ChatB { get; }
        public string TagA { get; }
        public string TagB { get; }

        public Pair(string tagA, string tagB)
        {
            TagA = tagA;
            TagB = tagB;
            RadioA = new FakeRadioMesh(tagA);
            RadioB = new FakeRadioMesh(tagB);
            ChatA = new ChatService(StoreA, new FakeIdentity(tagA), SignalA, PreKeysA, RadioA);
            ChatB = new ChatService(StoreB, new FakeIdentity(tagB), SignalB, PreKeysB, RadioB);

            RadioA.Peer = RadioB;
            RadioB.Peer = RadioA;

            SignalA.OpenSessionWith(tagB);
            SignalB.OpenSessionWith(tagA);
            RadioA.Link();
            RadioB.Link();
        }

        /// <summary>
        /// A's ratchet silently diverges from B's: B keeps sending, and nothing B sends can be read.
        /// </summary>
        public Pair WithBrokenRatchetOnA()
        {
            SignalA.RatchetBroken = true;
            return this;
        }

        /// <summary>B says something. A will not be able to open it.</summary>
        public Task BSpeaksAsync(string body = "can you hear me") => ChatB.SendAsync(TagA, body);

        public void Dispose() { StoreA.Dispose(); StoreB.Dispose(); }
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

    // ── The dead session goes ─────────────────────────────────────────────────

    [Fact]
    public async Task An_unreadable_message_throws_the_session_away()
    {
        using var pair = new Pair(Lower, Higher).WithBrokenRatchetOnA();

        await pair.BSpeaksAsync();

        Assert.True(await Eventually(() => pair.SignalA.Dropped.Contains(Higher)),
            "the session that cannot read anything is still in place, blocking its own replacement");
    }

    [Fact]
    public async Task A_readable_message_leaves_the_session_alone()
    {
        using var pair = new Pair(Lower, Higher);

        await pair.BSpeaksAsync();
        await Task.Delay(200);

        Assert.Empty(pair.SignalA.Dropped);
    }

    [Fact]
    public async Task An_unreadable_receipt_throws_the_session_away()
    {
        using var pair = new Pair(Lower, Higher);

        // A speaks while everything works, then loses the ability to read B's reply — which is the
        // receipt for what A just sent.
        pair.SignalA.RatchetBroken = true;
        await pair.ChatA.SendAsync(Higher, "did you get this");

        Assert.True(await Eventually(() => pair.SignalA.Dropped.Contains(Higher)));
    }

    // ── The phone that cannot read is the one that fixes it ───────────────────

    /// <summary>
    /// The peer is still sending happily and sees nothing wrong, so nobody is coming to help. Whichever
    /// phone noticed has to be the one that asks — whatever its tag happens to be.
    /// </summary>
    [Theory]
    [InlineData(Lower, Higher)]
    [InlineData(Higher, Lower)]
    public async Task The_phone_that_cannot_read_asks_for_a_fresh_bundle(string mine, string theirs)
    {
        using var pair = new Pair(mine, theirs).WithBrokenRatchetOnA();

        await pair.BSpeaksAsync();

        Assert.True(await Eventually(() => pair.PreKeysA.Requested.Contains(theirs)),
            "the only phone that knows the session is broken deferred to one that does not");
    }

    [Theory]
    [InlineData(Lower, Higher)]
    [InlineData(Higher, Lower)]
    public async Task The_phone_that_can_read_perfectly_well_does_nothing(string mine, string theirs)
    {
        using var pair = new Pair(mine, theirs).WithBrokenRatchetOnA();

        await pair.BSpeaksAsync();
        await Task.Delay(300);

        Assert.Empty(pair.SignalB.Dropped);
    }

    // ── And the conversation gets moving again ────────────────────────────────

    [Fact]
    public async Task The_backlog_goes_out_once_the_new_session_is_up()
    {
        using var pair = new Pair(Lower, Higher).WithBrokenRatchetOnA();
        await pair.ChatA.SendAsync(Higher, "still owed");

        await pair.BSpeaksAsync();
        await Eventually(() => pair.SignalA.Dropped.Contains(Higher));

        pair.SignalA.RatchetBroken = false;
        pair.PreKeysA.RaiseBundleReceived(Higher);   // their fresh bundle comes back over the radio

        Assert.True(await Eventually(() =>
            pair.StoreA.GetMessages(Higher).Any(m => m.Mine && m.State != ChatMessage.Pending)),
            "a new session came up and what was waiting never went");
    }

    /// <summary>
    /// Six unreadable payloads inside two seconds is what the P30 actually saw. Each one must not tear
    /// the session down again and hand the peer another half-built replacement.
    /// </summary>
    [Fact]
    public async Task A_burst_of_unreadable_payloads_repairs_once()
    {
        using var pair = new Pair(Lower, Higher).WithBrokenRatchetOnA();

        for (var i = 0; i < 6; i++) await pair.BSpeaksAsync($"number {i}");
        await Task.Delay(400);

        Assert.Equal(1, pair.SignalA.Dropped.Count(t => t == Higher));
    }
}

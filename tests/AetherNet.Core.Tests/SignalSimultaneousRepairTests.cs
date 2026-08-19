// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;
using AetherNet.Security.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Two devices that rebuild their session at the same moment must still be able to talk.
///
/// <para>
/// One session per peer only holds while one side at a time rebuilds. On real hardware both sides
/// discover they cannot read within milliseconds of each other, both fetch the other's pre-key
/// bundle, and both become X3DH <i>initiator</i>. Each one's opening message then replaces what the
/// other has just built, and because the two arrive in different orders on the two phones, the pair
/// can settle on different ratchets — which fails every message on its authentication tag and looks
/// exactly like broken crypto. It is not: it is two correct ratchets for one pair.
/// </para>
///
/// <para>
/// These tests pin the behaviour that ends it — a node keeps the ratchets it has replaced, opens a
/// message under whichever one the sender actually used, and adopts that one for its replies.
/// </para>
/// </summary>
public class SignalSimultaneousRepairTests
{
    private const string Alice = "alice-uhid-simultaneous";
    private const string Bob = "bob-uhid-simultaneous";

    private static SignalProtocolService Build(string localUhid)
    {
        var service = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);
        service.SetLocalUhid(localUhid);
        return service;
    }

    /// <summary>Establish in one direction: <paramref name="initiator"/> adopts the responder's bundle.</summary>
    private static async Task EstablishAsync(
        SignalProtocolService initiator, string initiatorUhid,
        SignalProtocolService responder, string responderUhid)
    {
        var bundle = await responder.GeneratePreKeyBundleAsync(responderUhid);
        await initiator.ProcessPreKeyBundleAsync(bundle);

        // The opening message is what puts the responder on the same ratchet.
        var opening = await initiator.EncryptAsync(responderUhid, Encoding.UTF8.GetBytes("hello"));
        await responder.DecryptAsync(initiatorUhid, opening);
    }

    private static async Task<string> SayAsync(
        SignalProtocolService from, string toUhid,
        SignalProtocolService to, string fromUhid,
        string text)
    {
        var sealed_ = await from.EncryptAsync(toUhid, Encoding.UTF8.GetBytes(text));
        return Encoding.UTF8.GetString(await to.DecryptAsync(fromUhid, sealed_));
    }

    // ── the race itself ────────────────────────────────────────────────────

    /// <summary>
    /// The failure seen on two phones: both sides repair, both become initiator, and the ratchets
    /// they end up holding disagree. Every later message must still open.
    /// </summary>
    [Fact]
    public async Task Both_sides_rebuilding_at_once_can_still_talk()
    {
        var alice = Build(Alice);
        var bob = Build(Bob);
        await EstablishAsync(alice, Alice, bob, Bob);
        Assert.Equal("first", await SayAsync(alice, Bob, bob, Alice, "first"));

        // Both find they cannot read and both go for a fresh bundle — the case that used to leave
        // the pair permanently unable to talk.
        alice.DropSession(Bob);
        bob.DropSession(Alice);

        var bobBundle = await bob.GeneratePreKeyBundleAsync(Bob);
        var aliceBundle = await alice.GeneratePreKeyBundleAsync(Alice);
        await alice.ProcessPreKeyBundleAsync(bobBundle);     // Alice: initiator on ratchet A
        await bob.ProcessPreKeyBundleAsync(aliceBundle);     // Bob:   initiator on ratchet B

        // Their opening messages cross. Each one replaces the ratchet the other just built.
        var aliceOpening = await alice.EncryptAsync(Bob, Encoding.UTF8.GetBytes("mine"));
        var bobOpening = await bob.EncryptAsync(Alice, Encoding.UTF8.GetBytes("mine"));
        await bob.DecryptAsync(Alice, aliceOpening);
        await alice.DecryptAsync(Bob, bobOpening);

        // Whichever ratchet each side settled on, the conversation continues.
        Assert.Equal("after the crash", await SayAsync(alice, Bob, bob, Alice, "after the crash"));
        Assert.Equal("and back", await SayAsync(bob, Alice, alice, Bob, "and back"));
    }

    /// <summary>
    /// The same race, with the crossing messages arriving in the opposite order. Convergence must
    /// not depend on who happened to be heard first.
    /// </summary>
    [Fact]
    public async Task Both_sides_rebuilding_at_once_can_still_talk_whichever_arrives_first()
    {
        var alice = Build(Alice);
        var bob = Build(Bob);
        await EstablishAsync(alice, Alice, bob, Bob);

        alice.DropSession(Bob);
        bob.DropSession(Alice);
        await alice.ProcessPreKeyBundleAsync(await bob.GeneratePreKeyBundleAsync(Bob));
        await bob.ProcessPreKeyBundleAsync(await alice.GeneratePreKeyBundleAsync(Alice));

        var aliceOpening = await alice.EncryptAsync(Bob, Encoding.UTF8.GetBytes("mine"));
        var bobOpening = await bob.EncryptAsync(Alice, Encoding.UTF8.GetBytes("mine"));
        await alice.DecryptAsync(Bob, bobOpening);           // reversed against the test above
        await bob.DecryptAsync(Alice, aliceOpening);

        Assert.Equal("still fine", await SayAsync(bob, Alice, alice, Bob, "still fine"));
        Assert.Equal("both ways", await SayAsync(alice, Bob, bob, Alice, "both ways"));
    }

    /// <summary>
    /// A voice call is what exposed this: signalling rides the ratchet, so an offer and its answer
    /// are two messages in opposite directions with no chat traffic in between to converge things.
    /// </summary>
    [Fact]
    public async Task A_call_offer_and_its_answer_both_open_after_a_crossed_repair()
    {
        var alice = Build(Alice);
        var bob = Build(Bob);
        await EstablishAsync(alice, Alice, bob, Bob);

        alice.DropSession(Bob);
        bob.DropSession(Alice);
        await alice.ProcessPreKeyBundleAsync(await bob.GeneratePreKeyBundleAsync(Bob));
        await bob.ProcessPreKeyBundleAsync(await alice.GeneratePreKeyBundleAsync(Alice));
        await bob.DecryptAsync(Alice, await alice.EncryptAsync(Bob, Encoding.UTF8.GetBytes("x")));
        await alice.DecryptAsync(Bob, await bob.EncryptAsync(Alice, Encoding.UTF8.GetBytes("x")));

        Assert.Equal("offer", await SayAsync(alice, Bob, bob, Alice, "offer"));
        Assert.Equal("answer", await SayAsync(bob, Alice, alice, Bob, "answer"));
        Assert.Equal("media-key", await SayAsync(alice, Bob, bob, Alice, "media-key"));
    }

    // ── keeping a replaced ratchet ─────────────────────────────────────────

    /// <summary>
    /// A message already in flight on the old ratchet when this side rebuilt must still open. It
    /// was sent before the peer knew anything had changed.
    /// </summary>
    [Fact]
    public async Task A_message_sent_before_this_side_rebuilt_still_opens()
    {
        var alice = Build(Alice);
        var bob = Build(Bob);
        await EstablishAsync(alice, Alice, bob, Bob);

        // Alice speaks on the ratchet they share, and it is still in the air.
        var inFlight = await alice.EncryptAsync(Bob, Encoding.UTF8.GetBytes("in flight"));

        // Bob rebuilds in the meantime.
        bob.DropSession(Alice);
        await bob.ProcessPreKeyBundleAsync(await alice.GeneratePreKeyBundleAsync(Alice));
        await alice.DecryptAsync(Bob, await bob.EncryptAsync(Alice, Encoding.UTF8.GetBytes("new")));

        Assert.Equal("in flight",
            Encoding.UTF8.GetString(await bob.DecryptAsync(Alice, inFlight)));
    }

    /// <summary>
    /// Reading on a replaced ratchet is not enough — the reply has to go back on that same ratchet,
    /// or the peer cannot read it and the two sides trade repairs indefinitely.
    /// </summary>
    [Fact]
    public async Task Replying_after_a_replaced_ratchet_opened_goes_back_on_that_ratchet()
    {
        var alice = Build(Alice);
        var bob = Build(Bob);
        await EstablishAsync(alice, Alice, bob, Bob);

        var onOldRatchet = await alice.EncryptAsync(Bob, Encoding.UTF8.GetBytes("still here"));

        bob.DropSession(Alice);
        await bob.ProcessPreKeyBundleAsync(await alice.GeneratePreKeyBundleAsync(Alice));
        await alice.DecryptAsync(Bob, await bob.EncryptAsync(Alice, Encoding.UTF8.GetBytes("new")));

        await bob.DecryptAsync(Alice, onOldRatchet);

        // Bob answers. Alice never saw Bob move, so this only opens if Bob went back to hers.
        Assert.Equal("got it", await SayAsync(bob, Alice, alice, Bob, "got it"));
    }

    // ── the limits of it ───────────────────────────────────────────────────

    /// <summary>
    /// Only ratchets this pair actually built are tried. A payload from somewhere else still fails,
    /// rather than being quietly attributed to one of them.
    /// </summary>
    [Fact]
    public async Task A_payload_from_a_stranger_still_fails()
    {
        var alice = Build(Alice);
        var bob = Build(Bob);
        var mallory = Build("mallory-uhid-simultaneous");
        await EstablishAsync(alice, Alice, bob, Bob);
        await EstablishAsync(mallory, "mallory-uhid-simultaneous", bob, Bob);

        var notForAlice = await mallory.EncryptAsync(Bob, Encoding.UTF8.GetBytes("wrong ratchet"));

        // Offered as though Alice had sent it.
        await Assert.ThrowsAnyAsync<CryptographicException>(
            () => bob.DecryptAsync(Alice, notForAlice));
    }

    /// <summary>
    /// A tampered payload fails on every ratchet held, and having several to try does not turn a
    /// forgery into a message.
    /// </summary>
    [Fact]
    public async Task A_tampered_payload_fails_on_every_ratchet_held()
    {
        var alice = Build(Alice);
        var bob = Build(Bob);
        await EstablishAsync(alice, Alice, bob, Bob);

        bob.DropSession(Alice);
        await bob.ProcessPreKeyBundleAsync(await alice.GeneratePreKeyBundleAsync(Alice));
        await alice.DecryptAsync(Bob, await bob.EncryptAsync(Alice, Encoding.UTF8.GetBytes("new")));

        var good = await alice.EncryptAsync(Bob, Encoding.UTF8.GetBytes("genuine"));
        var ciphertext = (byte[])good.Ciphertext.Clone();
        ciphertext[0] ^= 0xFF;
        var tampered = good with { Ciphertext = ciphertext };

        await Assert.ThrowsAnyAsync<CryptographicException>(() => bob.DecryptAsync(Alice, tampered));
    }

    /// <summary>
    /// Dropping a session drops it. Keeping replaced ratchets must not resurrect one the caller
    /// deliberately threw away.
    /// </summary>
    [Fact]
    public async Task Dropping_a_session_does_not_leave_a_readable_ratchet_behind()
    {
        var alice = Build(Alice);
        var bob = Build(Bob);
        await EstablishAsync(alice, Alice, bob, Bob);

        var beforeDrop = await alice.EncryptAsync(Bob, Encoding.UTF8.GetBytes("before"));
        bob.DropSession(Alice);

        Assert.False(bob.HasSession(Alice));
        await Assert.ThrowsAnyAsync<Exception>(() => bob.DecryptAsync(Alice, beforeDrop));
    }

    /// <summary>
    /// The radio delivers the same opening message twice. The one-time pre-key it names is spent, so
    /// it cannot be replayed — and it must not take the working session down with it either. An
    /// attempt that failed halfway through a ratchet step used to leave the session unable to read
    /// anything afterwards, which turned one duplicated frame into a dead conversation.
    /// </summary>
    [Fact]
    public async Task A_repeated_opening_message_is_refused_without_breaking_the_session()
    {
        var alice = Build(Alice);
        var bob = Build(Bob);

        var bundle = await bob.GeneratePreKeyBundleAsync(Bob);
        await alice.ProcessPreKeyBundleAsync(bundle);

        var opening = await alice.EncryptAsync(Bob, Encoding.UTF8.GetBytes("hello"));
        Assert.Equal("hello", Encoding.UTF8.GetString(await bob.DecryptAsync(Alice, opening)));

        // A second delivery of the very same message is a replay, and is refused.
        await Assert.ThrowsAnyAsync<CryptographicException>(() => bob.DecryptAsync(Alice, opening));

        // The conversation carries on as though the duplicate had never arrived.
        Assert.Equal("after the duplicate", await SayAsync(alice, Bob, bob, Alice, "after the duplicate"));
    }

    /// <summary>Ordinary back-and-forth is untouched by any of this.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(10)]
    [InlineData(50)]
    public async Task An_undisturbed_conversation_still_works(int turns)
    {
        var alice = Build(Alice);
        var bob = Build(Bob);
        await EstablishAsync(alice, Alice, bob, Bob);

        for (var i = 0; i < turns; i++)
        {
            Assert.Equal($"a{i}", await SayAsync(alice, Bob, bob, Alice, $"a{i}"));
            Assert.Equal($"b{i}", await SayAsync(bob, Alice, alice, Bob, $"b{i}"));
        }
    }
}

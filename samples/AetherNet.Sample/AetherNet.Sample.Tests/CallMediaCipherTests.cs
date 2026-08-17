// SPDX-License-Identifier: MIT

using System.Text;
using AetherNet.Sample.Shared.Services;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// Encrypting voice with one key per call instead of a ratchet per frame.
///
/// <para>
/// Voice first went through the Signal double ratchet exactly like a chat message, and failed on the
/// first real call — <c>payload would not open</c>, for every single frame. A ratchet advances per
/// message and tolerates a small reordering window; fifty frames a second over a lossy radio outrun it
/// within a second and the two ends never recover. Watched on device 2026-08-17: the answering phone
/// streamed happily while the caller could not open one frame of it.
/// </para>
///
/// <para>
/// So the properties that matter here are the ones the ratchet could not give: frames must open
/// <b>out of order</b>, a lost frame must cost only itself, and the two directions must never share a
/// key and nonce.
/// </para>
/// </summary>
public class CallMediaCipherTests
{
    private static (CallMediaCipher Caller, CallMediaCipher Answerer) APair()
    {
        var master = CallMediaCipher.NewMasterKey();
        return (new CallMediaCipher(master, iAmTheCaller: true),
                new CallMediaCipher(master, iAmTheCaller: false));
    }

    private static byte[] AFrame(string what = "twenty milliseconds of speech") =>
        Encoding.UTF8.GetBytes(what);

    // ── It carries voice both ways ────────────────────────────────────────────

    [Fact]
    public void A_frame_sealed_by_the_caller_opens_for_the_answerer()
    {
        var (caller, answerer) = APair();

        var opened = answerer.Open(caller.Seal(AFrame()));

        Assert.Equal(AFrame(), opened);
    }

    [Fact]
    public void A_frame_sealed_by_the_answerer_opens_for_the_caller()
    {
        var (caller, answerer) = APair();

        var opened = caller.Open(answerer.Seal(AFrame("their reply")));

        Assert.Equal(AFrame("their reply"), opened);
    }

    [Fact]
    public void A_sealed_frame_does_not_contain_the_audio_it_came_from()
    {
        var (caller, _) = APair();
        var frame = AFrame();

        var sealedFrame = caller.Seal(frame);

        Assert.DoesNotContain(Encoding.UTF8.GetString(frame),
            Encoding.UTF8.GetString(sealedFrame), StringComparison.Ordinal);
    }

    // ── The properties a ratchet could not give ───────────────────────────────

    /// <summary>
    /// The whole reason this class exists. On a mesh, frames arrive out of order — and every one of
    /// them still has to play.
    /// </summary>
    [Fact]
    public void Frames_open_out_of_order()
    {
        var (caller, answerer) = APair();
        var sealed1 = caller.Seal(AFrame("one"));
        var sealed2 = caller.Seal(AFrame("two"));
        var sealed3 = caller.Seal(AFrame("three"));

        // Deliberately backwards.
        Assert.Equal(AFrame("three"), answerer.Open(sealed3));
        Assert.Equal(AFrame("two"), answerer.Open(sealed2));
        Assert.Equal(AFrame("one"), answerer.Open(sealed1));
    }

    /// <summary>
    /// A dropped frame must cost that frame and nothing else. Under the ratchet it cost the call.
    /// </summary>
    [Fact]
    public void A_lost_frame_does_not_take_the_rest_of_the_call_with_it()
    {
        var (caller, answerer) = APair();

        answerer.Open(caller.Seal(AFrame("heard")));
        caller.Seal(AFrame("lost on the air"));          // sealed, never delivered
        var after = answerer.Open(caller.Seal(AFrame("still heard")));

        Assert.Equal(AFrame("still heard"), after);
    }

    [Fact]
    public void A_hundred_frames_in_a_row_all_open()
    {
        var (caller, answerer) = APair();
        var opened = 0;

        for (var i = 0; i < 100; i++)
            if (answerer.Open(caller.Seal(AFrame($"frame {i}"))) is not null) opened++;

        Assert.Equal(100, opened);
    }

    // ── Nobody else gets to speak ─────────────────────────────────────────────

    [Fact]
    public void A_frame_from_a_different_call_does_not_open()
    {
        var (caller, _) = APair();
        var (_, strangerAnswerer) = APair();          // a different master key entirely

        Assert.Null(strangerAnswerer.Open(caller.Seal(AFrame())));
    }

    /// <summary>
    /// The counter travels in the clear so a frame can be opened without its predecessors. Authenticating
    /// it as associated data is what stops it being rewritten in flight to pass one frame off as another.
    /// </summary>
    [Fact]
    public void A_frame_whose_counter_was_tampered_with_does_not_open()
    {
        var (caller, answerer) = APair();
        var sealedFrame = caller.Seal(AFrame());

        sealedFrame[0] ^= 0xFF;

        Assert.Null(answerer.Open(sealedFrame));
    }

    [Fact]
    public void A_frame_whose_audio_was_tampered_with_does_not_open()
    {
        var (caller, answerer) = APair();
        var sealedFrame = caller.Seal(AFrame());

        sealedFrame[^1] ^= 0xFF;

        Assert.Null(answerer.Open(sealedFrame));
    }

    /// <summary>
    /// Without this, anything recorded off the air could be played back into a live call.
    /// </summary>
    [Fact]
    public void A_frame_replayed_from_far_back_is_refused()
    {
        var (caller, answerer) = APair();
        var early = caller.Seal(AFrame("recorded earlier"));

        // Move the call well past it, then try the old frame again.
        for (var i = 0; i < 200; i++) answerer.Open(caller.Seal(AFrame($"frame {i}")));

        Assert.Null(answerer.Open(early));
    }

    /// <summary>
    /// Two directions must never seal under the same key and nonce — that is the one thing AES-GCM
    /// cannot survive. Both sides sealing their first frame is exactly when it would happen.
    /// </summary>
    [Fact]
    public void The_two_directions_do_not_produce_the_same_bytes_for_the_same_frame()
    {
        var (caller, answerer) = APair();

        Assert.NotEqual(caller.Seal(AFrame()), answerer.Seal(AFrame()));
    }

    /// <summary>A phone must not be able to open the frames it sent — that would mean one key both ways.</summary>
    [Fact]
    public void A_phone_cannot_open_its_own_frames()
    {
        var (caller, _) = APair();

        Assert.Null(caller.Open(caller.Seal(AFrame())));
    }

    // ── Keys ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Every_call_gets_a_different_key() =>
        Assert.NotEqual(CallMediaCipher.NewMasterKey(), CallMediaCipher.NewMasterKey());

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(33)]
    public void A_key_of_the_wrong_length_is_refused(int bytes) =>
        Assert.Throws<ArgumentException>(() => new CallMediaCipher(new byte[bytes], true));

    [Fact]
    public void Rubbish_instead_of_a_frame_opens_to_nothing_rather_than_throwing()
    {
        var (_, answerer) = APair();

        Assert.Null(answerer.Open(new byte[] { 1, 2, 3 }));
    }
}

// SPDX-License-Identifier: MIT

extern alias service;

using ChatMessage = service::AetherNetNodeService.Host.Data.ChatMessage;
using AetherNet.Sample.Shared.Data;
using AetherNet.Sample.Shared.Services;
using AetherNet.Sample.Tests.Fakes;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// A note — somebody talking — going from one phone to another and back out of the other end.
///
/// <para>
/// This is the feature the measured radio actually allows. A call needs fifty packets a second in
/// each direction and Bluetooth manages nine in one (PROTOCOL_SPEC §5.5); a ten-second voice note is
/// about ten kilobytes and crosses the same link in seven seconds. Nobody is waiting on it in real
/// time, so slow is simply slow rather than broken.
/// </para>
///
/// <para>
/// The bytes and the message travel separately and that is the whole design: the message names the
/// note by content hash and appears at once, while the note itself arrives in chunks, resumably. So
/// what has to be pinned is that the naming survives the wire — a note whose message arrives without
/// its hash is a permanently blank bubble, and one whose hash arrives without the message never
/// appears at all.
/// </para>
/// </summary>
public class VoiceNoteTests
{
    // ── Naming the container ───────────────────────────────────────────────

    /// <summary>
    /// The extension has to match the bytes. A player handed an Opus stream named .mp4 refuses to open
    /// it, and the note then looks corrupt when it is perfectly fine.
    /// </summary>
    [Theory]
    [InlineData(ChatMessage.VoiceNote, "note.ogg")]
    [InlineData(ChatMessage.VoiceNoteAac, "note.m4a")]
    [InlineData(ChatMessage.VideoNote, "note.mp4")]
    public void A_recording_is_named_after_its_container(string contentType, string expected)
        => Assert.Equal(expected, new RecordedNote([1, 2, 3], contentType, TimeSpan.FromSeconds(2)).SuggestedName);

    // ── The type has to be one a player accepts ────────────────────────────

    /// <summary>And the extension has to match the container, or the platform refuses to open it.</summary>
    [Theory]
    [InlineData(ChatMessage.VoiceNote, ".ogg")]
    [InlineData(ChatMessage.VoiceNoteAac, ".m4a")]
    [InlineData(ChatMessage.VideoNote, ".mp4")]
    public void The_name_agrees_with_the_type(string contentType, string extension)
        => Assert.EndsWith(extension, new RecordedNote([1, 2, 3], contentType, TimeSpan.FromSeconds(2)).SuggestedName);

    // ── A host that cannot record ──────────────────────────────────────────

    /// <summary>
    /// The web head has no microphone. It says so, and every recording call comes back empty rather
    /// than starting something that produces nothing — the same failure mode as a call that connects
    /// and stays silent, and worth making impossible rather than discoverable.
    /// </summary>
    [Fact]
    public async Task A_host_with_no_microphone_says_so()
    {
        var capture = new NullMediaCapture();

        Assert.False(capture.CanRecordVoice);
        Assert.False(capture.CanRecordVideo);
        Assert.NotNull(capture.UnavailableReason);
        Assert.False(await capture.EnsurePermissionAsync(video: false));
        Assert.False(await capture.StartVoiceAsync());
        Assert.Null(await capture.StopVoiceAsync());
        Assert.Null(await capture.RecordVideoAsync());
    }

    /// <summary>
    /// The cap is the radio's, not a preference. A minute of voice is about 180 KB, which is minutes
    /// of transfer on the slow link — past that people assume the note failed.
    /// </summary>
    [Fact]
    public void A_note_is_capped_at_a_length_the_radio_can_carry()
    {
        var capture = new NullMediaCapture();

        Assert.True(capture.MaxDuration <= TimeSpan.FromMinutes(1));
        Assert.True(capture.MinDuration > TimeSpan.Zero);
        Assert.True(capture.MinDuration < capture.MaxDuration);
    }
}

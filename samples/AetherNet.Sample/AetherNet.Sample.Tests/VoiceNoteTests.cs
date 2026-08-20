// SPDX-License-Identifier: MIT

using AetherNet.Content;
using AetherNet.Content.Models;
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
    private const string Me = "KXJB7-MN2P4";
    private const string Them = "DY5CF-84G9T";

    private static byte[] Clip(int bytes)
    {
        var b = new byte[bytes];
        for (var i = 0; i < bytes; i++) b[i] = (byte)(i * 37 % 253);
        return b;
    }

    // ── The header on the wire ─────────────────────────────────────────────

    [Fact]
    public void A_note_survives_being_written_and_read()
    {
        var original = new AttachmentRef("abc123", ChatMessage.VoiceNote, 9_001);

        var (back, caption) = AttachmentRef.Decode(original.Encode());

        Assert.Equal(original, back);
        Assert.Equal("", caption);
    }

    [Fact]
    public void A_note_can_carry_words_as_well()
    {
        var original = new AttachmentRef("deadbeef", ChatMessage.VideoNote, 42);

        var (back, caption) = AttachmentRef.Decode(original.Encode("listen to this"));

        Assert.Equal(original, back);
        Assert.Equal("listen to this", caption);
    }

    /// <summary>
    /// The property the whole encoding rests on: a message with nothing attached goes out exactly as
    /// it always did. Text is untouched by this and cannot be broken by it.
    /// </summary>
    [Theory]
    [InlineData("no tower no wifi")]
    [InlineData("")]
    [InlineData("a message with  a unit separator in it")]
    [InlineData("{\"looks\":\"like json\"}")]
    public void Plain_text_is_read_back_unchanged(string text)
    {
        var (attachment, caption) = AttachmentRef.Decode(text);

        Assert.Null(attachment);
        Assert.Equal(text, caption);
    }

    /// <summary>
    /// This arrived from a radio, so it gets parsed like input, not like data. Anything malformed
    /// comes back as nothing — showing the raw innards of a corrupt header as if they were somebody's
    /// words is worse than showing an empty message.
    /// </summary>
    [Theory]
    [InlineData("never closed")]
    [InlineData("onlytwo")]
    [InlineData("abcd")]
    [InlineData("audio/opus10")]          // no hash
    [InlineData("abc10")]                  // no type
    [InlineData("abcaudio/opusnotanumber")]
    [InlineData("abcaudio/opus-5")]        // a note cannot be negative
    public void A_malformed_header_yields_nothing_rather_than_half_a_note(string body)
    {
        var (attachment, caption) = AttachmentRef.Decode(body);

        Assert.Null(attachment);
        Assert.Equal("", caption);
    }

    /// <summary>
    /// And nobody can type a header. The marker is stripped from outgoing text, which is what lets the
    /// parse above trust its very first character.
    /// </summary>
    [Fact]
    public void The_marker_is_taken_out_of_anything_typed()
    {
        var typed = "abcaudio/opus10pretending to be a note";

        var cleaned = AttachmentRef.Clean(typed);

        Assert.DoesNotContain("", cleaned, StringComparison.Ordinal);
        Assert.Null(AttachmentRef.Decode(cleaned).Attachment);
    }

    // ── Two phones ─────────────────────────────────────────────────────────

    private sealed class Pair : IDisposable
    {
        public AetherStore StoreA { get; } = AetherStore.InMemory();
        public AetherStore StoreB { get; } = AetherStore.InMemory();
        public FakeSignalProtocol SignalA { get; } = new();
        public FakeSignalProtocol SignalB { get; } = new();
        public FakeRadioMesh RadioA { get; } = new(Me);
        public FakeRadioMesh RadioB { get; } = new(Them);
        public AttachmentService AttachmentsA { get; }
        public AttachmentService AttachmentsB { get; }
        public IContentStore ContentB { get; } = new InMemoryContentStore();
        public ChatService ChatA { get; }
        public ChatService ChatB { get; }

        public Pair()
        {
            var meA = new FakeIdentity(Me);
            var meB = new FakeIdentity(Them);

            AttachmentsA = new AttachmentService(meA, SignalA, new InMemoryContentStore(), RadioA);
            AttachmentsB = new AttachmentService(meB, SignalB, ContentB, RadioB);

            ChatA = new ChatService(StoreA, meA, SignalA, new FakePreKeyExchange(), RadioA, AttachmentsA);
            ChatB = new ChatService(StoreB, meB, SignalB, new FakePreKeyExchange(), RadioB, AttachmentsB);

            RadioA.Peer = RadioB;
            RadioB.Peer = RadioA;

            SignalA.OpenSessionWith(Them);
            SignalB.OpenSessionWith(Me);
            RadioA.Link();
            RadioB.Link();
        }

        public void Dispose()
        {
            AttachmentsA.Dispose();
            AttachmentsB.Dispose();
            StoreA.Dispose();
            StoreB.Dispose();
        }
    }

    [Fact]
    public async Task A_note_appears_in_my_own_conversation_at_once()
    {
        using var pair = new Pair();

        var sent = await pair.ChatA.SendNoteAsync(Them, Clip(4_000), ChatMessage.VoiceNote, "note.ogg");

        Assert.True(sent);
        var mine = Assert.Single(pair.StoreA.GetMessages(Them));
        Assert.True(mine.IsVoiceNote);
        Assert.Equal(4_000, mine.AttachmentBytes);
        Assert.NotNull(mine.AttachmentHash);
    }

    /// <summary>
    /// The message reaches them naming the note. Whether the bytes have finished crossing is a
    /// separate question with its own answer — this is the one that decides whether a bubble appears.
    /// </summary>
    [Fact]
    public async Task The_other_phone_learns_there_is_a_note()
    {
        using var pair = new Pair();

        await pair.ChatA.SendNoteAsync(Them, Clip(4_000), ChatMessage.VoiceNote, "note.ogg");

        var theirs = Assert.Single(pair.StoreB.GetMessages(Me));
        Assert.True(theirs.HasAttachment);
        Assert.True(theirs.IsVoiceNote);
        Assert.Equal(4_000, theirs.AttachmentBytes);
        Assert.Equal(pair.StoreA.GetMessages(Them)[0].AttachmentHash, theirs.AttachmentHash);
    }

    /// <summary>And the bytes really arrive, verified chunk by chunk against the manifest.</summary>
    [Fact]
    public async Task The_note_itself_arrives_and_is_the_same_note()
    {
        using var pair = new Pair();
        var clip = Clip(10_000);

        await pair.ChatA.SendNoteAsync(Them, clip, ChatMessage.VoiceNote, "note.ogg");

        var hash = pair.StoreB.GetMessages(Me)[0].AttachmentHash!;
        var got = await pair.AttachmentsB.GetAsync(hash);

        Assert.Equal(clip, got);
    }

    [Fact]
    public async Task A_video_note_travels_the_same_way()
    {
        using var pair = new Pair();

        await pair.ChatA.SendNoteAsync(Them, Clip(20_000), ChatMessage.VideoNote, "note.mp4");

        var theirs = Assert.Single(pair.StoreB.GetMessages(Me));
        Assert.True(theirs.IsVideoNote);
        Assert.False(theirs.IsVoiceNote);
    }

    [Fact]
    public async Task A_note_can_be_sent_with_words()
    {
        using var pair = new Pair();

        await pair.ChatA.SendNoteAsync(Them, Clip(2_000), ChatMessage.VoiceNote, "note.ogg", "about tonight");

        Assert.Equal("about tonight", pair.StoreB.GetMessages(Me)[0].Body);
    }

    /// <summary>Nothing recorded is nothing sent — never an empty bubble naming nothing.</summary>
    [Theory]
    [InlineData(0)]
    public async Task Nothing_recorded_sends_nothing(int size)
    {
        using var pair = new Pair();

        var sent = await pair.ChatA.SendNoteAsync(Them, Clip(size), ChatMessage.VoiceNote, "note.ogg");

        Assert.False(sent);
        Assert.Empty(pair.StoreA.GetMessages(Them));
    }

    /// <summary>
    /// A host with no way to move bytes says so instead of storing a message that names content that
    /// will never move. The web head is exactly this.
    /// </summary>
    [Fact]
    public async Task A_host_with_no_transport_refuses_rather_than_pretending()
    {
        using var store = AetherStore.InMemory();
        var chat = new ChatService(store, new FakeIdentity(Me), new FakeSignalProtocol(), new FakePreKeyExchange());

        var sent = await chat.SendNoteAsync(Them, Clip(1_000), ChatMessage.VoiceNote, "note.ogg");

        Assert.False(sent);
        Assert.Empty(store.GetMessages(Them));
    }

    /// <summary>
    /// A note recorded with no session yet waits, exactly as a typed message does, and is not lost.
    /// The commonest way to send a first note is to record it before anyone has ever spoken.
    /// </summary>
    [Fact]
    public async Task A_note_with_no_session_yet_waits_rather_than_vanishing()
    {
        using var store = AetherStore.InMemory();
        var me = new FakeIdentity(Me);
        var signal = new FakeSignalProtocol();
        var radio = new FakeRadioMesh(Me);
        using var attachments = new AttachmentService(me, signal, new InMemoryContentStore(), radio);
        var chat = new ChatService(store, me, signal, new FakePreKeyExchange(), radio, attachments);

        var sent = await chat.SendNoteAsync(Them, Clip(1_000), ChatMessage.VoiceNote, "note.ogg");

        Assert.True(sent);
        var pending = Assert.Single(store.GetMessages(Them));
        Assert.Equal(ChatMessage.Pending, pending.State);
        Assert.True(pending.HasAttachment);
    }

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

    /// <summary>Both containers are voice notes. A phone too old for Opus still sends and plays one.</summary>
    [Theory]
    [InlineData(ChatMessage.VoiceNote)]
    [InlineData(ChatMessage.VoiceNoteAac)]
    public void Both_audio_containers_count_as_a_voice_note(string contentType)
    {
        var m = new ChatMessage("id", Them, "", true, ChatMessage.Sent, 1,
            AttachmentHash: "abc", AttachmentType: contentType, AttachmentBytes: 10);

        Assert.True(m.IsVoiceNote);
        Assert.False(m.IsVideoNote);
    }

    // ── A stalled transfer has to resume ───────────────────────────────────

    /// <summary>
    /// A note whose chunks stopped arriving must start again when the link comes back.
    ///
    /// <para>
    /// It did not. <c>ResumeAsync</c> existed, documented as "call when a link comes back", and had no
    /// callers anywhere — so a transfer that stalled stayed stalled forever. Watched on device
    /// 2026-08-20: 29 chunks asked for, the link dropped and re-established twice, and ten minutes
    /// later not one chunk had moved.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_stalled_note_resumes_when_the_link_comes_back()
    {
        using var pair = new Pair();
        var clip = Clip(30_000);

        // Send with the radio down, so the offer goes nowhere and nothing transfers.
        pair.RadioA.Unlink();
        pair.RadioB.Unlink();
        await pair.ChatA.SendNoteAsync(Them, clip, ChatMessage.VoiceNote, "note.ogg");

        // The link returning is the only prompt there is.
        pair.RadioA.Link();
        pair.RadioB.Link();
        await Task.Delay(200);

        var hash = ContentDescriptor.FromBytes("note.ogg", clip, ChatMessage.VoiceNote, AttachmentService.ChunkBytes).RootHash;
        Assert.Equal(clip, await pair.AttachmentsB.GetAsync(hash));
    }

    // ── The type has to be one a player accepts ────────────────────────────

    /// <summary>
    /// A note is handed to a media element as <c>data:{ContentType};base64,...</c>, so the type must
    /// be the CONTAINER, not the codec.
    ///
    /// <para>
    /// This was <c>audio/opus</c>, which no browser accepts — Opus is the codec and Ogg is the file.
    /// The note transferred, verified, and was acknowledged, then sat in the conversation as a player
    /// reading "0:00 / 0:00" with 91 KB of good audio behind it. Nothing failed anywhere; it simply
    /// could not be played.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(ChatMessage.VoiceNote)]
    [InlineData(ChatMessage.VoiceNoteAac)]
    [InlineData(ChatMessage.VideoNote)]
    public void Every_note_type_is_a_container_a_player_understands(string contentType)
    {
        string[] playable = ["audio/ogg", "audio/mpeg", "audio/mp4", "audio/wav", "audio/webm", "video/mp4", "video/webm"];

        Assert.Contains(contentType, playable);
    }

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

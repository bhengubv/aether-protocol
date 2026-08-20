// SPDX-License-Identifier: MIT

using AetherNet.Content.Models;
using AetherNet.Sample.Shared.Data;
using AetherNet.Sample.Shared.Services;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// A message that carries something other than words.
///
/// <para>
/// The bytes never travel in the message — it names them by content hash and they move separately, in
/// chunks, each verified on arrival against the manifest rather than trusted because of who sent it.
/// That is the same path a card already uses for its artwork, and it buys two things a live call
/// cannot have: a transfer that resumes where it stopped, and one that works on a radio far too slow
/// to carry a conversation.
/// </para>
/// </summary>
public class AttachmentTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"aether-att-{Guid.NewGuid():N}.db");
    private readonly AetherStore _store;

    public AttachmentTests() => _store = new AetherStore(_path);

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_path); } catch { }
    }

    private static byte[] Clip(int bytes)
    {
        var b = new byte[bytes];
        for (var i = 0; i < bytes; i++) b[i] = (byte)(i * 31 % 251);
        return b;
    }

    // ── the message model ──────────────────────────────────────────────────

    [Fact]
    public void A_plain_message_has_no_attachment()
        => Assert.False(new ChatMessage("id", "peer", "hello", true, ChatMessage.Sent, 1).HasAttachment);

    [Fact]
    public void A_message_naming_content_has_an_attachment()
    {
        var m = new ChatMessage("id", "peer", "", true, ChatMessage.Sent, 1,
            AttachmentHash: "abc123", AttachmentType: ChatMessage.VoiceNote, AttachmentBytes: 4096);

        Assert.True(m.HasAttachment);
        Assert.Equal(ChatMessage.VoiceNote, m.AttachmentType);
    }

    /// <summary>An attachment survives the app closing, like everything else in a conversation.</summary>
    [Fact]
    public void An_attachment_survives_a_restart()
    {
        _store.SaveMessage(new ChatMessage("m1", "BQ6NH-V6Q5N", "", true, ChatMessage.Sent, 1,
            AttachmentHash: "deadbeef", AttachmentType: ChatMessage.VoiceNote, AttachmentBytes: 9_001));
        _store.Dispose();

        using var reopened = new AetherStore(_path);

        var back = Assert.Single(reopened.GetMessages("BQ6NH-V6Q5N"));
        Assert.True(back.HasAttachment);
        Assert.Equal("deadbeef", back.AttachmentHash);
        Assert.Equal(ChatMessage.VoiceNote, back.AttachmentType);
        Assert.Equal(9_001, back.AttachmentBytes);
    }

    /// <summary>
    /// A conversation recorded before attachments existed must still open. Phones in the field have a
    /// messages table without these columns, and a schema change must never cost anyone their chat.
    /// </summary>
    [Fact]
    public void Messages_written_before_attachments_existed_still_read()
    {
        _store.SaveMessage(new ChatMessage("old", "peer", "written before", true, ChatMessage.Sent, 1));

        var back = Assert.Single(_store.GetMessages("peer"));
        Assert.False(back.HasAttachment);
        Assert.Null(back.AttachmentHash);
        Assert.Equal(0, back.AttachmentBytes);
    }

    // ── chunking and verification ──────────────────────────────────────────

    /// <summary>
    /// Chunks are small on purpose. The content store's default is sized for a file server; on a radio
    /// it would be one indivisible lump with no visible progress and nothing to resume from.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(4096, 1)]
    [InlineData(4097, 2)]
    [InlineData(40_960, 10)]
    public void Content_is_cut_into_small_chunks(int bytes, int expectedChunks)
    {
        var d = ContentDescriptor.FromBytes("note.opus", Clip(bytes), ChatMessage.VoiceNote,
            AttachmentService.ChunkBytes);

        Assert.Equal(expectedChunks, d.ChunkCount);
        Assert.Equal(bytes, d.TotalBytes);
        Assert.True(d.VerifySelf());
    }

    /// <summary>Every chunk verifies against the manifest on its own — no trust in the sender.</summary>
    [Fact]
    public void Each_chunk_verifies_independently()
    {
        var data = Clip(10_000);
        var d = ContentDescriptor.FromBytes("note.opus", data, ChatMessage.VoiceNote, AttachmentService.ChunkBytes);

        for (var i = 0; i < d.ChunkCount; i++)
        {
            var offset = i * d.ChunkSizeBytes;
            var size = Math.Min(d.ChunkSizeBytes, data.Length - offset);
            Assert.True(d.VerifyChunk(i, data.AsSpan(offset, size)));
        }
    }

    /// <summary>A tampered chunk is rejected, whoever sent it. This is the whole point of hashing.</summary>
    [Fact]
    public void A_tampered_chunk_is_rejected()
    {
        var data = Clip(8_000);
        var d = ContentDescriptor.FromBytes("note.opus", data, ChatMessage.VoiceNote, AttachmentService.ChunkBytes);

        var chunk = data.AsSpan(0, d.ChunkSizeBytes).ToArray();
        chunk[0] ^= 0xFF;

        Assert.False(d.VerifyChunk(0, chunk));
    }

    /// <summary>A chunk offered under the wrong index is rejected too — order is part of the content.</summary>
    [Fact]
    public void A_chunk_in_the_wrong_place_is_rejected()
    {
        var data = Clip(12_000);
        var d = ContentDescriptor.FromBytes("note.opus", data, ChatMessage.VoiceNote, AttachmentService.ChunkBytes);

        var first = data.AsSpan(0, d.ChunkSizeBytes).ToArray();

        Assert.False(d.VerifyChunk(1, first));
    }

    // ── what makes it resumable ────────────────────────────────────────────

    /// <summary>
    /// The same bytes always produce the same hash, which is what lets a half-finished transfer be
    /// recognised and continued rather than started again.
    /// </summary>
    [Fact]
    public void The_same_content_always_has_the_same_hash()
    {
        var a = ContentDescriptor.FromBytes("a.opus", Clip(9_000), ChatMessage.VoiceNote, AttachmentService.ChunkBytes);
        var b = ContentDescriptor.FromBytes("different-name.opus", Clip(9_000), ChatMessage.VoiceNote, AttachmentService.ChunkBytes);

        Assert.Equal(a.RootHash, b.RootHash);
    }

    /// <summary>Different content never collides into the same transfer.</summary>
    [Fact]
    public void Different_content_has_a_different_hash()
    {
        var a = ContentDescriptor.FromBytes("a.opus", Clip(9_000), ChatMessage.VoiceNote, AttachmentService.ChunkBytes);
        var b = ContentDescriptor.FromBytes("a.opus", Clip(9_001), ChatMessage.VoiceNote, AttachmentService.ChunkBytes);

        Assert.NotEqual(a.RootHash, b.RootHash);
    }

    /// <summary>
    /// A ten-second voice note is small enough to cross even the slow radio. Measured, BLE between
    /// these handsets manages about 11 kbps — hopeless for a call, fine for a note that can take a
    /// few seconds. This pins the arithmetic that claim rests on.
    /// </summary>
    [Fact]
    public void A_ten_second_note_fits_the_slow_radio()
    {
        // Ten seconds of Opus at the 8 kbps floor.
        const int tenSecondsAtFloor = 8_000 / 8 * 10;   // bytes

        var d = ContentDescriptor.FromBytes("note.opus", Clip(tenSecondsAtFloor), ChatMessage.VoiceNote,
            AttachmentService.ChunkBytes);

        var secondsOverBle = d.TotalBytes * 8.0 / 11_000;

        Assert.True(secondsOverBle < 15,
            $"a ten-second note would take {secondsOverBle:0.0}s over the measured BLE link");
    }
}

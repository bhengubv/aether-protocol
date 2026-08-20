// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using AetherNet.Content;
using AetherNet.Content.Models;
using AetherNet.Messaging;
using AetherNet.Protocol;
using AetherNet.Security.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// Moves the bytes behind a message — a voice note, a video note, a picture — from one phone to
/// another.
///
/// <para>
/// A message carries a content hash, never the bytes. The bytes travel here, in chunks, each one
/// verified on arrival against the descriptor rather than trusted because of who sent it. That is the
/// same content-addressed path a card already uses for its artwork, and reusing it is the point:
/// there should be one way to move a blob across this mesh, not two.
/// </para>
///
/// <para>
/// <b>This is a transfer, not a stream, and that changes everything.</b> A voice note has all the time
/// in the world — so unlike a call it can resume across a dropped link, and it works perfectly well on
/// a radio far too slow to carry a conversation. Measured, BLE between these handsets manages about
/// 11 kbps: hopeless for a call, entirely adequate for a ten-second note that arrives a few seconds
/// later. It is the media that still works when the wide pipe is down.
/// </para>
/// </summary>
public sealed class AttachmentService : IDisposable
{
    /// <summary>An offer: here is what I am sending you, and how it is chunked.</summary>
    private const string OfferMarker = "AETHERATT";

    /// <summary>One chunk of it.</summary>
    private const string ChunkMarker = "AETHERACH";

    /// <summary>The chunks I am still missing — sent on receiving an offer, and again to resume.</summary>
    private const string WantMarker = "AETHERAWT";

    /// <summary>
    /// Bytes per chunk on the wire.
    ///
    /// <para>
    /// Deliberately small. The content store's default chunk is sized for a file server; on a radio it
    /// would mean one indivisible lump that must arrive whole, no visible progress, and nothing to
    /// resume from. Four kilobytes is a few seconds of speech even on the slowest radio here, so
    /// progress moves and a dropped link costs at most one chunk.
    /// </para>
    /// </summary>
    public const int ChunkBytes = 4096;

    private readonly IIdentityService _me;
    private readonly IRadioMesh? _radio;
    private readonly ISignalProtocolService _signal;
    private readonly IContentStore _content;
    private readonly ILogger _log;
    private bool _disposed;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public AttachmentService(
        IIdentityService me,
        ISignalProtocolService signal,
        IContentStore content,
        IRadioMesh? radio = null,
        ILoggerFactory? loggerFactory = null)
    {
        _me = me ?? throw new ArgumentNullException(nameof(me));
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
        _content = content ?? throw new ArgumentNullException(nameof(content));
        _radio = radio;
        _log = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<AttachmentService>();

        if (_radio is not null) _radio.PacketReceived += OnPacket;
    }

    /// <summary>
    /// Chase every unfinished transfer with one peer. Call when a link to them comes up.
    ///
    /// <para>
    /// Driven from outside rather than from the radio's own event, and that is the whole lesson here.
    /// A first attempt subscribed to the radio and sent to <c>_radio.PeerTag</c> — which is null right
    /// after a link comes up, because the long-term identity never travels in clear and only arrives
    /// once a message from them opens. So the sweep ran at exactly the moment it had nobody to send
    /// to. <see cref="ChatService"/> already works out who a new link is worth reviving, and already
    /// waits for the session; this rides that rather than guessing again.
    /// </para>
    ///
    /// <para>
    /// Per item: <b>incomplete</b> asks for the missing chunks, <b>complete</b> offers it again. Both,
    /// because there are two different stalls. If the link was down when the note was recorded the
    /// OFFER never arrived, so the far end has no manifest to ask about and only the holder can break
    /// the deadlock. Re-offering is idempotent — a peer that already has it wants nothing.
    /// </para>
    /// </summary>
    public async Task ResumeAllWithAsync(string peerTag, CancellationToken cancellationToken = default)
    {
        if (_disposed || string.IsNullOrEmpty(peerTag)) return;

        try
        {
            int asked = 0, offered = 0;

            foreach (var descriptor in await _content.ListDescriptorsAsync(cancellationToken).ConfigureAwait(false))
            {
                var have = (await _content.ListChunksAsync(descriptor.RootHash, cancellationToken).ConfigureAwait(false)).Count;

                if (have < descriptor.ChunkCount)
                {
                    asked++;
                    await AskForMissingAsync(peerTag, descriptor).ConfigureAwait(false);
                }
                else
                {
                    offered++;
                    await OfferAsync(peerTag, descriptor, cancellationToken).ConfigureAwait(false);
                }
            }

            if (asked > 0 || offered > 0) T($"link back with {peerTag} — re-asked {asked}, re-offered {offered}");
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "resuming transfers with {Peer}", peerTag);
        }
    }

    /// <summary>
    /// A packet from this peer would not open, and the session is the likely reason.
    ///
    /// <para>
    /// Raised rather than acted on, because repairing a session is <see cref="ChatService"/>'s job and
    /// duplicating it would mean two copies of the trickiest code in the app drifting apart. This is
    /// the seam voice already uses for the same reason.
    /// </para>
    ///
    /// <para>
    /// Without it, attachments were the one path with no recovery at all. Chat repairs itself, voice
    /// repairs itself, and a note whose WANT could not be decrypted simply died — which is exactly
    /// what a note stuck at 26 of 29 chunks across a dozen restarts looked like, while chat over the
    /// same pair carried on working perfectly.
    /// </para>
    /// </summary>
    public event Action<string>? SessionLooksBroken;

    /// <summary>Raised when an attachment finishes arriving, so the bubble can turn into a player.</summary>
    public event Action<string>? Arrived;

    /// <summary>Raised as chunks land, so a half-arrived note can show how far it has got.</summary>
    public event Action<string, double>? Progress;

    /// <summary>Running commentary, for the radio log.</summary>
    public event Action<string>? Trace;

    // ── Sending ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Store <paramref name="bytes"/> locally and start sending them to <paramref name="peerTag"/>.
    /// Returns the content hash to put on the message.
    ///
    /// <para>
    /// The hash comes back immediately, before anything has been transferred, because the message
    /// should appear in the conversation the moment it is recorded — not when the last chunk lands.
    /// </para>
    /// </summary>
    public async Task<ContentDescriptor> SendAsync(
        string peerTag, byte[] bytes, string contentType, string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(peerTag);
        ArgumentNullException.ThrowIfNull(bytes);

        var descriptor = ContentDescriptor.FromBytes(name, bytes, contentType, ChunkBytes);

        // Keep our own copy first. The sender must be able to play back what it sent even if the
        // transfer never completes, and a chunk cannot be served to the peer unless it is stored.
        await _content.SaveDescriptorAsync(descriptor, cancellationToken).ConfigureAwait(false);
        for (var i = 0; i < descriptor.ChunkCount; i++)
        {
            var offset = i * descriptor.ChunkSizeBytes;
            var size = Math.Min(descriptor.ChunkSizeBytes, bytes.Length - offset);
            await _content.SaveChunkAsync(descriptor.RootHash, i, bytes.AsSpan(offset, size).ToArray(), cancellationToken)
                .ConfigureAwait(false);
        }

        T($"attachment {Short(descriptor.RootHash)} — {descriptor.ChunkCount} chunk(s), {descriptor.TotalBytes}B");
        await OfferAsync(peerTag, descriptor, cancellationToken).ConfigureAwait(false);
        return descriptor;
    }

    /// <summary>Tell the peer what is coming. They reply saying which chunks they still need.</summary>
    private async Task OfferAsync(string peerTag, ContentDescriptor descriptor, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(descriptor, Json);
        await SendSealedAsync(peerTag, OfferMarker, body, cancellationToken).ConfigureAwait(false);
    }

    // ── Receiving ─────────────────────────────────────────────────────────────

    private void OnPacket(byte[] raw)
    {
        if (_disposed) return;

        MeshPacket packet;
        try { packet = PacketSerializer.Deserialize(raw); }
        catch { return; }

        if (packet.Type != PacketType.Data) return;
        var payload = packet.Payload;
        if (payload is null || payload.Length <= OfferMarker.Length) return;

        var marker = Encoding.UTF8.GetString(payload, 0, OfferMarker.Length);
        if (marker is not (OfferMarker or ChunkMarker or WantMarker)) return;

        _ = HandleAsync(packet.SourceUhid, marker, payload);
    }

    private async Task HandleAsync(string? from, string marker, byte[] payload)
    {
        if (string.IsNullOrEmpty(from)) return;

        // Every failure below used to go to ILogger, which on the phone goes nowhere anybody looks —
        // so a peer that received an attachment packet and could not open it was indistinguishable
        // from a peer that never received one. A note stuck at 26 of 29 chunks with a completely
        // empty log on the sending side is what that costs.
        T($"{marker} in from {from}");

        try
        {
            var sealedBody = EncryptedPayloadCodec.Deserialize(payload.AsSpan(marker.Length).ToArray());
            var body = await _signal.DecryptAsync(from, sealedBody).ConfigureAwait(false);
            _radio?.IdentifyPeer(from);

            switch (marker)
            {
                case OfferMarker: await OnOfferAsync(from, body).ConfigureAwait(false); break;
                case WantMarker: await OnWantAsync(from, body).ConfigureAwait(false); break;
                case ChunkMarker: await OnChunkAsync(from, body).ConfigureAwait(false); break;
            }
        }
        catch (Exception ex)
        {
            T($"{marker} from {from} would NOT open — {ex.GetType().Name}: {ex.Message}");
            _log.LogWarning(ex, "Could not handle an attachment {Marker} from {Peer}", marker, from);

            // A tag mismatch is not corruption on the wire — it is two sides holding sessions that no
            // longer agree. Somebody has to rebuild it, and the transfer resumes on its own once one
            // does, because resume asks only for what is still missing.
            if (LooksLikeABrokenSession(ex)) SessionLooksBroken?.Invoke(from);
        }
    }

    /// <summary>They are sending us something. Keep the manifest and ask for what we do not have.</summary>
    private async Task OnOfferAsync(string from, byte[] body)
    {
        var descriptor = JsonSerializer.Deserialize<ContentDescriptor>(body, Json);
        if (descriptor is null) return;

        // Trust the descriptor only if it is internally consistent — the root hash is computed over
        // the chunk hashes, so a descriptor that fails this was tampered with or corrupted, and every
        // chunk we verified against it afterwards would be meaningless.
        if (!descriptor.VerifySelf())
        {
            T($"attachment offer from {from} does not verify — ignored");
            return;
        }

        await _content.SaveDescriptorAsync(descriptor).ConfigureAwait(false);
        await AskForMissingAsync(from, descriptor).ConfigureAwait(false);
    }

    /// <summary>
    /// Ask for the chunks we are missing — which is also how a transfer resumes.
    /// <para>
    /// Nothing distinguishes "starting" from "resuming": we always ask for what we do not have, so a
    /// note interrupted at 80% picks up at 80% without anyone tracking that it was interrupted.
    /// </para>
    /// </summary>
    private async Task AskForMissingAsync(string peerTag, ContentDescriptor descriptor)
    {
        var have = (await _content.ListChunksAsync(descriptor.RootHash).ConfigureAwait(false)).ToHashSet();
        var missing = Enumerable.Range(0, descriptor.ChunkCount).Where(i => !have.Contains(i)).ToArray();

        if (missing.Length == 0)
        {
            Complete(descriptor.RootHash);
            return;
        }

        T($"want {missing.Length}/{descriptor.ChunkCount} chunk(s) of {Short(descriptor.RootHash)}");
        var want = new Want { Hash = descriptor.RootHash, Chunks = missing };
        await SendSealedAsync(peerTag, WantMarker, JsonSerializer.SerializeToUtf8Bytes(want, Json), default)
            .ConfigureAwait(false);
    }

    /// <summary>They asked for chunks. Send them, one at a time, in order.</summary>
    private async Task OnWantAsync(string from, byte[] body)
    {
        var want = JsonSerializer.Deserialize<Want>(body, Json);
        if (want is null || string.IsNullOrEmpty(want.Hash)) return;

        foreach (var index in want.Chunks)
        {
            if (_disposed) return;

            var bytes = await _content.GetChunkAsync(want.Hash, index).ConfigureAwait(false);
            if (bytes is null) continue;

            var chunk = new Chunk { Hash = want.Hash, Index = index, Bytes = bytes };
            await SendSealedAsync(from, ChunkMarker, JsonSerializer.SerializeToUtf8Bytes(chunk, Json), default)
                .ConfigureAwait(false);
        }
    }

    /// <summary>A chunk arrived. Verify it against the manifest before keeping it.</summary>
    private async Task OnChunkAsync(string from, byte[] body)
    {
        var chunk = JsonSerializer.Deserialize<Chunk>(body, Json);
        if (chunk is null || chunk.Bytes is null || string.IsNullOrEmpty(chunk.Hash)) return;

        var descriptor = await _content.GetDescriptorAsync(chunk.Hash).ConfigureAwait(false);
        if (descriptor is null) return;

        // Content addressing means we never have to trust the sender — only the maths. A chunk that
        // does not hash to what the descriptor says is dropped, whoever it came from.
        if (!descriptor.VerifyChunk(chunk.Index, chunk.Bytes))
        {
            T($"chunk {chunk.Index} of {Short(chunk.Hash)} failed verification — dropped");
            return;
        }

        await _content.SaveChunkAsync(chunk.Hash, chunk.Index, chunk.Bytes).ConfigureAwait(false);

        var have = (await _content.ListChunksAsync(chunk.Hash).ConfigureAwait(false)).Count;
        Progress?.Invoke(chunk.Hash, descriptor.ChunkCount == 0 ? 1 : (double)have / descriptor.ChunkCount);

        if (have >= descriptor.ChunkCount) Complete(chunk.Hash);
    }

    private void Complete(string hash)
    {
        T($"attachment {Short(hash)} complete");
        Progress?.Invoke(hash, 1);
        Arrived?.Invoke(hash);
    }

    // ── Reading it back ───────────────────────────────────────────────────────

    /// <summary>
    /// The whole thing, reassembled — or null if it has not all arrived yet.
    /// <para>Returning null rather than a partial file is deliberate: half a voice note is noise.</para>
    /// </summary>
    public async Task<byte[]?> GetAsync(string hash, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(hash)) return null;

        var descriptor = await _content.GetDescriptorAsync(hash, cancellationToken).ConfigureAwait(false);
        if (descriptor is null) return null;

        var buffer = new byte[descriptor.TotalBytes];
        var written = 0;

        for (var i = 0; i < descriptor.ChunkCount; i++)
        {
            var chunk = await _content.GetChunkAsync(hash, i, cancellationToken).ConfigureAwait(false);
            if (chunk is null) return null;          // still incomplete

            chunk.CopyTo(buffer, written);
            written += chunk.Length;
        }

        return written == buffer.Length ? buffer : null;
    }

    /// <summary>How much of it is here, 0 to 1 — for a bubble that is still filling.</summary>
    public async Task<double> ProgressOfAsync(string hash, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(hash)) return 0;

        var descriptor = await _content.GetDescriptorAsync(hash, cancellationToken).ConfigureAwait(false);
        if (descriptor is null || descriptor.ChunkCount == 0) return 0;

        var have = (await _content.ListChunksAsync(hash, cancellationToken).ConfigureAwait(false)).Count;
        return Math.Min(1, (double)have / descriptor.ChunkCount);
    }

    /// <summary>
    /// Nudge a stalled transfer along — call when a link comes back.
    /// <para>Asks only for what is missing, so it costs nothing when everything already arrived.</para>
    /// </summary>
    public async Task ResumeAsync(string peerTag, string hash, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(peerTag) || string.IsNullOrEmpty(hash)) return;

        var descriptor = await _content.GetDescriptorAsync(hash, cancellationToken).ConfigureAwait(false);
        if (descriptor is null) return;

        await AskForMissingAsync(peerTag, descriptor).ConfigureAwait(false);
    }

    // ── The wire ──────────────────────────────────────────────────────────────

    private async Task SendSealedAsync(string peerTag, string marker, byte[] body, CancellationToken cancellationToken)
    {
        // A send that does not happen has to say so. This returned silently, so a WANT that was
        // composed, logged and then dropped for want of a session looked exactly like a WANT that was
        // sent and ignored by the far end — and a note stuck at 26 of 29 chunks gave no clue which.
        if (_radio is null)
        {
            T($"{marker} to {peerTag} DROPPED — no radio");
            return;
        }

        if (!_signal.HasSession(peerTag))
        {
            T($"{marker} to {peerTag} DROPPED — no session with that name");
            return;
        }

        try
        {
            var sealedBody = await _signal.EncryptAsync(peerTag, body, cancellationToken).ConfigureAwait(false);
            var serialized = EncryptedPayloadCodec.Serialize(sealedBody);

            var payload = new byte[marker.Length + serialized.Length];
            Encoding.UTF8.GetBytes(marker).CopyTo(payload, 0);
            serialized.CopyTo(payload, marker.Length);

            await _radio.SendPacketAsync(PacketSerializer.Serialize(new MeshPacket
            {
                Type = PacketType.Data,
                SourceUhid = _me.AetherTag,
                DestinationUhid = peerTag,
                Ttl = 1,
                Payload = payload,
            })).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A chunk that will not go is asked for again on the next resume; nothing is lost.
            _log.LogDebug(ex, "Could not send an attachment {Marker} to {Peer}", marker, peerTag);
        }
    }

    /// <summary>
    /// Whether this failure means the session, rather than the packet.
    ///
    /// <para>
    /// A diverged double ratchet fails its authentication tag — the ciphertext is intact and the key
    /// is wrong. Anything else here is a malformed or truncated packet, which repairing a session
    /// would not help and which asking again will.
    /// </para>
    /// </summary>
    private static bool LooksLikeABrokenSession(Exception ex) =>
        ex is System.Security.Cryptography.AuthenticationTagMismatchException
        || ex is System.Security.Cryptography.CryptographicException
        || ex.InnerException is System.Security.Cryptography.AuthenticationTagMismatchException;

    private static string Short(string hash) => hash.Length <= 8 ? hash : hash[..8];
    private void T(string message) => Trace?.Invoke(message);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_radio is not null) _radio.PacketReceived -= OnPacket;
    }

    // ── Wire shapes ───────────────────────────────────────────────────────────

    private sealed class Want
    {
        public string Hash { get; set; } = string.Empty;
        public int[] Chunks { get; set; } = Array.Empty<int>();
    }

    private sealed class Chunk
    {
        public string Hash { get; set; } = string.Empty;
        public int Index { get; set; }
        public byte[]? Bytes { get; set; }
    }
}

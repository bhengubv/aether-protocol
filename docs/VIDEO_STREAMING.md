# Video Streaming -- Developer Guide

A practical guide for developers who want to understand, implement, or extend Aether's video capabilities. This document covers the complete video subsystem: P2P calls, group video, synchronized watch-together, BitTorrent ingest, and the ChipIn group-funding mechanism.

All code examples reference the C# reference implementation in `Shared.Aether/Video/`.

---

## Overview

Aether Video Streaming (Phase 7) adds three media modes on top of the mesh network:

1. **Video Calls** -- P2P with codec negotiation (H.264/H.265/VP8), transport capability detection, and automatic BLE-to-voice downgrade.
2. **Watch Together** -- Synchronized group media playback in three sub-modes: SharedFile (sync commands only), StreamFromHost (P2P chunk transfer), and BitTorrent (mesh-internet bridge).
3. **Live Broadcast** -- Extends the Phase 6 streaming subsystem with two new quality tiers: NearLink 360p (800 Kbps) and Internet 1080p (3000 Kbps).

All video features are transport-aware, end-to-end encrypted via Signal Protocol, and work offline through the mesh. Every feature is gated behind a feature flag (disabled by default) and can be enabled progressively.

---

## Packet Types

Phase 7 introduces 8 new packet types (27-34). All travel as standard `MeshPacket` instances -- the `Type` field determines how the payload is interpreted.

| Type Value | Name | Purpose | Direction | Typical Payload Size |
|:----------:|------|---------|-----------|---------------------|
| 27 | `VideoCall` | Carries encoded video frames during a P2P call | Sender -> Receiver | 1-50 KB per frame |
| 28 | `VideoSignaling` | Call lifecycle: offer, answer, reject, bye, codec change, screen-share toggle | Bidirectional | 200-500 bytes |
| 29 | `WatchSync` | Host-issued playback commands: play, pause, seek, speed, buffer status | Host -> Participants | 100-200 bytes |
| 30 | `WatchReaction` | Timestamped emoji or voice comment during watch-together | Sender -> All | 50 bytes (emoji) / 5-50 KB (voice) |
| 31 | `VideoFrame` | Encoded video frame in group video sessions | Sender -> Peers or SFU | 1-50 KB per frame |
| 32 | `ScreenShare` | Screen capture frames (10 fps); uses same pipeline as VideoCall/VideoFrame | Sender -> Receiver(s) | 5-100 KB per frame |
| 33 | `WatchChunkRequest` | Request a specific content chunk from a peer (StreamFromHost mode) | Downloader -> Seeder | 50-100 bytes |
| 34 | `TorrentMetadata` | BitTorrent .torrent metadata broadcast to watch-together participants | Host -> All (broadcast) | 1-50 KB |

All packet types are defined in `AetherMesh.Protocol.PacketType` (open-source) and `TheGeekNetwork.Shared.AetherMesh.Protocol.Models.PacketType` (private).

---

## Video Call -- Step by Step

This section walks through the complete lifecycle of a P2P video call, from capability check to teardown.

### Step 1: Check Transport Capability

Before initiating a call, the caller queries what the active transport can support. The `VideoCallService` inspects `TransportManager` metrics to determine the transport type:

```csharp
public Task<TransportVideoCapability> GetVideoCapabilityAsync(string peerUhid)
{
    var metrics = _transport.GetMetrics();
    var transportType = metrics.NearLinkSendCount > 0 ? "nearlink"
        : metrics.ActiveWifiDirectConnections > 0 ? "wifi_direct"
        : metrics.BleSendCount > 0 ? "ble"
        : "internet";

    var capability = transportType switch
    {
        "ble" => new TransportVideoCapability
        {
            SupportsVideo = false,
            MaxResolution = VideoResolution.AudioOnly,
            MaxBitrateKbps = 64,
            Description = "Voice only -- BLE connection"
        },
        "nearlink" => new TransportVideoCapability
        {
            SupportsVideo = true,
            MaxResolution = VideoResolution.R360p,
            RecommendedCodec = VideoCodec.H265,
            MaxBitrateKbps = 800,  // ProtocolConstants.NearLink360pBitrateKbps
        },
        "wifi_direct" => new TransportVideoCapability
        {
            SupportsVideo = true,
            MaxResolution = VideoResolution.R1080p,
            MaxBitrateKbps = 3000, // ProtocolConstants.Internet1080pBitrateKbps
        },
        _ => new TransportVideoCapability
        {
            SupportsVideo = true,
            MaxResolution = VideoResolution.R720p,
            MaxBitrateKbps = 1500,
        }
    };

    return Task.FromResult(capability);
}
```

If the transport is BLE-only (`SupportsVideo == false`), the service automatically delegates to `IVoiceCallService` and returns a `VideoCall` with `Resolution = AudioOnly`. The caller's UI should display the call as voice-only.

### Step 2: Send Offer

`InitiateVideoCallAsync` creates a `VideoCall` object, serializes a `VideoSignaling` message with `SignalType = Offer`, encrypts it with Signal Protocol, wraps it in a `MeshPacket`, and sends it:

```csharp
var signaling = new VideoSignaling
{
    CallId = call.Id,
    SenderUhid = localNode.Uhid,
    SignalType = VideoSignalType.Offer,
    PreferredCodec = call.Codec,
    MaxResolution = call.Resolution,
    MaxBitrateKbps = call.BitrateKbps
};

var payload = JsonSerializer.SerializeToUtf8Bytes(signaling);

// Encrypt with Signal Protocol
if (_signalProtocol is not null)
{
    var encrypted = await _signalProtocol.EncryptAsync(calleeUhid, payload);
    if (encrypted is not null)
        payload = JsonSerializer.SerializeToUtf8Bytes(encrypted);
}

var packet = new MeshPacket
{
    Type = PacketType.VideoSignaling,
    SourceUhid = localNode.Uhid,
    DestinationUhid = calleeUhid,
    Payload = payload
};

var serialized = PacketSerializer.Serialize(packet);
await _transport.SendAsync(calleeUhid, serialized);
```

The call is also registered on the server (`StoreVideoSignalAsync`) for signaling relay when peers are not directly reachable. Server registration is fire-and-forget -- the call works without it.

### Step 3: Receive Answer (Codec Negotiation)

The receiver's `HandleVideoSignalingAsync` dispatches the offer to `HandleOfferAsync`, which creates a local `VideoCall` in `Ringing` state and fires the `IncomingVideoCall` event. The UI presents the incoming call.

When the user accepts, `AcceptVideoCallAsync` sends an `Answer` signal back. The caller's `HandleAnswerAsync` performs codec negotiation by picking the **lowest common denominator**:

```csharp
// Negotiate to lowest common denominator
if (signaling.PreferredCodec.HasValue)
    call.Codec = signaling.PreferredCodec.Value;
if (signaling.MaxResolution.HasValue && signaling.MaxResolution.Value < call.Resolution)
    call.Resolution = signaling.MaxResolution.Value;
if (signaling.MaxBitrateKbps.HasValue && signaling.MaxBitrateKbps.Value < call.BitrateKbps)
    call.BitrateKbps = signaling.MaxBitrateKbps.Value;
```

Both sides configure their jitter buffers with `VideoJitterBufferMinMs` (60ms) and `VideoJitterBufferMaxMs` (500ms).

### Step 4: Exchange Frames

Once the call is `Active`, frames flow through `SendVideoFrameAsync`:

```csharp
var payload = JsonSerializer.SerializeToUtf8Bytes(frame);

// Encrypt every frame
if (_signalProtocol is not null)
{
    var encrypted = await _signalProtocol.EncryptAsync(peerUhid, payload);
    if (encrypted is not null)
        payload = JsonSerializer.SerializeToUtf8Bytes(encrypted);
    else
    {
        // No Signal session -- frame dropped (never send unencrypted video)
        return;
    }
}

var packetType = frame.IsScreenShare ? PacketType.ScreenShare : PacketType.VideoCall;
var packet = new MeshPacket
{
    Type = packetType,
    SourceUhid = localNode.Uhid,
    DestinationUhid = peerUhid,
    Payload = payload,
    Priority = frame.IsKeyFrame ? 10 : 0  // Keyframes get priority
};

var serialized = PacketSerializer.Serialize(packet);
await _transport.SendAsync(peerUhid, serialized);
call.FramesSent++;
```

Key points:
- **Keyframes get Priority 10** so mesh routers prefer them over P-frames during congestion.
- **Unencrypted video frames are never sent** -- if there is no Signal session, the frame is silently dropped.
- The receiver's `HandleVideoCallAsync` decrypts and feeds frames into the jitter buffer.

### Step 5: Toggle Screen Share

Screen sharing uses the same frame pipeline but with a different packet type and signal:

```csharp
call.IsScreenSharing = !call.IsScreenSharing;

var signaling = new VideoSignaling
{
    CallId = call.Id,
    SenderUhid = localNode.Uhid,
    SignalType = call.IsScreenSharing
        ? VideoSignalType.ScreenShareStart
        : VideoSignalType.ScreenShareStop
};
```

Screen share frames run at 10 fps (vs. 30 fps for camera) and use `PacketType.ScreenShare`.

### Step 6: End Call

Either party can end the call by sending a `Bye` signal:

```csharp
call.State = VideoCallState.Ended;
call.EndedAt = DateTimeOffset.UtcNow;
call.DurationSeconds = (int)(call.EndedAt.Value - call.StartedAt).TotalSeconds;
_jitterBuffer?.Reset();

// Send Bye signal (encrypted)
var signaling = new VideoSignaling
{
    CallId = call.Id,
    SenderUhid = localNode.Uhid,
    SignalType = VideoSignalType.Bye
};
```

The receiver's `HandleByeAsync` mirrors the cleanup: state to `Ended`, jitter buffer reset, `VideoCallEnded` event fired.

### Encryption Model (Video Calls)

The encryption flow for every packet:

**Sending:**
1. Serialize the domain object (e.g., `VideoSignaling` or `VideoFrame`) to JSON bytes
2. Call `_signalProtocol.EncryptAsync(peerUhid, plaintextBytes)` -- returns an `EncryptedPayload`
3. Serialize the `EncryptedPayload` to JSON bytes -- this becomes the packet payload
4. Wrap in a `MeshPacket` and call `PacketSerializer.Serialize(packet)` to get wire bytes

**Receiving:**
1. `PacketSerializer.Deserialize(wireBytes)` produces the `MeshPacket`
2. Try to deserialize `packet.Payload` as `EncryptedPayload`
3. If it has ciphertext, call `_signalProtocol.DecryptAsync(encryptedPayload)` to get plaintext bytes
4. Deserialize plaintext bytes as the domain object

```
Plaintext -> JSON bytes -> EncryptAsync -> EncryptedPayload -> JSON bytes -> MeshPacket.Payload
                                                                                    |
                                                                         PacketSerializer.Serialize
                                                                                    |
                                                                              Wire bytes
```

The `EncryptedPayload` structure:

```csharp
public class EncryptedPayload
{
    public byte[] Ciphertext { get; set; } = [];
    public byte[] Nonce { get; set; } = [];
    public int MessageType { get; set; }    // 1 = PreKey message, 2 = regular
    public string SenderUhid { get; set; } = string.Empty;
    public int Counter { get; set; }
    public DateTimeOffset EncryptedAt { get; set; }
}
```

### How the Jitter Buffer Orders and Drops Frames

`VideoJitterBuffer` uses a `SortedDictionary<int, VideoFrame>` keyed by sequence number. This ensures frames are always dequeued in order regardless of arrival order.

**Adaptive depth:** The buffer targets 2x the estimated jitter, clamped between `MinBufferMs` (60ms) and `MaxBufferMs` (500ms). Jitter is estimated with an exponential moving average (alpha = 0.1):

```csharp
var deviation = Math.Abs(interval - expectedInterval);
_jitterEstimate = 0.1 * deviation + 0.9 * _jitterEstimate;
_config.CurrentBufferMs = Math.Clamp((int)(_jitterEstimate * 2), minMs, maxMs);
```

**Overflow dropping:** When the buffer exceeds `MaxBufferMs / 33` frames (approximate frame count at 30fps), the oldest **non-keyframe** is dropped first. Keyframes are preserved as long as possible because losing an I-frame corrupts all subsequent P-frames until the next I-frame.

**Gap handling on dequeue:** If the expected sequence number is missing:
1. Search for the next available keyframe at or after the expected position
2. If found, skip to it (fast-forward past the gap)
3. If no keyframe exists, return the next available frame

```csharp
var nextKeyFrame = _buffer.FirstOrDefault(
    kvp => kvp.Value.IsKeyFrame && kvp.Key >= _nextExpectedSequence);
if (nextKeyFrame.Value is not null)
{
    _buffer.Remove(nextKeyFrame.Key);
    _nextExpectedSequence = nextKeyFrame.Key + 1;
    return nextKeyFrame.Value;
}
```

---

## Group Video

### FullMesh vs SFU Topology

Group video sessions support two topologies that switch automatically based on participant count:

| Topology | Participants | How It Works |
|----------|:-----------:|--------------|
| **FullMesh** | 2-3 | Every participant sends frames directly to every other participant. Simple, low latency, but O(n^2) bandwidth. |
| **SFU** | 4+ | All participants send frames to a single relay node, which distributes them. O(n) bandwidth per sender. |

### Auto-Switch at 4 Participants

The threshold is defined by `ProtocolConstants.SfuThresholdParticipants = 4`. When a participant joins or leaves, `UpdateTopologyAsync` re-evaluates:

```csharp
private Task UpdateTopologyAsync(GroupVideoSession session)
{
    var count = session.Participants.Count;

    if (count < ProtocolConstants.SfuThresholdParticipants)
    {
        session.Topology = VideoTopology.FullMesh;
        session.SfuRelayUhid = null;
    }
    else
    {
        session.Topology = VideoTopology.Sfu;
        if (session.SfuRelayUhid is null)
            session.SfuRelayUhid = session.Participants.FirstOrDefault()?.Uhid;
    }
    return Task.CompletedTask;
}
```

### SFU Relay Node Selection

In the current implementation, the first participant is selected as the relay. In production, this should be the participant (or a dedicated node) with the highest reliability score and best connectivity. Gateway nodes are ideal candidates.

### FullMesh Frame Distribution

```csharp
// FullMesh: send to each participant directly
foreach (var participant in session.Participants)
{
    if (participant.Uhid == localNode.Uhid) continue;
    var packet = new MeshPacket
    {
        Type = packetType,
        SourceUhid = localNode.Uhid,
        DestinationUhid = participant.Uhid,
        Payload = payload,
        Priority = frame.IsKeyFrame ? 10 : 0
    };
    var serialized = PacketSerializer.Serialize(packet);
    await _transport.SendAsync(participant.Uhid, serialized);
}
```

### SFU Frame Distribution

```csharp
// SFU: send only to relay node
var packet = new MeshPacket
{
    Type = packetType,
    SourceUhid = localNode.Uhid,
    DestinationUhid = session.SfuRelayUhid,
    Payload = payload,
    Priority = frame.IsKeyFrame ? 10 : 0
};
var serialized = PacketSerializer.Serialize(packet);
await _transport.SendAsync(session.SfuRelayUhid, serialized);
```

### Incentive: Relay Nodes Earn Tips

SFU relay nodes consume significant bandwidth on behalf of the group. Through Aether's tipping system, participants can reward relay nodes with SDPKT wallet tips. The incentive layer (Phase 4) tracks relay bytes and surfaces tip opportunities in the UI.

---

## Watch Together -- Three Modes

All three modes share the same `WatchSession` model, `WatchSyncCommand` system, and reaction mechanism. They differ in how content reaches participants.

### SharedFile

**The simplest mode.** All participants already have the media file locally. Only sync commands travel over the mesh.

- **Content matched by SHA-256 hash:** When creating a session, the host specifies `contentHash`. Participants verify they have the same file.
- **Works over BLE:** Since only tiny sync commands (100-200 bytes) are exchanged, this mode works on the lowest-bandwidth transports.
- **No chunk transfer needed:** `HasFile = true` for all participants.

**RTT compensation formula:**

When a sync command arrives, the receiver adjusts the playback position to account for network delay:

```csharp
var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
var networkDelay = (now - syncCmd.WallClockMs) / 2;  // Half RTT
syncCmd.RttMs = (int)(now - syncCmd.WallClockMs);

case WatchSyncType.Play:
    session.State = WatchState.Playing;
    session.PositionMs = syncCmd.PositionMs + networkDelay;
    break;
```

The host embeds `WallClockMs` (UTC unix milliseconds) when sending. The receiver computes the one-way delay as `(now - wallClock) / 2` and advances the position by that amount. This keeps all participants within half-RTT of each other.

### StreamFromHost

**The host has the file; participants download it in real time.** Content is distributed as chunks using the P2P content system.

Key behaviors:

1. **ContentManifest generation:** The host generates a manifest listing all chunks with their SHA-256 hashes. The `ManifestId` is stored on the `WatchSession`.

2. **SequentialFromPosition chunk strategy:** Unlike RarestFirst (used for general P2P downloads), StreamFromHost uses `SequentialFromPositionStrategy` which prioritizes chunks near the current playback position:

```csharp
public ChunkInfo? SelectNext(ContentManifest manifest, Dictionary<int, List<string>> chunkSeeders)
{
    // Priority 1: Missing chunks from current position forward
    var aheadChunk = manifest.Chunks
        .Where(c => c.Status == ChunkStatus.Missing && c.ChunkIndex >= _currentChunkIndex)
        .OrderBy(c => c.ChunkIndex)
        .FirstOrDefault();
    if (aheadChunk is not null) return aheadChunk;

    // Priority 2: Backfill earlier chunks (for seeding to others)
    var backfillChunk = manifest.Chunks
        .Where(c => c.Status == ChunkStatus.Missing && c.ChunkIndex < _currentChunkIndex)
        .OrderBy(c => chunkSeeders.TryGetValue(c.ChunkIndex, out var s) ? s.Count : int.MaxValue)
        .ThenBy(c => c.ChunkIndex)
        .FirstOrDefault();
    return backfillChunk;
}
```

The algorithm ensures:
- Chunks ahead of playback are fetched first (prevents stalling)
- Once ahead-buffer is full, earlier chunks are backfilled so the viewer can seed them
- Backfill uses rarest-first ordering to maximize swarm health

3. **30-second buffer target:** `ProtocolConstants.WatchBufferAheadSeconds = 30`. The system tries to maintain 30 seconds of content buffered ahead of playback.

4. **Auto-pause on buffer underrun (10s threshold):** If any participant's buffer drops below `WatchMinBufferSeconds * 1000` (10,000ms), the session auto-pauses for everyone with a `BufferUnderrun` sync command:

```csharp
if (bufferAheadMs < ProtocolConstants.WatchTogetherMinBufferSeconds * 1000
    && session.State == WatchState.Playing
    && session.Mode != WatchMode.SharedFile)
{
    session.State = WatchState.Buffering;
    var syncCmd = CreateSyncCommand(session, WatchSyncType.BufferUnderrun, session.PositionMs);
    await BroadcastSyncAsync(session, syncCmd);
}
```

Playback resumes automatically when **all** participants have buffered past the minimum threshold and the buffer-ahead target is met:

```csharp
var allBuffered = session.Participants.All(p =>
    p.BufferAheadMs >= ProtocolConstants.WatchTogetherMinBufferSeconds * 1000 || p.HasFile);
if (allBuffered)
{
    session.State = WatchState.Playing;
    var syncCmd = CreateSyncCommand(session, WatchSyncType.BufferReady, session.PositionMs);
    await BroadcastSyncAsync(session, syncCmd);
}
```

5. **Viewers become seeders:** Once a participant downloads chunks, they appear in the seeder map. New joiners can download from any participant, not just the host.

### BitTorrent

**Bridges internet BitTorrent swarms with the mesh network.** Gateway nodes with internet access download from the public swarm and re-distribute via mesh.

1. **TorrentMetadata packet distribution:** The host broadcasts a `TorrentMetadata` packet containing the `.torrent` file data:

```csharp
var payload = JsonSerializer.SerializeToUtf8Bytes(torrentInfo);
var packet = new MeshPacket
{
    Type = PacketType.TorrentMetadata,
    SourceUhid = localNode.Uhid,
    DestinationUhid = "*", // broadcast
    Payload = payload
};
```

The `TorrentInfo` model carries everything needed to join the swarm:

```csharp
public class TorrentInfo
{
    public string InfoHash { get; set; }
    public string? MagnetLink { get; set; }
    public byte[]? TorrentFileData { get; set; }
    public string? Name { get; set; }
    public long TotalSizeBytes { get; set; }
    public int PieceCount { get; set; }
    public int PieceSizeBytes { get; set; }
    public List<TorrentFile> Files { get; set; } = [];
}
```

2. **Gateway bridge: internet swarm to mesh peers:** A gateway node joins the BitTorrent swarm using the info hash, downloads pieces, and serves them as Aether chunks to mesh peers.

3. **Piece-to-chunk translation:** BitTorrent pieces (typically 256KB-4MB) are split into Aether chunks (`ProtocolConstants.DefaultChunkSizeBytes = 8192` bytes) for mesh transport. Chunk indices map deterministically to piece ranges.

4. **Re-encryption on ingest:** Content arriving from the internet swarm is unencrypted. The gateway node re-encrypts chunks using Signal Protocol before distributing to mesh peers, ensuring end-to-end encryption within the mesh.

---

## ChipIn -- Group Content Acquisition

ChipIn enables group members to pool SDPKT funds to collectively purchase content (a movie, album, etc.) for a watch-together session.

### State Machine

```
Collecting -> Funded -> Purchasing -> Acquired
     |
     +-> Failed
     +-> Refunded
```

| State | Meaning |
|-------|---------|
| `Collecting` | Pool is open, accepting contributions |
| `Funded` | Target amount reached, ready to purchase |
| `Purchasing` | Purchase in progress (server-side) |
| `Acquired` | Content purchased and available |
| `Failed` | Purchase failed after funding |
| `Refunded` | Contributions returned to wallets |

### SDPKT Wallet Integration

Each contribution is recorded both locally and on the server. The server call returns a `SdpktTransactionId` linking the contribution to the SDPKT ledger:

```csharp
var contribution = new ChipInContribution
{
    ContributorUhid = localNode.Uhid,
    AmountZar = amountZar
};

session.ChipIn.Contributions.Add(contribution);
session.ChipIn.CollectedAmountZar += amountZar;

// Record via SDPKT
var result = await _apiClient.ContributeChipInAsync<ChipInContribution>(new
{
    chip_in_id = chipInId,
    contributor_uhid = localNode.Uhid,
    amount_zar = amountZar
});
if (result is not null)
    contribution.SdpktTransactionId = result.SdpktTransactionId;
```

When `CollectedAmountZar >= TargetAmountZar`, the state transitions to `Funded` and the `ChipInUpdated` event fires. Settlement happens via LedgerAPI on the server side.

### Contribution Tracking

Every contribution is tracked with:
- `ContributorUhid` -- who paid
- `AmountZar` -- how much (in ZAR)
- `SdpktTransactionId` -- SDPKT ledger reference (nullable if offline)
- `ContributedAt` -- timestamp

The `ChipInPool` model on the `WatchSession` aggregates all contributions and provides the running total.

---

## Reactions

Watch-together participants can react to media at specific timestamps. Reactions are broadcast to all participants.

### Emoji Reactions

```csharp
var reaction = new WatchReaction
{
    SessionId = sessionId,
    SenderUhid = localNode.Uhid,
    Type = WatchReactionType.Emoji,
    Emoji = emoji,             // e.g., "fire", "laugh", "heart"
    MediaPositionMs = mediaPositionMs  // Position in the media timeline
};
```

### Voice Comments

Voice reactions are Opus-encoded audio clips, capped at 10 seconds:

```csharp
if (durationMs > 10000) return; // Hard limit: 10 seconds

var reaction = new WatchReaction
{
    Type = WatchReactionType.VoiceComment,
    VoiceData = voiceData,
    VoiceDurationMs = durationMs,
    MediaPositionMs = mediaPositionMs
};
```

### Broadcast

All reactions are sent as `PacketType.WatchReaction` packets to every participant (except the sender). The receiver fires `ReactionReceived` for the UI to display.

---

## Transport Capability Matrix

| Transport | Video Supported | Max Resolution | Recommended Codec | Max Bitrate | Watch Together | Description |
|-----------|:--------------:|:--------------:|:-----------------:|:-----------:|:--------------:|-------------|
| **BLE** | No | AudioOnly | N/A | 64 Kbps | SharedFile only | Auto-downgrades to voice call |
| **CircleLink** | No | AudioOnly | N/A | 64 Kbps | SharedFile only | Voice only |
| **NearLink** | Yes | 360p | H.265 | 800 Kbps | Yes | Light video |
| **Wi-Fi Direct** | Yes | 1080p | H.264 | 3000 Kbps | Yes | Full HD video |
| **Internet** | Yes | 720p | H.264 | 1500 Kbps | Yes | Default path |

BLE and CircleLink connections cannot carry video. When a video call is initiated over these transports, `VideoCallService` automatically delegates to `VoiceCallService`:

```csharp
if (!capability.SupportsVideo && _voiceCallService is not null)
{
    var voiceCall = await _voiceCallService.InitiateCallAsync(calleeUhid);
    return new VideoCall
    {
        Resolution = VideoResolution.AudioOnly,
        BitrateKbps = 64,
        TransportType = capability.TransportType
    };
}
```

---

## Encryption Model

| Video Mode | Encryption Method | Key Distribution |
|------------|-------------------|-----------------|
| **P2P Video Call** | Signal Protocol (per-frame) | Pre-key exchange via mesh or server relay |
| **Group Video (FullMesh)** | None (current) | Future: channel key via Signal |
| **Group Video (SFU)** | None (current) | Future: SFU re-encrypts per recipient |
| **Watch Sync Commands** | Signal Protocol (per-participant) | Each command encrypted individually per recipient |
| **Watch Reactions** | None (current) | Future: channel-level encryption |
| **Watch Chunks (StreamFromHost)** | P2P Content encryption | Chunk-level AES via P2pContentService |
| **BitTorrent Ingest** | Re-encrypted at gateway | Gateway encrypts with Signal before mesh distribution |

In P2P calls, every video frame is encrypted before sending. If no Signal session exists, the frame is **silently dropped** -- unencrypted video never traverses the network. For group video, encryption is planned for a future phase when channel-level key agreement is implemented.

---

## Feature Flags

All 6 video feature flags are seeded as disabled. Enable them progressively in production.

| Flag Key | Display Name | Parent Dependency | What It Gates |
|----------|-------------|-------------------|---------------|
| `AETHERMESH_VIDEO_CALL` | Video Calling | `AETHERMESH_VOICE` | P2P video calls, codec negotiation, capability detection |
| `AETHERMESH_VIDEO_GROUP` | Group Video | `AETHERMESH_VIDEO_CALL` | Multi-party video sessions, FullMesh/SFU topology |
| `AETHERMESH_SCREEN_SHARE` | Screen Sharing | `AETHERMESH_VIDEO_CALL` | Screen capture frame sharing in calls |
| `AETHERMESH_WATCH_TOGETHER` | Watch Together | `AETHERMESH_CONTENT_P2P` | Synchronized media playback sessions |
| `AETHERMESH_WATCH_REACTIONS` | Watch Reactions | `AETHERMESH_WATCH_TOGETHER` | Emoji and voice reactions during sessions |
| `AETHERMESH_TORRENT_INGEST` | BitTorrent Ingest | `AETHERMESH_CONTENT_P2P` | Accept torrent files for watch-together |

**Parent dependency** means the parent flag must be enabled before the child flag can be activated. For example, `AETHERMESH_VIDEO_GROUP` requires `AETHERMESH_VIDEO_CALL` which in turn requires `AETHERMESH_VOICE`.

---

## Database Schema (AetherMeshAPI)

Migration `010_VideoWatchTogether.sql` creates 8 tables in PostgreSQL. All use `gen_random_uuid()` for primary keys.

### video_calls

Stores P2P video call records.

| Column | Type | Default | Description |
|--------|------|---------|-------------|
| `id` | UUID | `gen_random_uuid()` | Primary key |
| `caller_uhid` | TEXT | -- | Caller's UHID |
| `callee_uhid` | TEXT | -- | Callee's UHID |
| `codec` | INT | 0 (H264) | Negotiated codec |
| `resolution` | INT | 3 (720p) | Negotiated resolution |
| `bitrate_kbps` | INT | 1500 | Negotiated bitrate |
| `transport_type` | TEXT | 'internet' | Transport used |
| `state` | INT | 0 (Initiating) | Call state |
| `frames_sent` | BIGINT | 0 | Total frames sent |
| `frames_received` | BIGINT | 0 | Total frames received |
| `duration_seconds` | INT | 0 | Call duration |
| `is_screen_sharing` | BOOLEAN | false | Screen share active |
| `started_at` | TIMESTAMPTZ | NOW() | Call start time |
| `ended_at` | TIMESTAMPTZ | -- | Call end time (nullable) |

Indexes: `caller_uhid`, `callee_uhid`, `state`

### group_video_sessions

Stores group video session metadata.

| Column | Type | Default | Description |
|--------|------|---------|-------------|
| `id` | UUID | `gen_random_uuid()` | Primary key |
| `channel_id` | UUID | -- | Associated channel |
| `topology` | INT | 0 (FullMesh) | Current topology |
| `sfu_relay_uhid` | TEXT | -- | SFU relay node (nullable) |
| `participant_count` | INT | 0 | Current participant count |
| `created_at` | TIMESTAMPTZ | NOW() | Session creation time |
| `ended_at` | TIMESTAMPTZ | -- | Session end time (nullable) |

Index: `channel_id`

### group_video_participants

Tracks individual participants in group video sessions.

| Column | Type | Default | Description |
|--------|------|---------|-------------|
| `id` | UUID | `gen_random_uuid()` | Primary key |
| `session_id` | UUID | -- | FK to `group_video_sessions` |
| `uhid` | TEXT | -- | Participant UHID |
| `resolution` | INT | 3 (720p) | Participant's resolution |
| `codec` | INT | 0 (H264) | Participant's codec |
| `bitrate_kbps` | INT | 1500 | Participant's bitrate |
| `is_muted` | BOOLEAN | false | Audio muted |
| `is_video_off` | BOOLEAN | false | Video disabled |
| `is_screen_sharing` | BOOLEAN | false | Screen sharing active |
| `joined_at` | TIMESTAMPTZ | NOW() | Join time |
| `left_at` | TIMESTAMPTZ | -- | Leave time (nullable) |

Index: `session_id`

### watch_sessions

Stores watch-together session state.

| Column | Type | Default | Description |
|--------|------|---------|-------------|
| `id` | UUID | `gen_random_uuid()` | Primary key |
| `channel_id` | UUID | -- | Associated channel |
| `host_uhid` | TEXT | -- | Session host UHID |
| `mode` | INT | 0 (SharedFile) | Watch mode |
| `state` | INT | 0 (WaitingForReady) | Session state |
| `content_hash` | TEXT | -- | SHA-256 hash of content |
| `manifest_id` | UUID | -- | Content manifest (nullable) |
| `position_ms` | BIGINT | 0 | Current playback position |
| `speed` | DOUBLE PRECISION | 1.0 | Playback speed |
| `created_at` | TIMESTAMPTZ | NOW() | Creation time |
| `ended_at` | TIMESTAMPTZ | -- | End time (nullable) |

Indexes: `channel_id`, `host_uhid`

### watch_participants

Tracks participants in watch-together sessions.

| Column | Type | Default | Description |
|--------|------|---------|-------------|
| `id` | UUID | `gen_random_uuid()` | Primary key |
| `session_id` | UUID | -- | FK to `watch_sessions` |
| `uhid` | TEXT | -- | Participant UHID |
| `is_ready` | BOOLEAN | false | Participant ready status |
| `has_file` | BOOLEAN | false | Has content locally |
| `buffer_percent` | INT | 0 | Buffer percentage |
| `buffer_ahead_ms` | BIGINT | 0 | Milliseconds buffered ahead |
| `joined_at` | TIMESTAMPTZ | NOW() | Join time |

Index: `session_id`

### watch_reactions

Stores reactions (emoji and voice) during watch-together sessions.

| Column | Type | Default | Description |
|--------|------|---------|-------------|
| `id` | UUID | `gen_random_uuid()` | Primary key |
| `session_id` | UUID | -- | FK to `watch_sessions` |
| `sender_uhid` | TEXT | -- | Reactor's UHID |
| `reaction_type` | INT | 0 (Emoji) | Emoji or VoiceComment |
| `emoji` | TEXT | -- | Emoji string (nullable) |
| `has_voice` | BOOLEAN | false | Has voice data |
| `media_position_ms` | BIGINT | 0 | Media timeline position |
| `created_at` | TIMESTAMPTZ | NOW() | Reaction time |

Index: `session_id`

### chip_in_pools

Stores group funding pools for content acquisition.

| Column | Type | Default | Description |
|--------|------|---------|-------------|
| `id` | UUID | `gen_random_uuid()` | Primary key |
| `session_id` | UUID | -- | FK to `watch_sessions` |
| `initiator_uhid` | TEXT | -- | Who started the pool |
| `target_amount_zar` | DECIMAL(10,2) | -- | Target amount in ZAR |
| `collected_amount_zar` | DECIMAL(10,2) | 0 | Amount collected so far |
| `state` | INT | 0 (Collecting) | Pool state |
| `content_description` | TEXT | -- | What is being purchased |
| `torrent_info_hash` | TEXT | -- | Torrent hash (nullable) |
| `magnet_link` | TEXT | -- | Magnet link (nullable) |
| `created_at` | TIMESTAMPTZ | NOW() | Pool creation time |

### chip_in_contributions

Individual contributions to a ChipIn pool.

| Column | Type | Default | Description |
|--------|------|---------|-------------|
| `id` | UUID | `gen_random_uuid()` | Primary key |
| `chip_in_id` | UUID | -- | FK to `chip_in_pools` |
| `contributor_uhid` | TEXT | -- | Who contributed |
| `amount_zar` | DECIMAL(10,2) | -- | Contribution amount |
| `sdpkt_transaction_id` | UUID | -- | SDPKT ledger reference (nullable) |
| `contributed_at` | TIMESTAMPTZ | NOW() | Contribution time |

Index: `chip_in_id`

---

## API Endpoints

AetherMeshAPI exposes 17 endpoints across two endpoint groups, all requiring `NodeAuth` authorization.

### Video Endpoints (`/api/aether/video`)

| Method | Path | Request Body | Response | Description |
|--------|------|-------------|----------|-------------|
| POST | `/signal` | `VideoCallSignalRequest` | `{ id: Guid }` | Store a video call signal (offer/answer/bye) |
| GET | `/{id}` | -- | `VideoCallResponse` | Get video call details |
| GET | `/active/{uhid}` | -- | `VideoCallResponse[]` | Get active calls for a UHID |
| PUT | `/{id}/state/{state}` | -- | 200 OK | Update call state |
| POST | `/group` | `CreateGroupVideoRequest` | `{ id: Guid }` | Create a group video session |
| GET | `/group/{id}` | -- | `GroupVideoResponse` | Get group session details |
| POST | `/group/{sessionId}/join` | Query: `uhid`, `resolution`, `codec`, `bitrateKbps` | 200 OK | Join a group session |
| POST | `/group/{sessionId}/leave` | Query: `uhid` | 200 OK | Leave a group session |

### Watch Endpoints (`/api/aether/watch`)

| Method | Path | Request Body | Response | Description |
|--------|------|-------------|----------|-------------|
| POST | `/sessions` | `CreateWatchSessionRequest` | `{ id: Guid }` | Create a watch-together session |
| GET | `/sessions/{id}` | -- | `WatchSessionResponse` | Get session details |
| GET | `/sessions/channel/{channelId}` | -- | `WatchSessionResponse[]` | Get active sessions for a channel |
| POST | `/sessions/{sessionId}/join` | `JoinWatchSessionRequest` | 200 OK | Join a watch session |
| PUT | `/sessions/{id}/state` | Query: `state`, `positionMs` | 200 OK | Update session state and position |
| POST | `/reactions` | `WatchReactionRequest` | 200 OK | Record a reaction |
| POST | `/chipin` | `CreateChipInRequest` | `{ id: Guid }` | Create a ChipIn pool |
| GET | `/chipin/{id}` | -- | `ChipInResponse` | Get ChipIn pool details |
| POST | `/chipin/contribute` | `ChipInContributeRequest` | 200 OK | Contribute to a ChipIn pool |

### Request/Response Shapes

```csharp
// Video
public record VideoCallSignalRequest(
    string CallerUhid, string CalleeUhid,
    int Codec = 0, int Resolution = 3, int BitrateKbps = 1500,
    string SignalType = "offer", byte[]? SignalData = null);

public record CreateGroupVideoRequest(
    Guid ChannelId, string Uhid,
    int Resolution = 3, int Codec = 0, int BitrateKbps = 1500);

// Watch
public record CreateWatchSessionRequest(
    Guid ChannelId, string HostUhid, string ContentHash, int Mode = 0);

public record JoinWatchSessionRequest(string Uhid, bool HasFile = false);

public record WatchReactionRequest(
    Guid SessionId, string SenderUhid,
    int ReactionType = 0, string? Emoji = null,
    bool HasVoice = false, long MediaPositionMs = 0);

// ChipIn
public record CreateChipInRequest(
    Guid SessionId, string InitiatorUhid, decimal TargetAmountZar,
    string? ContentDescription = null, string? TorrentInfoHash = null,
    string? MagnetLink = null);

public record ChipInContributeRequest(
    Guid ChipInId, string ContributorUhid, decimal AmountZar);
```

---

## Configuration Constants

All video-related constants from `ProtocolConstants`:

| Constant | Value | Unit | Description |
|----------|------:|------|-------------|
| `VideoFrameDurationMs` | 33 | ms | Target frame duration at 30 fps |
| `VideoJitterBufferMinMs` | 60 | ms | Minimum jitter buffer depth |
| `VideoJitterBufferMaxMs` | 500 | ms | Maximum jitter buffer depth |
| `WatchBufferAheadSeconds` | 30 | s | Target buffer ahead of playback |
| `WatchMinBufferSeconds` | 10 | s | Minimum buffer before auto-pause triggers |
| `SfuThresholdParticipants` | 4 | count | Participant count that triggers FullMesh-to-SFU switch |
| `NearLink360pBitrateKbps` | 800 | Kbps | NearLink video bitrate ceiling |
| `Internet1080pBitrateKbps` | 3000 | Kbps | Wi-Fi Direct / Internet 1080p bitrate ceiling |
| `DefaultChunkSizeBytes` | 8192 | bytes | Chunk size for content transfer |
| `MaxConcurrentChunkTransfers` | 4 | count | Concurrent chunk downloads per peer |
| `StreamSegmentDurationMs` | 2000 | ms | Live stream segment duration |
| `MaxStreamSubscribers` | 50 | count | Max subscribers per relay node |

Additional relevant constants (from voice/transport):

| Constant | Value | Unit | Description |
|----------|------:|------|-------------|
| `JitterBufferTargetMs` | 60 | ms | Voice jitter buffer target (audio baseline) |
| `JitterBufferMaxMs` | 200 | ms | Voice jitter buffer max |
| `BleMaxPayloadBytes` | 1024 | bytes | BLE payload limit |
| `WifiDirectMaxPayloadBytes` | 65536 | bytes | Wi-Fi Direct payload limit |
| `NearLinkMaxPayloadBytes` | 4096 | bytes | NearLink payload limit |

---

## Extending Video

### Adding a New Codec

1. Add the codec to `VideoCodec` enum in both repos:

```csharp
// Shared.Aether/Video/Models/VideoModels.cs
public enum VideoCodec
{
    H264 = 0,
    H265 = 1,
    VP8 = 2,
    AV1 = 3     // New codec
}

// aether-protocol/src/AetherMesh.Streaming/Models/VideoModels.cs
public enum VideoCodec : byte
{
    H264 = 0,
    H265 = 1,
    VP8 = 2,
    AV1 = 3     // New codec
}
```

2. Implement `IVideoCodecService.EncodeFrameAsync` and `DecodeFrameAsync` for the new codec.

3. Update `GetVideoCapabilityAsync` in `VideoCallService` to recommend the new codec for appropriate transports.

4. Update `GetBestCodec` in your `IVideoCodecService` implementation.

5. Update the open-source implementations in all 8 languages (Rust, Go, TypeScript, Python, Kotlin, Swift, C).

### Adding a New Watch Mode

1. Add the mode to `WatchMode` enum:

```csharp
public enum WatchMode
{
    SharedFile = 0,
    StreamFromHost = 1,
    BitTorrent = 2,
    HlsProxy = 3     // New mode
}
```

2. Handle the new mode in `WatchTogetherService.CreateSessionAsync` -- set up any required infrastructure (manifests, proxies, etc.).

3. Handle joining for the new mode in `JoinSessionAsync`.

4. If the mode requires new packet types, add them to `PacketType` (next available is 35) and implement handlers.

5. Update `ReportBufferStatusAsync` if the new mode has different buffering semantics.

### Implementing a Custom Chunk Selection Strategy

Implement the `IChunkSelectionStrategy` interface:

```csharp
public interface IChunkSelectionStrategy
{
    ChunkInfo? SelectNext(ContentManifest manifest, Dictionary<int, List<string>> chunkSeeders);
}
```

Example -- priority-weighted strategy that fetches chunks based on external priority scores:

```csharp
public class PriorityWeightedStrategy : IChunkSelectionStrategy
{
    private readonly Dictionary<int, int> _priorities = new();

    public void SetPriority(int chunkIndex, int priority)
    {
        _priorities[chunkIndex] = priority;
    }

    public ChunkInfo? SelectNext(ContentManifest manifest, Dictionary<int, List<string>> chunkSeeders)
    {
        return manifest.Chunks
            .Where(c => c.Status == ChunkStatus.Missing)
            .OrderByDescending(c => _priorities.TryGetValue(c.ChunkIndex, out var p) ? p : 0)
            .ThenBy(c => chunkSeeders.TryGetValue(c.ChunkIndex, out var s) ? s.Count : int.MaxValue)
            .FirstOrDefault();
    }
}
```

### Registering Video Services in DI

All video services are registered in `AetherMeshExtensions.AddAetherMeshLink()`:

```csharp
// Phase 7: Video
services.AddSingleton<IVideoCallService, VideoCallService>();
services.AddSingleton<IGroupVideoService, GroupVideoService>();
services.AddSingleton<IVideoJitterBufferService, VideoJitterBuffer>();
services.AddSingleton<IWatchTogetherService, WatchTogetherService>();
```

To register a custom service, add it after the standard registrations:

```csharp
services.AddAetherMeshLink();
services.AddSingleton<IVideoCodecService, MyCustomCodecService>();
```

Optional dependencies (Signal Protocol, voice call service, jitter buffer) are injected via nullable constructor parameters. If not registered, the service degrades gracefully:
- No `ISignalProtocolService` -- signaling sent unencrypted, video frames dropped
- No `IVideoJitterBufferService` -- frames delivered directly via `VideoFrameReceived` event
- No `IVoiceCallService` -- BLE connections throw instead of downgrading to voice

---

## Troubleshooting

### BLE Auto-Downgrade to Voice

**Symptom:** User initiates a video call but only gets audio.

**Cause:** The only available transport is BLE, which caps at 64 Kbps -- insufficient for video.

**What happens:** `VideoCallService.InitiateVideoCallAsync` detects `SupportsVideo == false` from `GetVideoCapabilityAsync`, delegates to `VoiceCallService.InitiateCallAsync`, and returns a `VideoCall` with `Resolution = AudioOnly`.

**Resolution:** This is intentional behavior. The UI should display the call as voice-only and explain why (e.g., "Mesh connection -- voice only"). If the user moves closer to a Wi-Fi Direct or NearLink peer, subsequent calls will upgrade.

### Jitter Buffer Overflow

**Symptom:** Video becomes choppy with visible frame drops. `VideoJitterBufferConfig.FramesDropped` increases rapidly.

**Cause:** Frames arrive faster than they are consumed, or sustained high jitter causes the buffer to grow past `MaxBufferMs`.

**What happens:** The buffer drops the oldest non-keyframe first. If all frames are keyframes (unusual), it drops the oldest keyframe.

**Resolution:**
1. Check `GetStats().AverageJitterMs` -- if consistently high, the transport is overloaded
2. Consider reducing resolution/bitrate via a `Downgrade` signal
3. If on NearLink, 800 Kbps may be at capacity -- reduce to 360p
4. Verify the dequeue rate matches the target frame rate (33ms per frame at 30fps)

### Buffer Underrun During StreamFromHost

**Symptom:** Watch-together session auto-pauses frequently with `BufferUnderrun` sync commands.

**Cause:** The download rate from seeders is slower than the playback rate. The buffer drops below `WatchMinBufferSeconds` (10s).

**What happens:** `ReportBufferStatusAsync` detects `bufferAheadMs < 10000` and broadcasts `BufferUnderrun`. All participants pause.

**Resolution:**
1. Check if more seeders are available -- viewers who already downloaded chunks should appear in the seeder map
2. Reduce playback speed (`SendSpeedAsync` with speed < 1.0) to give the buffer time to fill
3. If the host is the only seeder and on a slow transport, consider switching to SharedFile mode (if all participants can obtain the file independently)
4. The 30-second buffer target (`WatchBufferAheadSeconds`) means the system tries to stay 30s ahead. If it cannot, underruns will occur

### ChipIn State Stuck in Collecting

**Symptom:** ChipIn pool has received enough contributions but remains in `Collecting` state.

**Cause:** The `CollectedAmountZar` comparison uses `>=` against `TargetAmountZar`, but floating-point rounding on `decimal` should not be an issue. More likely, the server-side contribution recording failed (offline) and the local state was not updated.

**What happens:** `ContributeToChipInAsync` adds the contribution locally and attempts to record it on the server. If the server call fails, the `SdpktTransactionId` remains null but the local total is still updated.

**Resolution:**
1. Check if `CollectedAmountZar >= TargetAmountZar` in the local `ChipInPool` object -- if true, manually transition to `Funded`
2. If contributions were lost due to offline state, have participants re-send contributions when connectivity is restored
3. Monitor `ChipInUpdated` events -- the state should transition to `Funded` immediately when the threshold is crossed
4. If the issue persists, inspect the `chip_in_pools` and `chip_in_contributions` tables in the `aethermesh_db` database

---

## Wire Format Reference

For completeness, here is the `MeshPacket` binary wire format used by `PacketSerializer`. All multi-byte integers are little-endian.

```
Offset  Size     Field
------  ------   -----
0       1 byte   Protocol version (currently 2)
1       1 byte   Packet type (27-34 for video)
2       16 bytes Packet ID (GUID)
18      1 byte   Priority (0 = normal, 10 = keyframe, 255 = SOS)
19      4 bytes  TTL (int32)
23      8 bytes  TimestampMs (int64, unix milliseconds)
31      2 bytes  SourceUhid length (uint16)
33      N bytes  SourceUhid (UTF-8)
33+N    2 bytes  DestinationUhid length (uint16)
35+N    M bytes  DestinationUhid (UTF-8)
35+N+M  2 bytes  PacketNonce length (uint16)
37+N+M  P bytes  PacketNonce
37+N+M+P 4 bytes Payload length (int32)
41+...   Q bytes Payload (encrypted JSON)
41+...+Q 2 bytes Signature length (uint16)
43+...   S bytes Signature (Ed25519)
```

The payload for video packets is always JSON-serialized domain objects (possibly wrapped in an `EncryptedPayload` JSON envelope). The packet type tells the receiver how to interpret the payload after decryption.

---

## Node Capability

Video support is advertised via the `NodeCapabilities` flags enum:

```csharp
[Flags]
public enum NodeCapabilities : ushort
{
    None = 0,
    Ble = 1,
    WifiDirect = 2,
    Gateway = 4,
    Relay = 8,
    Sos = 16,
    Streaming = 32,
    Voice = 64,
    DtnCarrier = 128,
    NearLink = 256,
    Video = 512        // Phase 7
}
```

A node that supports video calls should advertise `Video = 512`. This is separate from `Streaming = 32` (live broadcast) and `Voice = 64` (audio calls). Check capabilities with:

```csharp
if (node.HasCapability(NodeCapabilities.Video))
{
    // Node supports video calls and watch-together
}
```

---

## Summary of Key Files

| File | Location | Purpose |
|------|----------|---------|
| `VideoModels.cs` | `Shared.Aether/Video/Models/` | VideoCall, VideoFrame, VideoSignaling, GroupVideoSession, TransportVideoCapability |
| `WatchModels.cs` | `Shared.Aether/Video/Models/` | WatchSession, WatchSyncCommand, WatchReaction, ChipInPool, TorrentInfo |
| `IVideoServices.cs` | `Shared.Aether/Video/Services/` | IVideoCallService, IGroupVideoService, IVideoCodecService, IVideoJitterBufferService |
| `IWatchTogetherService.cs` | `Shared.Aether/Video/Services/` | IWatchTogetherService (full interface) |
| `VideoCallService.cs` | `Shared.Aether/Video/Services/` | P2P video calls with Signal Protocol encryption |
| `GroupVideoService.cs` | `Shared.Aether/Video/Services/` | Group video with FullMesh/SFU auto-switch |
| `VideoJitterBuffer.cs` | `Shared.Aether/Video/Services/` | Adaptive jitter buffer with keyframe-aware dropping |
| `WatchTogetherService.cs` | `Shared.Aether/Video/Services/` | Watch-together with all three modes + ChipIn |
| `SequentialFromPosition.cs` | `Shared.Aether/Content/Strategies/` | Chunk selection biased to playback position |
| `010_VideoWatchTogether.sql` | `AetherMeshAPI/Migrations/` | 8 PostgreSQL tables + 6 feature flags |
| `VideoEndpoints.cs` | `AetherMeshAPI/Endpoints/` | 8 video API endpoints |
| `WatchEndpoints.cs` | `AetherMeshAPI/Endpoints/` | 9 watch-together API endpoints |
| `AetherMeshExtensions.cs` | `Shared.Aether/DependencyInjection/` | DI registration for all video services |
| `ProtocolConstants.cs` | `Shared.Aether/Protocol/Constants/` | Video-related configuration constants |

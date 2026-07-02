// SPDX-License-Identifier: MIT

namespace AetherNet.VideoCallControl;

/// <summary>
/// JSON payload for <see cref="Protocol.PacketType.VideoCall"/> packets — the video call-control
/// signal (ring / accept / decline / hangup), distinct from the media-plane <c>VideoSignaling</c>
/// (SDP/ICE negotiation) and <c>VideoFrame</c> (media) handled by the streaming VideoCallService.
/// This is the caller-intent layer, mirroring how <c>VoiceCall</c> carries voice call-control.
///
/// Wire format: UTF-8 JSON, snake_case keys, field order call_id, action, sent_at_ms, no whitespace,
/// lowercase-dashed UUID, sent_at_ms a bare integer, action ASCII. Byte-identity gate:
/// fixtures/videocall/vectors.json.
/// </summary>
public sealed class VideoCallControlPayload
{
    /// <summary>Unique id for this call (minted by the caller on ring; echoed by accept/decline/hangup).</summary>
    public Guid CallId { get; set; }

    /// <summary>Control verb: "ring", "accept", "decline", or "hangup".</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Unix timestamp in milliseconds when the control signal was sent.</summary>
    public long SentAtMs { get; set; }
}

/// <summary>Event raised when a video call-control signal arrives from a peer.</summary>
public sealed class VideoCallStateChanged
{
    /// <summary>Id of the call the signal refers to.</summary>
    public Guid CallId { get; set; }

    /// <summary>The control verb received ("ring" / "accept" / "decline" / "hangup").</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>UHID of the peer that sent the signal.</summary>
    public string FromUhid { get; set; } = string.Empty;
}

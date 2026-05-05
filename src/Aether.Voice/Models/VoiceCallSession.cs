// SPDX-License-Identifier: MIT

namespace Aether.Voice.Models;

/// <summary>
/// State machine of a voice call from this node's perspective.
/// </summary>
public enum CallState : byte
{
    /// <summary>No call. Initial and terminal state for failed/missed calls.</summary>
    Idle = 0,
    /// <summary>We sent an offer; awaiting peer answer.</summary>
    Outgoing = 1,
    /// <summary>Peer sent us an offer; user has not yet decided.</summary>
    Incoming = 2,
    /// <summary>Both parties accepted; voice frames are flowing.</summary>
    Connected = 3,
    /// <summary>Call ended cleanly (either party hung up).</summary>
    Ended = 4,
    /// <summary>Call ended due to error (timeout, transport failure, codec mismatch).</summary>
    Failed = 5,
}

/// <summary>
/// Reason a call ended. Carried in <see cref="VoiceSignalingMessage"/> with kind=Hangup.
/// </summary>
public enum HangupReason : byte
{
    Normal = 0,
    Busy = 1,
    Declined = 2,
    Timeout = 3,
    NetworkFailure = 4,
    CodecMismatch = 5,
    Unknown = 255,
}

/// <summary>
/// Tracks the state of a single voice call. Created when the local node either
/// sends an offer (<see cref="CallState.Outgoing"/>) or receives one (<see cref="CallState.Incoming"/>).
/// Mutated by the signaling service as offers / answers / hangups arrive.
/// </summary>
public sealed class VoiceCallSession
{
    /// <summary>Globally unique call id. Used as the correlation key for every signaling and frame packet.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>UHID of the call's originator (whoever sent the initial offer).</summary>
    public string CallerUhid { get; set; } = string.Empty;

    /// <summary>UHID of the call's recipient (whoever received the initial offer).</summary>
    public string CalleeUhid { get; set; } = string.Empty;

    /// <summary>Current state.</summary>
    public CallState State { get; set; } = CallState.Idle;

    /// <summary>Codec name selected for this call (e.g. "opus", "speex"). Empty until both parties agree.</summary>
    public string Codec { get; set; } = string.Empty;

    /// <summary>Sample rate in Hz (e.g. 16000, 48000). 0 until both parties agree.</summary>
    public int SampleRateHz { get; set; }

    /// <summary>Frame duration in ms (typically <see cref="Aether.Constants.ProtocolConstants.VoiceFrameDurationMs"/>).</summary>
    public int FrameDurationMs { get; set; } = Aether.Constants.ProtocolConstants.VoiceFrameDurationMs;

    /// <summary>UTC timestamp at which the call session was created locally.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>UTC timestamp at which the call connected (entered <see cref="CallState.Connected"/>), or null if not yet.</summary>
    public DateTime? ConnectedAt { get; set; }

    /// <summary>UTC timestamp at which the call ended, or null if still active.</summary>
    public DateTime? EndedAt { get; set; }

    /// <summary>Hangup reason, populated when <see cref="State"/> is <see cref="CallState.Ended"/> or <see cref="CallState.Failed"/>.</summary>
    public HangupReason? HangupReason { get; set; }

    /// <summary>The remote party's UHID from the local node's perspective.</summary>
    public string RemoteUhid(string localUhid) =>
        string.Equals(localUhid, CallerUhid, StringComparison.Ordinal) ? CalleeUhid : CallerUhid;
}

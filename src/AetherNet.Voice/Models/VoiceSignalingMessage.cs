// SPDX-License-Identifier: MIT

namespace AetherNet.Voice.Models;

/// <summary>
/// Kind of voice signaling message. Carried in the JSON payload of a
/// <see cref="AetherNet.Protocol.PacketType.VoiceSignaling"/> packet under the <c>kind</c> field.
/// </summary>
public enum SignalingKind : byte
{
    /// <summary>Caller initiates a call. Includes proposed codec list.</summary>
    Offer = 0,
    /// <summary>Callee accepts. Includes the chosen codec.</summary>
    Answer = 1,
    /// <summary>Either party ends the call.</summary>
    Hangup = 2,
    /// <summary>Either party rings out / tears down due to timeout. Equivalent to a Hangup with <see cref="HangupReason.Timeout"/>.</summary>
    Timeout = 3,
    /// <summary>Caller cancels before answer.</summary>
    Cancel = 4,
}

/// <summary>
/// Signaling envelope serialized into <see cref="AetherNet.Protocol.PacketType.VoiceSignaling"/>
/// packet payloads. JSON-encoded with snake_case property names so other-language
/// implementations can produce / consume identical bytes.
/// </summary>
public sealed class VoiceSignalingMessage
{
    /// <summary>Discriminator — drives the state-machine transition on receive.</summary>
    public SignalingKind Kind { get; set; }

    /// <summary>Call this message refers to.</summary>
    public Guid CallId { get; set; }

    /// <summary>UHID of the node that sent this signaling message.</summary>
    public string FromUhid { get; set; } = string.Empty;

    /// <summary>UHID of the node addressed by this signaling message.</summary>
    public string ToUhid { get; set; } = string.Empty;

    /// <summary>For <see cref="SignalingKind.Offer"/>: codec names this caller can encode/decode, in preference order. Ignored for other kinds.</summary>
    public IReadOnlyList<string> ProposedCodecs { get; set; } = Array.Empty<string>();

    /// <summary>For <see cref="SignalingKind.Answer"/>: the single codec the callee chose from the proposal. Empty for other kinds.</summary>
    public string SelectedCodec { get; set; } = string.Empty;

    /// <summary>For <see cref="SignalingKind.Answer"/>: sample rate the callee will encode at. 0 for other kinds.</summary>
    public int SampleRateHz { get; set; }

    /// <summary>For <see cref="SignalingKind.Hangup"/> and <see cref="SignalingKind.Timeout"/>: reason code.</summary>
    public HangupReason Reason { get; set; } = HangupReason.Unknown;

    /// <summary>UTC timestamp of the signaling message.</summary>
    public DateTime SentAt { get; set; } = DateTime.UtcNow;
}

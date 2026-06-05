// SPDX-License-Identifier: MIT

namespace AetherMesh.Voice.Models;

/// <summary>
/// State of a group voice call (3+ participants).
/// </summary>
public enum GroupCallState : byte
{
    Pending = 0,
    Active = 1,
    Ended = 2,
}

/// <summary>
/// Group voice call session. The host orchestrates membership; every participant
/// encrypts their outbound frames once with the current group sender-key and
/// broadcasts. The sender-key rotates on member-join / member-leave so a leaver
/// cannot decrypt future frames.
/// </summary>
public sealed class GroupVoiceCallSession
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>UHID of the host. Host is the only node that can invite or kick participants.</summary>
    public string HostUhid { get; set; } = string.Empty;

    /// <summary>Current participant UHIDs. Always includes the host.</summary>
    public IReadOnlyList<string> Participants { get; set; } = Array.Empty<string>();

    public GroupCallState State { get; set; } = GroupCallState.Pending;

    /// <summary>Negotiated codec name. Empty until the host's invite settles.</summary>
    public string Codec { get; set; } = string.Empty;

    /// <summary>Negotiated sample rate.</summary>
    public int SampleRateHz { get; set; }

    /// <summary>
    /// Current sender-key generation. Increments on every membership change so receivers
    /// know which key version a frame was encrypted with.
    /// </summary>
    public uint KeyGeneration { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
}

/// <summary>
/// Discriminator for group voice signaling messages.
/// </summary>
public enum GroupSignalingKind : byte
{
    /// <summary>Host invites a peer to the call.</summary>
    Invite = 0,
    /// <summary>Invitee accepts.</summary>
    Accept = 1,
    /// <summary>Invitee declines.</summary>
    Decline = 2,
    /// <summary>Participant leaves voluntarily.</summary>
    Leave = 3,
    /// <summary>Host removes a participant.</summary>
    Kick = 4,
    /// <summary>Host rotates the sender-key. Carries an opaque host-encrypted key blob per participant.</summary>
    RotateKey = 5,
    /// <summary>Host ends the call for everyone.</summary>
    End = 6,
}

/// <summary>
/// Wire envelope for <see cref="AetherMesh.Protocol.PacketType.VoiceSignaling"/> when the
/// signaling concerns a group call. JSON-encoded snake_case names.
/// </summary>
public sealed class GroupVoiceSignalingMessage
{
    public GroupSignalingKind Kind { get; set; }
    public Guid CallId { get; set; }
    public string FromUhid { get; set; } = string.Empty;
    public string ToUhid { get; set; } = string.Empty;

    /// <summary>For <see cref="GroupSignalingKind.Invite"/>: codec proposed by the host.</summary>
    public string Codec { get; set; } = string.Empty;
    public int SampleRateHz { get; set; }

    /// <summary>For <see cref="GroupSignalingKind.RotateKey"/>: generation of the new key.</summary>
    public uint KeyGeneration { get; set; }

    /// <summary>For <see cref="GroupSignalingKind.RotateKey"/>: the key material wrapped for this recipient (opaque to the protocol).</summary>
    public byte[] WrappedKeyForRecipient { get; set; } = [];

    /// <summary>For <see cref="GroupSignalingKind.Invite"/> / Kick: the participant UHID being added or removed.</summary>
    public string AffectedUhid { get; set; } = string.Empty;

    public DateTime SentAt { get; set; } = DateTime.UtcNow;
}

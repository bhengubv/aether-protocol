// SPDX-License-Identifier: MIT

namespace AetherNet.Profiles;

/// <summary>
/// JSON payload for <see cref="Protocol.PacketType.ProfileSync"/> packets. Wire format: UTF-8 JSON with
/// snake_case keys, field order uhid, display_name, avatar_ref, status_message, updated_at_ms, no
/// whitespace, updated_at_ms a bare integer. Byte-identity is locked by fixtures/profiles/vectors.json.
/// All string fields are always present (empty when unset) — no nulls — so the encoding cannot diverge
/// across languages.
///
/// <para><b>Privacy:</b> a profile is exchanged <em>directed</em> (point-to-point to a specific peer),
/// NOT broadcast to the whole mesh — broadcasting display names to every device in range is exactly the
/// metadata leak the privacy roadmap forbids. A peer you interact with learns your profile; strangers do
/// not.</para>
/// </summary>
public sealed class ProfileSyncPayload
{
    /// <summary>UHID this profile describes (the sender). Self-identifying so a cached profile stays attributable.</summary>
    public string Uhid { get; set; } = string.Empty;

    /// <summary>Human-readable display name (empty if unset).</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Content-addressed reference to an avatar (e.g. "blake3:…"), empty if none.</summary>
    public string AvatarRef { get; set; } = string.Empty;

    /// <summary>Free-text status / presence message (empty if unset).</summary>
    public string StatusMessage { get; set; } = string.Empty;

    /// <summary>Unix timestamp in milliseconds when the profile was last updated by its owner.</summary>
    public long UpdatedAtMs { get; set; }
}

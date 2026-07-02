/**
 * Profile sync data models (PacketType.ProfileSync = 23).
 *
 * The wire payload is UTF-8 JSON with snake_case keys and field order `uhid`, `display_name`,
 * `avatar_ref`, `status_message`, `updated_at_ms` — no whitespace, `updated_at_ms` a bare
 * integer, all string fields always present (empty when unset, never null) — so the encoding is
 * byte-identical across every language port (locked by fixtures/profiles/vectors.json).
 *
 * SPDX-License-Identifier: MIT
 */

/**
 * JSON payload for a ProfileSync packet.
 *
 * Privacy: a profile is exchanged directed (point-to-point to a specific peer), NOT broadcast to
 * the whole mesh — broadcasting display names to every device in range is exactly the metadata
 * leak the privacy roadmap forbids. A peer you interact with learns your profile; strangers do not.
 */
export interface ProfileSyncPayload {
  /** UHID this profile describes (the sender). Self-identifying so a cached profile stays attributable. */
  uhid: string;
  /** Human-readable display name (empty if unset). */
  displayName: string;
  /** Content-addressed reference to an avatar (e.g. "blake3:…"), empty if none. */
  avatarRef: string;
  /** Free-text status / presence message (empty if unset). */
  statusMessage: string;
  /** Unix timestamp in milliseconds when the profile was last updated by its owner. */
  updatedAtMs: number;
}

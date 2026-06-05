// SPDX-License-Identifier: MIT

using AetherMesh.Identity;

namespace AetherMesh.Models;

/// <summary>
/// Capabilities a node can advertise. Combined as flags.
/// </summary>
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
    Video = 512,
    Space = 1024,
    Forge = 2048,
    Vault = 4096,
    Market = 8192
}

/// <summary>
/// Presence status reported by a node.
/// </summary>
public enum PresenceStatus : byte
{
    Unknown = 0,
    Available = 1,
    Busy = 2,
    Away = 3,
    DoNotDisturb = 4,
    Offline = 5
}

/// <summary>
/// Controls how much location and identity information a node shares with the mesh.
/// </summary>
public enum PrivacyLevel : byte
{
    /// <summary>Share full location and identity.</summary>
    Full = 0,

    /// <summary>Share approximate location (reduced geohash precision).</summary>
    Approximate = 1,

    /// <summary>Share no location, identity visible only to direct peers.</summary>
    Minimal = 2,

    /// <summary>Node is invisible to discovery; only responds to direct addressed packets.</summary>
    Hidden = 3
}

/// <summary>
/// Represents a node participating in the Aether mesh network.
/// </summary>
public sealed class AetherNode
{
    /// <summary>Persistent storage identifier.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Universal Hash ID — the node's globally unique mesh address.</summary>
    public string Uhid { get; set; } = string.Empty;

    /// <summary>Human-readable name for the node.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Ed25519 or X25519 public key bytes for identity verification and key exchange.</summary>
    public byte[] PublicKey { get; set; } = [];

    /// <summary>
    /// The node's Aether Tag — a human-readable, shareable identity address derived from
    /// <see cref="PublicKey"/>. Format: XXXXX-XXXXX (Crockford base-32, e.g. "KXJB7-MN2P4").
    /// Returns an empty string if <see cref="PublicKey"/> has not been set.
    /// </summary>
    public string AetherTag => PublicKey.Length == 32
        ? Identity.AetherTag.FromPublicKey(PublicKey).Value
        : string.Empty;

    /// <summary>Advertised capabilities of this node.</summary>
    public NodeCapabilities Capabilities { get; set; } = NodeCapabilities.None;

    /// <summary>
    /// Reliability score from 0.0 (untrusted) to 1.0 (fully reliable).
    /// Computed from successful relay ratio and uptime.
    /// </summary>
    public double ReliabilityScore { get; set; } = 0.5;

    /// <summary>Whether this node provides internet gateway access to the mesh.</summary>
    public bool IsGateway { get; set; }

    /// <summary>Whether this node is currently reachable.</summary>
    public bool IsOnline { get; set; }

    /// <summary>Geohash of the node's last known position (precision depends on PrivacyLevel).</summary>
    public string? LastGeohash { get; set; }

    /// <summary>Last known latitude. Null if location is not shared.</summary>
    public double? Latitude { get; set; }

    /// <summary>Last known longitude. Null if location is not shared.</summary>
    public double? Longitude { get; set; }

    /// <summary>
    /// The transport network type the node is currently using
    /// (e.g. "ble", "wifi-direct", "cellular", "satellite").
    /// </summary>
    public string? NetworkType { get; set; }

    /// <summary>UTC timestamp of the last time this node was seen on the mesh.</summary>
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;

    /// <summary>Current presence status advertised by this node.</summary>
    public PresenceStatus PresenceStatus { get; set; } = PresenceStatus.Unknown;

    /// <summary>How much information this node shares with the network.</summary>
    public PrivacyLevel PrivacyLevel { get; set; } = PrivacyLevel.Full;

    /// <summary>
    /// Returns true if the node has a specific capability.
    /// </summary>
    public bool HasCapability(NodeCapabilities capability) =>
        (Capabilities & capability) == capability;

    public override string ToString() =>
        $"{DisplayName} ({Uhid}) [{Capabilities}] reliability={ReliabilityScore:F2}";
}

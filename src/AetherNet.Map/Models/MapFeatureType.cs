// SPDX-License-Identifier: MIT
namespace AetherNet.Map.Models;

/// <summary>
/// The kind of thing a map feature describes. Part of a feature's immutable identity (set at genesis),
/// so it is never merged — two CRDTs for the same feature id must agree on it.
/// </summary>
public enum MapFeatureType : byte
{
    /// <summary>A shop / business storefront (owner-authoritative: menu, hours, contact).</summary>
    Storefront = 0,

    /// <summary>A sidewalk / pedestrian accessibility feature (ramp, kerb-cut, obstruction) — observed.</summary>
    SidewalkFeature = 1,

    /// <summary>An environmental reading (air quality, noise, flooding) at a place — observed.</summary>
    EnvironmentalReading = 2,

    /// <summary>A landmark / point of interest.</summary>
    Landmark = 3,

    /// <summary>Anything else; the attribute set carries the meaning.</summary>
    Other = 255,
}

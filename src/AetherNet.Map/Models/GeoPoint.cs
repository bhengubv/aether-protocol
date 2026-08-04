// SPDX-License-Identifier: MIT
namespace AetherNet.Map.Models;

/// <summary>
/// A WGS-84 coordinate plus its precomputed geohash cell. A feature's location is last-write-wins
/// (a shop can be re-pinned), so this travels inside an <c>LwwRegister</c>.
/// </summary>
public readonly record struct GeoPoint(double Latitude, double Longitude, string Geohash)
{
    /// <summary>Build a point from coordinates, encoding the geohash at the given precision.
    /// (Fully qualified because the <see cref="Geohash"/> property shadows the geohash type here.)</summary>
    public static GeoPoint At(double latitude, double longitude, int precision = AetherNet.Map.Geohash.DefaultPrecision)
        => new(latitude, longitude, AetherNet.Map.Geohash.Encode(latitude, longitude, precision));
}

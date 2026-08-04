// SPDX-License-Identifier: MIT
using System.Text;

namespace AetherNet.Map;

/// <summary>
/// Standard (Niemeyer) base-32 geohash: encode a coordinate to a cell string, decode a cell back to its
/// centre, and compute the 8 adjacent cells. Proximity queries use <see cref="RangeEnd"/> to turn a cell
/// prefix into a half-open <c>[cell, RangeEnd(cell))</c> range that a sorted index can scan.
///
/// <para>This is the ONE geohash implementation across every AetherNet language port — it MUST stay
/// byte-identical, so it is pinned by <c>fixtures/map/geohash.json</c>. Do not "optimise" the algorithm
/// in a way that changes output.</para>
///
/// Alphabet: <c>0123456789bcdefghjkmnpqrstuvwxyz</c> (no a, i, l, o). Precision 7 ≈ 150 m cell
/// (the money-bearing coarse cell), precision 9 ≈ 4.8 m (the feature-storage cell).
/// </summary>
public static class Geohash
{
    /// <summary>Geohash base-32 alphabet (excludes a, i, l, o).</summary>
    public const string Base32 = "0123456789bcdefghjkmnpqrstuvwxyz";

    /// <summary>Default storage precision (~4.8 m cell).</summary>
    public const int DefaultPrecision = 9;

    private static readonly int[] BitMask = [16, 8, 4, 2, 1];

    /// <summary>The four cardinal directions used by <see cref="Adjacent"/>.</summary>
    public enum Direction { North, East, South, West }

    /// <summary>
    /// Encode a WGS-84 coordinate to a geohash of the given precision (1–12 chars).
    /// Latitude is clamped to [-90, 90], longitude to [-180, 180].
    /// </summary>
    public static string Encode(double latitude, double longitude, int precision = DefaultPrecision)
    {
        if (precision is < 1 or > 12)
            throw new ArgumentOutOfRangeException(nameof(precision), precision, "Precision must be 1–12.");

        latitude = Math.Clamp(latitude, -90.0, 90.0);
        longitude = Math.Clamp(longitude, -180.0, 180.0);

        double latMin = -90.0, latMax = 90.0, lonMin = -180.0, lonMax = 180.0;
        var sb = new StringBuilder(precision);
        bool even = true;
        int bit = 0, ch = 0;

        while (sb.Length < precision)
        {
            if (even)
            {
                double mid = (lonMin + lonMax) / 2.0;
                if (longitude >= mid) { ch |= BitMask[bit]; lonMin = mid; }
                else lonMax = mid;
            }
            else
            {
                double mid = (latMin + latMax) / 2.0;
                if (latitude >= mid) { ch |= BitMask[bit]; latMin = mid; }
                else latMax = mid;
            }

            even = !even;
            if (bit < 4)
            {
                bit++;
            }
            else
            {
                sb.Append(Base32[ch]);
                bit = 0;
                ch = 0;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Decode a geohash to the centre of its cell. Also returns the cell half-heights so callers can
    /// reconstruct the bounding box if needed.
    /// </summary>
    public static (double Latitude, double Longitude, double LatError, double LonError) DecodeCell(string geohash)
    {
        ArgumentException.ThrowIfNullOrEmpty(geohash);

        double latMin = -90.0, latMax = 90.0, lonMin = -180.0, lonMax = 180.0;
        bool even = true;

        foreach (char c in geohash)
        {
            int cd = Base32.IndexOf(char.ToLowerInvariant(c));
            if (cd < 0)
                throw new FormatException($"'{c}' is not a valid geohash character.");

            for (int i = 0; i < 5; i++)
            {
                int bit = (cd & BitMask[i]) != 0 ? 1 : 0;
                if (even)
                {
                    double mid = (lonMin + lonMax) / 2.0;
                    if (bit == 1) lonMin = mid; else lonMax = mid;
                }
                else
                {
                    double mid = (latMin + latMax) / 2.0;
                    if (bit == 1) latMin = mid; else latMax = mid;
                }
                even = !even;
            }
        }

        return ((latMin + latMax) / 2.0, (lonMin + lonMax) / 2.0,
                (latMax - latMin) / 2.0, (lonMax - lonMin) / 2.0);
    }

    /// <summary>Decode a geohash to the centre coordinate of its cell.</summary>
    public static (double Latitude, double Longitude) Decode(string geohash)
    {
        var (lat, lon, _, _) = DecodeCell(geohash);
        return (lat, lon);
    }

    // Adjacency tables (Niemeyer). Index [direction, parity] where parity = geohash.Length % 2
    // (0 = even length, 1 = odd length).
    private static readonly string[,] NeighbourTable =
    {
        { "p0r21436x8zb9dcf5h7kjnmqesgutwvy", "bc01fg45238967deuvhjyznpkmstqrwx" }, // North
        { "bc01fg45238967deuvhjyznpkmstqrwx", "p0r21436x8zb9dcf5h7kjnmqesgutwvy" }, // East
        { "14365h7k9dcfesgujnmqp0r2twvyx8zb", "238967debc01fg45kmstqrwxuvhjyznp" }, // South
        { "238967debc01fg45kmstqrwxuvhjyznp", "14365h7k9dcfesgujnmqp0r2twvyx8zb" }, // West
    };

    private static readonly string[,] BorderTable =
    {
        { "prxz", "bcfguvyz" }, // North
        { "bcfguvyz", "prxz" }, // East
        { "028b", "0145hjnp" }, // South
        { "0145hjnp", "028b" }, // West
    };

    /// <summary>
    /// The geohash cell immediately adjacent to <paramref name="geohash"/> in the given direction, at the
    /// same precision. Recurses into the parent cell when crossing a base-32 boundary.
    /// </summary>
    public static string Adjacent(string geohash, Direction direction)
    {
        ArgumentException.ThrowIfNullOrEmpty(geohash);
        geohash = geohash.ToLowerInvariant();

        char last = geohash[^1];
        int parity = geohash.Length % 2;
        string parent = geohash[..^1];

        if (BorderTable[(int)direction, parity].IndexOf(last) != -1 && parent.Length > 0)
            parent = Adjacent(parent, direction);

        int idx = NeighbourTable[(int)direction, parity].IndexOf(last);
        if (idx < 0)
            throw new FormatException($"'{last}' is not a valid geohash character.");

        return parent + Base32[idx];
    }

    /// <summary>
    /// The 8 cells surrounding <paramref name="geohash"/> (N, NE, E, SE, S, SW, W, NW), same precision.
    /// Together with the cell itself these are the 9 cells a proximity query scans.
    /// </summary>
    public static IReadOnlyList<string> Neighbours(string geohash)
    {
        string n = Adjacent(geohash, Direction.North);
        string s = Adjacent(geohash, Direction.South);
        string e = Adjacent(geohash, Direction.East);
        string w = Adjacent(geohash, Direction.West);
        return
        [
            n,
            Adjacent(n, Direction.East),  // NE
            e,
            Adjacent(s, Direction.East),  // SE
            s,
            Adjacent(s, Direction.West),  // SW
            w,
            Adjacent(n, Direction.West),  // NW
        ];
    }

    /// <summary>The cell plus its 8 neighbours — the 9 cells covering a proximity search.</summary>
    public static IReadOnlyList<string> CellAndNeighbours(string geohash)
    {
        var result = new List<string>(9) { geohash };
        result.AddRange(Neighbours(geohash));
        return result;
    }

    /// <summary>
    /// The exclusive upper bound of the half-open prefix range for a cell: the smallest string strictly
    /// greater than every string beginning with <paramref name="geohash"/>. Used for indexed proximity
    /// queries — <c>geohash &gt;= cell AND geohash &lt; RangeEnd(cell)</c>. Returns <c>null</c> when the
    /// cell is all-<c>z</c> (no finite upper bound — the query should omit the upper predicate).
    /// </summary>
    public static string? RangeEnd(string geohash)
    {
        ArgumentException.ThrowIfNullOrEmpty(geohash);
        char[] chars = geohash.ToCharArray();
        for (int i = chars.Length - 1; i >= 0; i--)
        {
            int idx = Base32.IndexOf(chars[i]);
            if (idx < 0)
                throw new FormatException($"'{chars[i]}' is not a valid geohash character.");
            if (idx < Base32.Length - 1)
            {
                chars[i] = Base32[idx + 1];
                return new string(chars, 0, i + 1);
            }
            // last char is 'z' — carry into the previous position (drop this char)
        }
        return null; // all 'z' — unbounded above
    }
}

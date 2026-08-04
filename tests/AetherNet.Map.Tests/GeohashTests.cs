// SPDX-License-Identifier: MIT
using AetherNet.Map;
using Xunit;

namespace AetherNet.Map.Tests;

public class GeohashTests
{
    [Theory]
    // Canonical reference vectors (Wikipedia / geohash.org).
    [InlineData(57.64911, 10.40744, 11, "u4pruydqqvj")]
    [InlineData(42.6, -5.6, 5, "ezs42")]
    public void Encode_MatchesCanonicalVectors(double lat, double lon, int precision, string expected)
        => Assert.Equal(expected, Geohash.Encode(lat, lon, precision));

    [Fact]
    public void Encode_Prefix_IsStableAcrossPrecision()
    {
        // A shorter geohash is always a prefix of a longer one for the same point.
        string p11 = Geohash.Encode(57.64911, 10.40744, 11);
        string p7 = Geohash.Encode(57.64911, 10.40744, 7);
        Assert.StartsWith(p7, p11);
    }

    [Theory]
    [InlineData(-90)]
    [InlineData(91)]
    [InlineData(13)]
    public void Encode_RejectsOutOfRangePrecision(int precision)
        => Assert.Throws<ArgumentOutOfRangeException>(() => Geohash.Encode(0, 0, precision));

    [Fact]
    public void EncodeDecode_RoundTrips_WithinCellError()
    {
        const double lat = 51.5007, lon = -0.1246; // London
        string gh = Geohash.Encode(lat, lon, 9);
        var (dlat, dlon, latErr, lonErr) = Geohash.DecodeCell(gh);
        Assert.True(Math.Abs(dlat - lat) <= latErr, $"lat {dlat} vs {lat} err {latErr}");
        Assert.True(Math.Abs(dlon - lon) <= lonErr, $"lon {dlon} vs {lon} err {lonErr}");
    }

    [Fact]
    public void Neighbours_AreDirectionallyCorrect()
    {
        string c = Geohash.Encode(51.5, -0.1, 7); // mid-latitude, far from poles/antimeridian
        var (clat, clon) = Geohash.Decode(c);

        var (nlat, _) = Geohash.Decode(Geohash.Adjacent(c, Geohash.Direction.North));
        var (slat, _) = Geohash.Decode(Geohash.Adjacent(c, Geohash.Direction.South));
        var (_, elon) = Geohash.Decode(Geohash.Adjacent(c, Geohash.Direction.East));
        var (_, wlon) = Geohash.Decode(Geohash.Adjacent(c, Geohash.Direction.West));

        Assert.True(nlat > clat, "north latitude should increase");
        Assert.True(slat < clat, "south latitude should decrease");
        Assert.True(elon > clon, "east longitude should increase");
        Assert.True(wlon < clon, "west longitude should decrease");
    }

    [Fact]
    public void Adjacent_NorthThenSouth_ReturnsToOrigin()
    {
        string c = Geohash.Encode(51.5, -0.1, 7);
        Assert.Equal(c, Geohash.Adjacent(Geohash.Adjacent(c, Geohash.Direction.North), Geohash.Direction.South));
        Assert.Equal(c, Geohash.Adjacent(Geohash.Adjacent(c, Geohash.Direction.East), Geohash.Direction.West));
    }

    [Fact]
    public void CellAndNeighbours_AreNineDistinctCells()
    {
        string c = Geohash.Encode(51.5, -0.1, 7);
        var cells = Geohash.CellAndNeighbours(c);
        Assert.Equal(9, cells.Count);
        Assert.Equal(9, cells.Distinct().Count());
        Assert.Contains(c, cells);
    }

    [Theory]
    [InlineData("u4pru", "u4prv")] // increment last char
    [InlineData("gzz", "h")]       // carry across trailing 'z'
    [InlineData("b", "c")]
    public void RangeEnd_ComputesHalfOpenUpperBound(string cell, string expected)
        => Assert.Equal(expected, Geohash.RangeEnd(cell));

    [Fact]
    public void RangeEnd_AllZ_IsUnbounded()
        => Assert.Null(Geohash.RangeEnd("zzz"));

    [Fact]
    public void RangeEnd_BracketsAllPointsInCell()
    {
        // Every higher-precision geohash inside a cell sorts within [cell, RangeEnd(cell)).
        string cell = Geohash.Encode(51.5, -0.1, 5);
        string inside = Geohash.Encode(51.5, -0.1, 9);
        string? end = Geohash.RangeEnd(cell);
        Assert.StartsWith(cell, inside);
        Assert.True(string.CompareOrdinal(inside, cell) >= 0);
        Assert.True(end is null || string.CompareOrdinal(inside, end) < 0);
    }
}

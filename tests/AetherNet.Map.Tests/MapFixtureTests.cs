// SPDX-License-Identifier: MIT
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AetherNet.Map;
using Xunit;

namespace AetherNet.Map.Tests;

/// <summary>
/// Cross-language parity gate for the geohash primitive: the committed fixtures/map/geohash.json pins the
/// exact encode outputs and cell+neighbour sets that EVERY language SDK must reproduce byte-for-byte.
/// The C# reference asserts it still reproduces the committed file (so an accidental algorithm change is
/// caught here). Regenerate the file with REGEN_FIXTURES=1 when intentionally changing the vectors.
/// </summary>
public class MapFixtureTests
{
    private static readonly (double Lat, double Lon, int Precision)[] EncodeCases =
    [
        (57.64911, 10.40744, 11),
        (42.6, -5.6, 5),
        (51.5074, -0.1278, 9),
        (48.8566, 2.3522, 7),
        (-33.9249, 18.4241, 8),   // Cape Town
        (35.6895, 139.6917, 10),  // Tokyo
        (0.0, 0.0, 6),
    ];

    private static readonly string[] NeighbourCases = ["u4pruy", "gcpvj0", "u120fw", "s0000"];

    private sealed record EncodeVec(
        [property: JsonPropertyName("lat")] double Lat,
        [property: JsonPropertyName("lon")] double Lon,
        [property: JsonPropertyName("precision")] int Precision,
        [property: JsonPropertyName("geohash")] string Geohash);

    private sealed record NeighbourVec(
        [property: JsonPropertyName("geohash")] string Geohash,
        [property: JsonPropertyName("cell_and_neighbours")] List<string> CellAndNeighbours);

    private sealed record GeohashFile(
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("encode")] List<EncodeVec> Encode,
        [property: JsonPropertyName("neighbours")] List<NeighbourVec> Neighbours);

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [Fact]
    public void RegenerateGeohashFixture()
    {
        if (Environment.GetEnvironmentVariable("REGEN_FIXTURES") != "1")
            return; // no-op in normal runs; set REGEN_FIXTURES=1 to (re)write the committed fixture

        var file = new GeohashFile(
            "Canonical geohash encode + cell/neighbour vectors (aether-map). 'geohash' is Geohash.Encode(lat,lon,precision); "
            + "'cell_and_neighbours' is the cell followed by its 8 neighbours in order N,NE,E,SE,S,SW,W,NW. "
            + "Alphabet 0123456789bcdefghjkmnpqrstuvwxyz. Every language SDK MUST reproduce these exactly.",
            EncodeCases.Select(c => new EncodeVec(c.Lat, c.Lon, c.Precision, Geohash.Encode(c.Lat, c.Lon, c.Precision))).ToList(),
            NeighbourCases.Select(g => new NeighbourVec(g, Geohash.CellAndNeighbours(g).ToList())).ToList());

        var dir = MapFixtureDir(create: true);
        File.WriteAllText(Path.Combine(dir, "geohash.json"), JsonSerializer.Serialize(file, WriteOpts) + "\n");
    }

    [Fact]
    public void Geohash_Encode_ReproducesFixture()
    {
        foreach (var v in Load().Encode)
            Assert.Equal(v.Geohash, Geohash.Encode(v.Lat, v.Lon, v.Precision));
    }

    [Fact]
    public void Geohash_CellAndNeighbours_ReproduceFixture()
    {
        foreach (var v in Load().Neighbours)
            Assert.Equal(v.CellAndNeighbours, Geohash.CellAndNeighbours(v.Geohash).ToList());
    }

    private static GeohashFile Load()
    {
        var path = Path.Combine(MapFixtureDir(create: false), "geohash.json");
        return JsonSerializer.Deserialize<GeohashFile>(File.ReadAllText(path))!;
    }

    private static string MapFixtureDir(bool create)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12; i++)
        {
            var fixtures = Path.Combine(dir, "fixtures");
            if (Directory.Exists(fixtures))
            {
                var map = Path.Combine(fixtures, "map");
                if (create) Directory.CreateDirectory(map);
                return map;
            }
            var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (parent is null || parent == dir) break;
            dir = parent;
        }
        throw new DirectoryNotFoundException("Could not locate the 'fixtures' directory from " + AppContext.BaseDirectory);
    }
}

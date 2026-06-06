// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;
using AetherMesh.Protocol;
using Xunit;

namespace AetherMesh.Core.Tests;

/// <summary>
/// Cross-language wire-format fixture verifier. Reads fixtures/inputs.json and
/// fixtures/expected/*.bin (committed to the repo) and asserts that this
/// language's PacketSerializer produces the same bytes for each canonical
/// input. See fixtures/README.md.
/// </summary>
public class FixtureTests
{
    private record FixtureInput(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("type")] int Type,
        [property: JsonPropertyName("source_uhid")] string SourceUhid,
        [property: JsonPropertyName("destination_uhid")] string DestinationUhid,
        [property: JsonPropertyName("ttl")] int Ttl,
        [property: JsonPropertyName("priority")] int Priority,
        [property: JsonPropertyName("payload_hex")] string PayloadHex,
        [property: JsonPropertyName("packet_nonce_hex")] string PacketNonceHex,
        [property: JsonPropertyName("signature_hex")] string SignatureHex,
        [property: JsonPropertyName("timestamp_ms")] long TimestampMs,
        [property: JsonPropertyName("protocol_version")] int ProtocolVersion);

    private static byte[] HexToBytes(string hex)
    {
        if (string.IsNullOrEmpty(hex)) return Array.Empty<byte>();
        var n = hex.Length / 2;
        var bytes = new byte[n];
        for (var i = 0; i < n; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }

    private static string FixturesDir()
    {
        // Tests run from tests/AetherMesh.Core.Tests/bin/Debug/netN.0/ — walk up
        // until we find the repo root (the first ancestor containing a
        // fixtures/inputs.json).
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir, "fixtures", "inputs.json");
            if (File.Exists(candidate)) return Path.Combine(dir, "fixtures");
            var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (parent is null || parent == dir) break;
            dir = parent;
        }
        throw new FileNotFoundException("Could not locate fixtures/inputs.json from " + AppContext.BaseDirectory);
    }

    private static IEnumerable<FixtureInput> LoadInputs()
    {
        var path = Path.Combine(FixturesDir(), "inputs.json");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<List<FixtureInput>>(json)!;
    }

    public static IEnumerable<object[]> AllFixtures() =>
        LoadInputs().Select(x => new object[] { x.Name });

    private static MeshPacket BuildPacket(FixtureInput input) => new()
    {
        Id = Guid.Parse(input.Id),
        Type = (PacketType)(byte)input.Type,
        SourceUhid = input.SourceUhid,
        DestinationUhid = input.DestinationUhid,
        Ttl = input.Ttl,
        Priority = (byte)input.Priority,
        Payload = HexToBytes(input.PayloadHex),
        PacketNonce = HexToBytes(input.PacketNonceHex),
        Signature = HexToBytes(input.SignatureHex),
        TimestampMs = input.TimestampMs,
        ProtocolVersion = (byte)input.ProtocolVersion,
    };

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Serialize_MatchesExpectedBytes(string name)
    {
        var input = LoadInputs().Single(x => x.Name == name);
        var packet = BuildPacket(input);
        var serialized = PacketSerializer.Serialize(packet);

        var expected = File.ReadAllBytes(Path.Combine(FixturesDir(), "expected", name + ".bin"));
        Assert.Equal(expected, serialized);
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Deserialize_FromExpectedBytes_MatchesInputFields(string name)
    {
        var input = LoadInputs().Single(x => x.Name == name);
        var bytes = File.ReadAllBytes(Path.Combine(FixturesDir(), "expected", name + ".bin"));
        var got = PacketSerializer.Deserialize(bytes);

        Assert.Equal(Guid.Parse(input.Id), got.Id);
        Assert.Equal((PacketType)(byte)input.Type, got.Type);
        Assert.Equal(input.SourceUhid, got.SourceUhid);
        Assert.Equal(input.DestinationUhid, got.DestinationUhid);
        Assert.Equal(input.Ttl, got.Ttl);
        Assert.Equal((byte)input.Priority, got.Priority);
        Assert.Equal(HexToBytes(input.PayloadHex), got.Payload);
        Assert.Equal(HexToBytes(input.PacketNonceHex), got.PacketNonce);
        Assert.Equal(HexToBytes(input.SignatureHex), got.Signature);
        Assert.Equal(input.TimestampMs, got.TimestampMs);
        Assert.Equal((byte)input.ProtocolVersion, got.ProtocolVersion);
    }
}

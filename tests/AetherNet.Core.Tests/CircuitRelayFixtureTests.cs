// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AetherNet.CircuitRelay;
using AetherNet.Models;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Cross-language circuit-relay-v2 wire-format verifier. Reads
/// fixtures/circuit-relay/inputs.json + expected/*.bin (the Go-oracle output committed to
/// the repo) and asserts this language's <see cref="RelayFrameSerializer"/> produces the
/// same bytes for each canonical input and round-trips every field. Every other language
/// SDK runs an equivalent test against the same .bin — that is the 8-language parity gate.
/// </summary>
public class CircuitRelayFixtureTests
{
    private record RelayFixtureInput(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("type")] int Type,
        [property: JsonPropertyName("status")] int Status,
        [property: JsonPropertyName("source_uhid")] string? SourceUhid,
        [property: JsonPropertyName("destination_uhid")] string? DestinationUhid,
        [property: JsonPropertyName("relay_uhid")] string? RelayUhid,
        [property: JsonPropertyName("connection_id")] string? ConnectionId,
        [property: JsonPropertyName("reservation_expires_at_ms")] long ReservationExpiresAtMs,
        [property: JsonPropertyName("limit_duration_seconds")] int LimitDurationSeconds,
        [property: JsonPropertyName("limit_data_bytes")] long LimitDataBytes,
        [property: JsonPropertyName("payload_hex")] string? PayloadHex,
        [property: JsonPropertyName("payload_len")] int PayloadLen);

    private static byte[] HexToBytes(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return Array.Empty<byte>();
        var n = hex.Length / 2;
        var bytes = new byte[n];
        for (var i = 0; i < n; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }

    private static byte[] PayloadFor(RelayFixtureInput input)
    {
        if (input.PayloadLen > 0)
        {
            var b = new byte[input.PayloadLen];
            for (var i = 0; i < b.Length; i++) b[i] = (byte)(i % 256);
            return b;
        }
        return HexToBytes(input.PayloadHex);
    }

    private static Guid ConnId(string? s) => string.IsNullOrEmpty(s) ? Guid.Empty : Guid.Parse(s);

    private static string FixturesDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir, "fixtures", "circuit-relay", "inputs.json");
            if (File.Exists(candidate)) return Path.Combine(dir, "fixtures", "circuit-relay");
            var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (parent is null || parent == dir) break;
            dir = parent;
        }
        throw new FileNotFoundException("Could not locate fixtures/circuit-relay/inputs.json from " + AppContext.BaseDirectory);
    }

    private static List<RelayFixtureInput> LoadInputs()
    {
        var path = Path.Combine(FixturesDir(), "inputs.json");
        return JsonSerializer.Deserialize<List<RelayFixtureInput>>(File.ReadAllText(path))!;
    }

    public static IEnumerable<object[]> AllFixtures() =>
        LoadInputs().Select(x => new object[] { x.Name });

    private static RelayFrame ToFrame(RelayFixtureInput i) => new()
    {
        Type = (RelayMessageType)(byte)i.Type,
        Status = (RelayStatus)(byte)i.Status,
        SourceUhid = i.SourceUhid ?? string.Empty,
        DestinationUhid = i.DestinationUhid ?? string.Empty,
        RelayUhid = i.RelayUhid ?? string.Empty,
        ConnectionId = ConnId(i.ConnectionId),
        ReservationExpiresAtMs = i.ReservationExpiresAtMs,
        LimitDurationSeconds = i.LimitDurationSeconds,
        LimitDataBytes = i.LimitDataBytes,
        Payload = PayloadFor(i),
    };

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Serialize_MatchesExpectedBytes(string name)
    {
        var input = LoadInputs().Single(x => x.Name == name);
        var serialized = RelayFrameSerializer.Serialize(ToFrame(input));
        var expected = File.ReadAllBytes(Path.Combine(FixturesDir(), "expected", name + ".bin"));
        Assert.Equal(expected, serialized);
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Deserialize_FromExpectedBytes_MatchesInputFields(string name)
    {
        var input = LoadInputs().Single(x => x.Name == name);
        var bytes = File.ReadAllBytes(Path.Combine(FixturesDir(), "expected", name + ".bin"));
        var f = RelayFrameSerializer.Deserialize(bytes);

        Assert.Equal((RelayMessageType)(byte)input.Type, f.Type);
        Assert.Equal((RelayStatus)(byte)input.Status, f.Status);
        Assert.Equal(input.SourceUhid ?? string.Empty, f.SourceUhid);
        Assert.Equal(input.DestinationUhid ?? string.Empty, f.DestinationUhid);
        Assert.Equal(input.RelayUhid ?? string.Empty, f.RelayUhid);
        Assert.Equal(ConnId(input.ConnectionId), f.ConnectionId);
        Assert.Equal(input.ReservationExpiresAtMs, f.ReservationExpiresAtMs);
        Assert.Equal(input.LimitDurationSeconds, f.LimitDurationSeconds);
        Assert.Equal(input.LimitDataBytes, f.LimitDataBytes);
        Assert.Equal(PayloadFor(input), f.Payload);
    }
}

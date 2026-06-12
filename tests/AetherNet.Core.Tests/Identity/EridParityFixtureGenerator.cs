// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Text;
using System.Text.Json;
using AetherNet.Identity;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// One-shot generator for the cross-language ERID parity fixtures. Run explicitly:
///   dotnet test --filter "FullyQualifiedName~EridParityFixtureGenerator"
/// Writes <c>fixtures/erid/vectors.json</c> from the C# reference — the single source of truth that
/// every language port (Go/Python/Rust/Swift/Kotlin/TS/C) must reproduce byte-for-byte.
/// </summary>
public class EridParityFixtureGenerator
{
    [Fact]
    public void Generate()
    {
        var secret = Encoding.ASCII.GetBytes("aethernet-erid-parity-v1");
        var routingKey = EphemeralRoutingId.DeriveRoutingKey(secret);

        static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

        var vectors = new
        {
            note = "Canonical ERID parity vectors from the C# reference. Every language port MUST reproduce these byte-for-byte.",
            secret_ascii = "aethernet-erid-parity-v1",
            routing_key_hex = Hex(routingKey),
            epoch_seconds = EphemeralRoutingId.DefaultEpochSeconds,
            erid_length = EphemeralRoutingId.DefaultLength,
            erids_by_epoch = new[]
            {
                new { epoch = 0L,    erid = EphemeralRoutingId.DeriveForEpoch(routingKey, 0) },
                new { epoch = 1L,    erid = EphemeralRoutingId.DeriveForEpoch(routingKey, 1) },
                new { epoch = 100L,  erid = EphemeralRoutingId.DeriveForEpoch(routingKey, 100) },
                new { epoch = 1371L, erid = EphemeralRoutingId.DeriveForEpoch(routingKey, 1371) },
            },
            derive_by_unixseconds = new[]
            {
                new { unix = 1000L, erid = EphemeralRoutingId.Derive(routingKey, 1000) },
                new { unix = 2000L, erid = EphemeralRoutingId.Derive(routingKey, 2000) },
            },
            // EridAnnouncementCodec.Encode(routingKey) with default epoch/length — the wire frame.
            announcement_encode_hex = Hex(EridAnnouncementCodec.Encode(routingKey)),
        };

        var json = JsonSerializer.Serialize(vectors, new JsonSerializerOptions { WriteIndented = true });
        var dir = "C:/Dev/Solutions/com.bhengubv/aether-protocol/fixtures/erid";
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "vectors.json"), json);
    }
}

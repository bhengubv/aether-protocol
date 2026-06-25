// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;
using AetherNet.Dtn;
using AetherNet.Models;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Cross-language DTN-envelope wire-format verifier. Reads fixtures/dtn/inputs.json
/// and fixtures/dtn/expected/*.bin (the Go-oracle output, committed to the repo) and
/// asserts that this language's <see cref="DtnEnvelopeSerializer"/> produces the same
/// bytes for each canonical input and round-trips every field.
/// </summary>
public class DtnFixtureTests
{
    private record DtnFixtureInput(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("priority")] int Priority,
        [property: JsonPropertyName("status")] int Status,
        [property: JsonPropertyName("copy_count")] int CopyCount,
        [property: JsonPropertyName("max_copies")] int MaxCopies,
        [property: JsonPropertyName("hop_count")] int HopCount,
        [property: JsonPropertyName("created_at_ms")] long CreatedAtMs,
        [property: JsonPropertyName("expires_at_ms")] long ExpiresAtMs,
        [property: JsonPropertyName("sender_uhid")] string? SenderUhid,
        [property: JsonPropertyName("recipient_uhid")] string? RecipientUhid,
        [property: JsonPropertyName("sender_geohash")] string? SenderGeohash,
        [property: JsonPropertyName("recipient_last_geohash")] string? RecipientLastGeohash,
        [property: JsonPropertyName("encrypted_payload_hex")] string? EncryptedPayloadHex,
        [property: JsonPropertyName("encrypted_payload_len")] int EncryptedPayloadLen,
        [property: JsonPropertyName("bundle_id")] string? BundleId,
        [property: JsonPropertyName("accepted")] bool Accepted,
        [property: JsonPropertyName("total_hops")] int TotalHops,
        [property: JsonPropertyName("total_custody_transfers")] int TotalCustodyTransfers,
        [property: JsonPropertyName("delivered_at_ms")] long DeliveredAtMs);

    private static byte[] HexToBytes(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return Array.Empty<byte>();
        var n = hex.Length / 2;
        var bytes = new byte[n];
        for (var i = 0; i < n; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }

    private static byte[] PayloadFor(DtnFixtureInput input)
    {
        if (input.EncryptedPayloadLen > 0)
        {
            var b = new byte[input.EncryptedPayloadLen];
            for (var i = 0; i < b.Length; i++) b[i] = (byte)(i % 256);
            return b;
        }
        return HexToBytes(input.EncryptedPayloadHex);
    }

    private static string FixturesDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir, "fixtures", "dtn", "inputs.json");
            if (File.Exists(candidate)) return Path.Combine(dir, "fixtures", "dtn");
            var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (parent is null || parent == dir) break;
            dir = parent;
        }
        throw new FileNotFoundException("Could not locate fixtures/dtn/inputs.json from " + AppContext.BaseDirectory);
    }

    private static List<DtnFixtureInput> LoadInputs()
    {
        var path = Path.Combine(FixturesDir(), "inputs.json");
        return JsonSerializer.Deserialize<List<DtnFixtureInput>>(File.ReadAllText(path))!;
    }

    public static IEnumerable<object[]> AllFixtures() =>
        LoadInputs().Select(x => new object[] { x.Name });

    private static byte[] Serialize(DtnFixtureInput input) => input.Kind switch
    {
        "bundle" => DtnEnvelopeSerializer.SerializeBundle(new DtnBundle
        {
            Id = Guid.Parse(input.Id!),
            SenderUhid = input.SenderUhid ?? string.Empty,
            RecipientUhid = input.RecipientUhid ?? string.Empty,
            EncryptedPayload = PayloadFor(input),
            Priority = (BundlePriority)(byte)input.Priority,
            Status = (BundleStatus)(byte)input.Status,
            CopyCount = input.CopyCount,
            MaxCopies = input.MaxCopies,
            SenderGeohash = input.SenderGeohash,
            RecipientLastGeohash = input.RecipientLastGeohash,
            HopCount = input.HopCount,
            CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(input.CreatedAtMs).UtcDateTime,
            ExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(input.ExpiresAtMs).UtcDateTime,
        }),
        "custody_ack" => DtnEnvelopeSerializer.SerializeCustodyAck(Guid.Parse(input.BundleId!), input.Accepted),
        "delivery_receipt" => DtnEnvelopeSerializer.SerializeDeliveryReceipt(new DtnDeliveryReceipt
        {
            BundleId = Guid.Parse(input.BundleId!),
            RecipientUhid = input.RecipientUhid ?? string.Empty,
            TotalHops = input.TotalHops,
            TotalCustodyTransfers = input.TotalCustodyTransfers,
            DeliveredAt = DateTimeOffset.FromUnixTimeMilliseconds(input.DeliveredAtMs).UtcDateTime,
        }),
        _ => throw new InvalidOperationException($"unknown kind {input.Kind}"),
    };

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Serialize_MatchesExpectedBytes(string name)
    {
        var input = LoadInputs().Single(x => x.Name == name);
        var serialized = Serialize(input);
        var expected = File.ReadAllBytes(Path.Combine(FixturesDir(), "expected", name + ".bin"));
        Assert.Equal(expected, serialized);
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Deserialize_FromExpectedBytes_MatchesInputFields(string name)
    {
        var input = LoadInputs().Single(x => x.Name == name);
        var bytes = File.ReadAllBytes(Path.Combine(FixturesDir(), "expected", name + ".bin"));

        switch (input.Kind)
        {
            case "bundle":
                var b = DtnEnvelopeSerializer.DeserializeBundle(bytes);
                Assert.Equal(Guid.Parse(input.Id!), b.Id);
                Assert.Equal((BundlePriority)(byte)input.Priority, b.Priority);
                Assert.Equal((BundleStatus)(byte)input.Status, b.Status);
                Assert.Equal(input.CopyCount, b.CopyCount);
                Assert.Equal(input.MaxCopies, b.MaxCopies);
                Assert.Equal(input.HopCount, b.HopCount);
                Assert.Equal(input.CreatedAtMs, new DateTimeOffset(b.CreatedAt, TimeSpan.Zero).ToUnixTimeMilliseconds());
                Assert.Equal(input.ExpiresAtMs, new DateTimeOffset(b.ExpiresAt, TimeSpan.Zero).ToUnixTimeMilliseconds());
                Assert.Equal(input.SenderUhid ?? string.Empty, b.SenderUhid);
                Assert.Equal(input.RecipientUhid ?? string.Empty, b.RecipientUhid);
                Assert.Equal(input.SenderGeohash ?? string.Empty, b.SenderGeohash);
                Assert.Equal(input.RecipientLastGeohash ?? string.Empty, b.RecipientLastGeohash);
                Assert.Equal(PayloadFor(input), b.EncryptedPayload);
                break;
            case "custody_ack":
                var (ackId, accepted) = DtnEnvelopeSerializer.DeserializeCustodyAck(bytes);
                Assert.Equal(Guid.Parse(input.BundleId!), ackId);
                Assert.Equal(input.Accepted, accepted);
                break;
            case "delivery_receipt":
                var r = DtnEnvelopeSerializer.DeserializeDeliveryReceipt(bytes);
                Assert.Equal(Guid.Parse(input.BundleId!), r.BundleId);
                Assert.Equal(input.RecipientUhid ?? string.Empty, r.RecipientUhid);
                Assert.Equal(input.TotalHops, r.TotalHops);
                Assert.Equal(input.TotalCustodyTransfers, r.TotalCustodyTransfers);
                Assert.Equal(input.DeliveredAtMs, new DateTimeOffset(r.DeliveredAt, TimeSpan.Zero).ToUnixTimeMilliseconds());
                break;
        }
    }
}

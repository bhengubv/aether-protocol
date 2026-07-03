// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;
using AetherNet.Security.Sync;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Verifies the decentralised multi-device sync core against the shared parity
/// fixture (fixtures/sync/vectors.json): the SyncRecord binary envelope is
/// byte-identical, last-write-wins reconciliation picks the same winner on every
/// device, and identity-signed DeviceLink records sign/serialize/verify the same
/// way across every AetherNet SDK. No server anywhere in the loop.
/// </summary>
public class SyncFixtureTests
{
    private record SyncRecJson(
        [property: JsonPropertyName("record_id")] string RecordId,
        [property: JsonPropertyName("device_id")] string DeviceId,
        [property: JsonPropertyName("op")] int Op,
        [property: JsonPropertyName("item_id")] string ItemId,
        [property: JsonPropertyName("logical_clock")] long LogicalClock,
        [property: JsonPropertyName("created_at_ms")] long CreatedAtMs,
        [property: JsonPropertyName("payload_hex")] string? PayloadHex,
        [property: JsonPropertyName("serialized_hex")] string? SerializedHex);

    private record ReconcileJson(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("records")] List<SyncRecJson> Records,
        [property: JsonPropertyName("winner_record_id")] string WinnerRecordId);

    private record DeviceLinkJson(
        [property: JsonPropertyName("device_id")] string DeviceId,
        [property: JsonPropertyName("device_public_key")] string DevicePublicKey,
        [property: JsonPropertyName("issued_at_ms")] long IssuedAtMs,
        [property: JsonPropertyName("signed_body_hex")] string SignedBodyHex,
        [property: JsonPropertyName("signature_hex")] string SignatureHex,
        [property: JsonPropertyName("serialized_hex")] string SerializedHex);

    private record VectorFile(
        [property: JsonPropertyName("identity_private")] string IdentityPrivate,
        [property: JsonPropertyName("identity_public")] string IdentityPublic,
        [property: JsonPropertyName("wrong_identity_public")] string WrongIdentityPublic,
        [property: JsonPropertyName("sync_records")] List<SyncRecJson> SyncRecords,
        [property: JsonPropertyName("reconcile")] List<ReconcileJson> Reconcile,
        [property: JsonPropertyName("device_links")] List<DeviceLinkJson> DeviceLinks);

    private static string Dir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var c = Path.Combine(dir, "fixtures", "sync", "vectors.json");
            if (File.Exists(c)) return Path.Combine(dir, "fixtures", "sync");
            var p = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (p is null || p == dir) break;
            dir = p;
        }
        throw new FileNotFoundException("Could not locate fixtures/sync/vectors.json from " + AppContext.BaseDirectory);
    }

    private static VectorFile Load() =>
        JsonSerializer.Deserialize<VectorFile>(File.ReadAllText(Path.Combine(Dir(), "vectors.json")))!;

    private static byte[] FromHex(string h) => h.Length == 0 ? Array.Empty<byte>() : Convert.FromHexString(h);
    private static string ToHex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    private static SyncRecord ToRecord(SyncRecJson j) => new(
        Guid.Parse(j.RecordId), j.DeviceId, (SyncOp)j.Op, j.ItemId,
        j.LogicalClock, j.CreatedAtMs, FromHex(j.PayloadHex ?? ""));

    [Fact]
    public void SyncRecord_Serialize_And_RoundTrip_MatchVectors()
    {
        foreach (var j in Load().SyncRecords)
        {
            var rec = ToRecord(j);
            Assert.Equal(j.SerializedHex, ToHex(SyncRecordSerializer.Serialize(rec)));

            var back = SyncRecordSerializer.Deserialize(FromHex(j.SerializedHex!));
            Assert.Equal(rec.RecordId, back.RecordId);
            Assert.Equal(rec.DeviceId, back.DeviceId);
            Assert.Equal(rec.Op, back.Op);
            Assert.Equal(rec.ItemId, back.ItemId);
            Assert.Equal(rec.LogicalClock, back.LogicalClock);
            Assert.Equal(rec.CreatedAtMs, back.CreatedAtMs);
            Assert.Equal(rec.EncryptedPayload, back.EncryptedPayload);
        }
    }

    [Fact]
    public void Reconcile_PicksDeterministicWinner()
    {
        foreach (var s in Load().Reconcile)
        {
            var recs = s.Records.Select(ToRecord).ToList();
            Assert.Equal(Guid.Parse(s.WinnerRecordId), SyncReconciler.Winner(recs).RecordId);

            // Order must not matter — reverse the input, same winner.
            recs.Reverse();
            Assert.Equal(Guid.Parse(s.WinnerRecordId), SyncReconciler.Winner(recs).RecordId);

            var merged = SyncReconciler.Merge(recs);
            Assert.Equal(Guid.Parse(s.WinnerRecordId), merged["x"].RecordId);
        }
    }

    [Fact]
    public void DeviceLink_Sign_Serialize_Verify_MatchVectors()
    {
        var f = Load();
        var idPriv = FromHex(f.IdentityPrivate);
        var idPub = FromHex(f.IdentityPublic);
        var wrongPub = FromHex(f.WrongIdentityPublic);

        foreach (var j in f.DeviceLinks)
        {
            var dpk = FromHex(j.DevicePublicKey);
            Assert.Equal(j.SignedBodyHex, ToHex(DeviceLinkCodec.SignedBody(j.DeviceId, dpk, j.IssuedAtMs)));

            var link = DeviceLinkCodec.Create(j.DeviceId, dpk, j.IssuedAtMs, idPriv);
            Assert.Equal(j.SignatureHex, ToHex(link.Signature));           // Ed25519 is deterministic
            Assert.Equal(j.SerializedHex, ToHex(DeviceLinkCodec.Serialize(link)));

            Assert.True(DeviceLinkCodec.Verify(link, idPub));
            Assert.False(DeviceLinkCodec.Verify(link, wrongPub));

            var back = DeviceLinkCodec.Deserialize(FromHex(j.SerializedHex));
            Assert.Equal(j.DeviceId, back.DeviceId);
            Assert.Equal(dpk, back.DevicePublicKey);
            Assert.Equal(j.IssuedAtMs, back.IssuedAtMs);
            Assert.True(DeviceLinkCodec.Verify(back, idPub));
        }
    }

    [Fact]
    public void SyncRecord_Rejects_BadVersion()
    {
        var good = SyncRecordSerializer.Serialize(ToRecord(Load().SyncRecords[0]));
        good[0] = 0x02;
        Assert.Throws<FormatException>(() => SyncRecordSerializer.Deserialize(good));
    }
}

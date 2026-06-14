// SPDX-License-Identifier: MIT
//
// Deterministic cross-language test-fixture generator + self-check for three features that every
// language port (Go / Python / Rust / Swift / Kotlin / TS / C / HarmonyOS) must reproduce byte-for-byte:
//
//   1. Tipping   — TipPacketPayload.BuildCanonicalData() + Ed25519 signature  -> fixtures/tipping/tip_packet_basic.json
//   2. Vault     — systematic Cauchy-Reed-Solomon (K=10, M=4) encode/decode   -> fixtures/vault/reed_solomon_basic.json
//   3. PoV token — PoVTokenCodec.BuildSignableTokenData() + Ed25519 signature  -> fixtures/market/pov_token_basic.json
//
// SAME PATTERN as fixtures/signal/ and fixtures/erid/: the C# reference implementations are the single
// source of truth; this generator runs them against FIXED inputs and FIXED Ed25519 seeds (so the keys
// and signatures are fully deterministic and reproducible), emits the expected bytes as hex-encoded JSON,
// and then re-reads its own output and verifies it (canonical bytes re-derive equal, Ed25519 signatures
// verify, RS recovers from every K-survivor subset, the K-1 subset fails). Keep this generator in the
// tree so the fixtures can be regenerated whenever the reference impl intentionally changes (a wire-break
// event — re-verify every language afterwards).
//
// Run (writes the files AND runs the self-check — both must stay green):
//   dotnet test tests/AetherNet.Core.Tests/AetherNet.Core.Tests.csproj -c Debug --nologo \
//     --filter "FullyQualifiedName~CrossLangFixtureGenerator"

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using AetherNet.Incentive;          // TipPacketPayload  (public, AetherNet.Core)
using AetherNet.Market;             // PoVTokenCodec     (internal -> InternalsVisibleTo AetherNet.Core.Tests)
using AetherNet.Market.Models;      // PoVToken, PoVTransportType
using AetherNet.Security.Services;  // Ed25519SigningService (public)
using AetherNet.Vault;              // ReedSolomonCodec  (internal -> InternalsVisibleTo AetherNet.Core.Tests)
using NSec.Cryptography;            // deterministic public-key derivation from a fixed 32-byte seed
using Xunit;

namespace AetherNet.Core.Tests.FixtureGenerators;

/// <summary>
/// One-shot generator for the cross-language tipping / vault / PoV parity fixtures, plus an in-process
/// self-check that the C# reference reads its own emitted fixtures back and verifies them.
/// </summary>
public sealed class CrossLangFixtureGenerator
{
    // ── Fixed Ed25519 seeds (32 raw bytes). Hardcoded so keys + signatures are deterministic. ──────────
    // Tipper identity seed = bytes 0x00..0x1f.
    private static readonly byte[] TipperSeed =
        Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    // PoV witness identity seed = bytes 0x20..0x3f.
    private static readonly byte[] WitnessSeed =
        Enumerable.Range(0x20, 32).Select(i => (byte)i).ToArray();

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
    private static byte[] FromHex(string h) => Convert.FromHexString(h);

    /// <summary>
    /// Derive the 32-byte Ed25519 public key for a fixed seed using the SAME NSec/libsodium path the
    /// production <see cref="Ed25519SigningService"/> uses internally (import RawPrivateKey, export
    /// RawPublicKey) — so the public key is exactly the one a node booting with this seed would have.
    /// </summary>
    private static byte[] PublicKeyFromSeed(byte[] seed)
    {
        using var key = Key.Import(SignatureAlgorithm.Ed25519, seed, KeyBlobFormat.RawPrivateKey,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        return key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────────────
    //  THE GENERATOR
    // ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Generate_And_SelfCheck()
    {
        var fixturesRoot = FindFixturesRoot();

        WriteTippingFixture(Path.Combine(fixturesRoot, "tipping", "tip_packet_basic.json"));
        WriteVaultFixture(Path.Combine(fixturesRoot, "vault", "reed_solomon_basic.json"));
        WritePoVFixture(Path.Combine(fixturesRoot, "market", "pov_token_basic.json"));

        // Self-check: re-read each emitted fixture and verify the C# reference agrees with it.
        VerifyTippingFixture(Path.Combine(fixturesRoot, "tipping", "tip_packet_basic.json"));
        VerifyVaultFixture(Path.Combine(fixturesRoot, "vault", "reed_solomon_basic.json"));
        VerifyPoVFixture(Path.Combine(fixturesRoot, "market", "pov_token_basic.json"));
    }

    // ── 1. TIPPING ──────────────────────────────────────────────────────────────────────────────────

    private static TipPacketPayload BuildTip(
        string tipper, string recipient, decimal amount, string traffic, Guid? refId, long unixMs) => new()
    {
        TipperUhid    = tipper,
        RecipientUhid = recipient,
        Amount        = amount,
        TrafficType   = traffic,
        ReferenceId   = refId,
        Timestamp     = DateTimeOffset.FromUnixTimeMilliseconds(unixMs),
    };

    private void WriteTippingFixture(string path)
    {
        var tipperPub = PublicKeyFromSeed(TipperSeed);

        // Fixed, varied cases — incl. a null reference_id and a high-precision/large amount.
        var inputs = new (string Tipper, string Recipient, decimal Amount, string Traffic, Guid? RefId, long UnixMs)[]
        {
            ("aether:tipper:aa", "aether:recipient:bb", 12.50m,    "message-relay",
                Guid.Parse("11112222-3333-4444-5555-666677778888"), 1_700_000_000_000L),
            ("aether:tipper:zz", "aether:recipient:bb", 0.0001m,   "gateway-share",
                null,                                                1_699_999_999_001L),
            ("aether:node:00",   "aether:node:01",      123456.789m, "stream-relay",
                Guid.Parse("deadbeef-cafe-4bad-8f00-0123456789ab"), 1_701_234_567_890L),
        };

        var cases = inputs.Select(inp =>
        {
            var tip = BuildTip(inp.Tipper, inp.Recipient, inp.Amount, inp.Traffic, inp.RefId, inp.UnixMs);
            var canonical = tip.BuildCanonicalData();
            var signature = Ed25519SigningService.Sign(TipperSeed, canonical);

            return new
            {
                tipper_uhid    = inp.Tipper,
                recipient_uhid = inp.Recipient,
                // amount is serialized in the canonical body as its invariant round-trip "G" form;
                // record that exact string so ports compare the same serialization, not a parsed double.
                amount         = inp.Amount.ToString(CultureInfo.InvariantCulture),
                traffic_type   = inp.Traffic,
                reference_id   = inp.RefId?.ToString("D"),   // null when the tip stands alone
                timestamp_unix_ms = inp.UnixMs,
                canonical_bytes = Hex(canonical),
                signature       = Hex(signature),
            };
        }).ToArray();

        var fixture = new
        {
            note = "Canonical tipping parity vectors from the C# reference (TipPacketPayload.BuildCanonicalData "
                 + "+ Ed25519). Every language port MUST reproduce canonical_bytes and signature byte-for-byte. "
                 + "Canonical layout (LE lengths): TipperLen|Tipper|RecipientLen|Recipient|AmountLen|Amount("
                 + "invariant G string, UTF-8)|TrafficLen|Traffic|ReferenceId(16, all-zero GUID when null)|"
                 + "TimestampUnixMs(8 LE i64).",
            algorithm     = "Ed25519",
            ed25519_seed  = Hex(TipperSeed),   // 32-byte raw seed (private). Public key + signatures derive from it.
            public_key    = Hex(tipperPub),    // 32-byte raw Ed25519 public key for ed25519_seed.
            cases,
        };

        WriteJson(path, fixture);
    }

    private void VerifyTippingFixture(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        var seed = FromHex(root.GetProperty("ed25519_seed").GetString()!);
        var pub  = FromHex(root.GetProperty("public_key").GetString()!);
        Assert.Equal(Hex(PublicKeyFromSeed(seed)), Hex(pub)); // public key matches the seed

        foreach (var c in root.GetProperty("cases").EnumerateArray())
        {
            var refStr = c.GetProperty("reference_id");
            Guid? refId = refStr.ValueKind == JsonValueKind.Null ? null : Guid.Parse(refStr.GetString()!);

            var tip = BuildTip(
                c.GetProperty("tipper_uhid").GetString()!,
                c.GetProperty("recipient_uhid").GetString()!,
                decimal.Parse(c.GetProperty("amount").GetString()!, CultureInfo.InvariantCulture),
                c.GetProperty("traffic_type").GetString()!,
                refId,
                c.GetProperty("timestamp_unix_ms").GetInt64());

            // (a) re-derived canonical bytes equal the recorded hex
            var canonical = tip.BuildCanonicalData();
            Assert.Equal(c.GetProperty("canonical_bytes").GetString(), Hex(canonical));

            // (b) the recorded signature Ed25519-verifies against the public key over those bytes
            var sig = FromHex(c.GetProperty("signature").GetString()!);
            Assert.True(Ed25519SigningService.Verify(pub, canonical, sig),
                "Tipping signature failed to verify against the fixture public key.");

            // (c) and is byte-identical to a fresh signature from the seed (deterministic Ed25519)
            Assert.Equal(c.GetProperty("signature").GetString(), Hex(Ed25519SigningService.Sign(seed, canonical)));
        }
    }

    // ── 2. VAULT (Reed-Solomon) ───────────────────────────────────────────────────────────────────────

    private const int VaultK = 10;
    private const int VaultM = 4;

    /// <summary>
    /// Slice plaintext into K equal zero-padded data shards exactly as <c>InMemoryVaultService.StoreAsync</c>
    /// does (shardSize = ceil(size/K), size==0 -> 1) — this slicing is the cross-language interop contract.
    /// </summary>
    private static (byte[][] DataShards, int ShardSize) SliceData(byte[] plaintext, int k)
    {
        long size = plaintext.Length;
        int shardSize = size == 0 ? 1 : (int)Math.Ceiling((double)size / k);
        var dataShards = new byte[k][];
        for (int i = 0; i < k; i++)
        {
            var shard = new byte[shardSize];
            int srcOffset = i * shardSize;
            int copyLen = (int)Math.Min(shardSize, size - srcOffset);
            if (copyLen > 0)
                Buffer.BlockCopy(plaintext, srcOffset, shard, 0, copyLen);
            dataShards[i] = shard;
        }
        return (dataShards, shardSize);
    }

    /// <summary>Concatenate K recovered data shards and trim to the original size — the Vault recovery rule.</summary>
    private static byte[] ReassembleAndTrim(byte[][] dataShards, int originalSize)
    {
        using var buffer = new MemoryStream();
        foreach (var shard in dataShards) buffer.Write(shard);
        return buffer.ToArray()[..originalSize];
    }

    private void WriteVaultFixture(string path)
    {
        // 2222 is deliberately NOT a multiple of K=10 → shardSize=ceil(2222/10)=223, last data shard
        // (index 9) carries 215 real bytes + 8 zero-pad bytes, so padding is exercised.
        const int size = 2222;
        var input = new byte[size];
        for (int i = 0; i < size; i++) input[i] = (byte)((i * 31 + 7) & 0xFF); // deterministic, reproducible

        var (dataShards, shardSize) = SliceData(input, VaultK);
        var codec = new ReedSolomonCodec(VaultK, VaultM);
        var shards = codec.Encode(dataShards); // N = K+M shards: 0..K-1 systematic data, K..N-1 parity

        var shardJson = shards.Select((s, idx) => new { index = idx, hex = Hex(s) }).ToArray();

        // Recovery subsets — each lists the surviving shard indices and the expected recovered input.
        // 1) Drop 4 DATA shards (0..3): 6 data + 4 parity = K survivors → forces matrix-inversion path.
        var keepDropData = new[] { 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 };
        // 2) Drop a DATA+PARITY mix (data 0,1,2 + parity 10): keep 7 data + 3 parity = K survivors.
        var keepMix = new[] { 3, 4, 5, 6, 7, 8, 9, 11, 12, 13 };
        // 3) All K data shards present (0..9) → systematic fast-path (no inversion).
        var keepAllData = Enumerable.Range(0, VaultK).ToArray();

        object Recovery(string note, int[] keep)
        {
            var available = keep.ToDictionary(i => i, i => shards[i]);
            var recovered = ReassembleAndTrim(codec.DecodeDataShards(available), size);
            return new { note, survivor_indices = keep, recovered = Hex(recovered) };
        }

        // Should-fail subset: only K-1 = 9 survivors → unrecoverable.
        var failKeep = Enumerable.Range(0, VaultK - 1).ToArray();

        var fixture = new
        {
            note = "Canonical systematic Cauchy-Reed-Solomon (K=10, M=4) parity vectors from the C# reference "
                 + "(AetherNet.Vault.ReedSolomonCodec, GF(2^8) primitive polynomial 0x11D, alpha=2). Data shards "
                 + "0..K-1 are the plaintext sliced into equal zero-padded slices (shardSize=ceil(size/K)); shards "
                 + "K..N-1 are MDS parity. ANY K of the N shards reconstruct the original; K-1 or fewer cannot. "
                 + "Recovery = concat the K recovered data shards in index order, then trim to the original size. "
                 + "Every language port MUST reproduce every shard and every recovery byte-for-byte.",
            field = new { primitive_polynomial = "0x11D", alpha = 2, gf_bits = 8 },
            k = VaultK,
            m = VaultM,
            n = VaultK + VaultM,
            input_size = size,
            shard_size = shardSize,
            input = Hex(input),
            shards = shardJson,
            recovery = new[]
            {
                Recovery("drop 4 data shards (0..3); recover via parity (matrix inversion)", keepDropData),
                Recovery("drop a data+parity mix (data 0,1,2 + parity 10)",                 keepMix),
                Recovery("all K data shards present (systematic fast-path, no inversion)",   keepAllData),
            },
            should_fail = new
            {
                note = "Only K-1 survivors → DecodeDataShards throws (unrecoverable). Ports MUST treat this as a failure.",
                survivor_indices = failKeep,
            },
        };

        WriteJson(path, fixture);
    }

    private void VerifyVaultFixture(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        int k = root.GetProperty("k").GetInt32();
        int m = root.GetProperty("m").GetInt32();
        int size = root.GetProperty("input_size").GetInt32();
        var input = FromHex(root.GetProperty("input").GetString()!);
        Assert.Equal(size, input.Length);

        // Re-encode from the recorded input and confirm every shard equals the recorded hex.
        var (dataShards, shardSize) = SliceData(input, k);
        Assert.Equal(root.GetProperty("shard_size").GetInt32(), shardSize);
        var codec = new ReedSolomonCodec(k, m);
        var shards = codec.Encode(dataShards);

        var recordedShards = root.GetProperty("shards").EnumerateArray().ToArray();
        Assert.Equal(k + m, recordedShards.Length);
        foreach (var sh in recordedShards)
        {
            int idx = sh.GetProperty("index").GetInt32();
            Assert.Equal(sh.GetProperty("hex").GetString(), Hex(shards[idx]));
        }

        // Each recovery subset RS-decodes to exactly the recorded input.
        foreach (var rec in root.GetProperty("recovery").EnumerateArray())
        {
            var keep = rec.GetProperty("survivor_indices").EnumerateArray().Select(e => e.GetInt32()).ToArray();
            Assert.Equal(k, keep.Length);
            var available = keep.ToDictionary(i => i, i => shards[i]);
            var recovered = ReassembleAndTrim(codec.DecodeDataShards(available), size);
            Assert.Equal(rec.GetProperty("recovered").GetString(), Hex(recovered));
            Assert.Equal(input, recovered);
        }

        // The K-1 subset MUST fail.
        var failKeep = root.GetProperty("should_fail").GetProperty("survivor_indices")
            .EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Assert.Equal(k - 1, failKeep.Length);
        var failAvailable = failKeep.ToDictionary(i => i, i => shards[i]);
        Assert.Throws<InvalidOperationException>(() => codec.DecodeDataShards(failAvailable));
    }

    // ── 3. PoV TOKEN ────────────────────────────────────────────────────────────────────────────────

    private void WritePoVFixture(string path)
    {
        var witnessPub = PublicKeyFromSeed(WitnessSeed);

        // Fixed timestamp ticks (DateTime.Ticks = 100ns since 0001-01-01). One case per transport.
        // 638_000_000_000_000_000 ticks ≈ 2023-09-29T20:53:20Z.
        var inputs = new (string Subject, long Ticks, PoVTransportType Transport)[]
        {
            ("aether:subject:01", 638_000_000_000_000_000L, PoVTransportType.Ble),
            ("aether:subject:02", 638_123_456_789_012_345L, PoVTransportType.Nfc),
            ("aether:subject:03", 637_900_000_000_000_001L, PoVTransportType.NearLink),
        };

        var cases = inputs.Select(inp =>
        {
            var canonical = PoVTokenCodec.BuildSignableTokenData(inp.Subject, inp.Ticks, inp.Transport);
            var signature = Ed25519SigningService.Sign(WitnessSeed, canonical);
            return new
            {
                subject_uhid     = inp.Subject,
                timestamp_ticks  = inp.Ticks,
                transport        = inp.Transport.ToString().ToLowerInvariant(),
                transport_byte   = (byte)inp.Transport,
                canonical_body   = Hex(canonical),
                witness_signature = Hex(signature),
            };
        }).ToArray();

        var fixture = new
        {
            note = "Canonical Proof-of-Vicinity token parity vectors from the C# reference "
                 + "(PoVTokenCodec.BuildSignableTokenData + Ed25519). Canonical body layout: "
                 + "SubjectLen(4 LE i32)|Subject(UTF-8)|TimestampTicks(8 LE i64)|Transport(1 byte). "
                 + "Transport enum: ble=0, nfc=1, nearlink=2. timestamp_ticks is .NET DateTime.Ticks "
                 + "(100ns intervals since 0001-01-01). Every language port MUST reproduce canonical_body "
                 + "and witness_signature byte-for-byte.",
            algorithm           = "Ed25519",
            witness_seed        = Hex(WitnessSeed),   // 32-byte raw seed (private).
            witness_public_key  = Hex(witnessPub),    // 32-byte raw Ed25519 public key for witness_seed.
            cases,
        };

        WriteJson(path, fixture);
    }

    private void VerifyPoVFixture(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        var seed = FromHex(root.GetProperty("witness_seed").GetString()!);
        var pub  = FromHex(root.GetProperty("witness_public_key").GetString()!);
        Assert.Equal(Hex(PublicKeyFromSeed(seed)), Hex(pub));

        foreach (var c in root.GetProperty("cases").EnumerateArray())
        {
            var subject = c.GetProperty("subject_uhid").GetString()!;
            var ticks = c.GetProperty("timestamp_ticks").GetInt64();
            var transport = (PoVTransportType)c.GetProperty("transport_byte").GetByte();

            var canonical = PoVTokenCodec.BuildSignableTokenData(subject, ticks, transport);
            Assert.Equal(c.GetProperty("canonical_body").GetString(), Hex(canonical));

            // Also confirm the token-overload produces the identical body (interop sanity).
            var token = new PoVToken
            {
                SubjectUhid = subject,
                TimestampUtc = new DateTime(ticks, DateTimeKind.Utc),
                TransportUsed = transport,
            };
            Assert.Equal(Hex(canonical), Hex(PoVTokenCodec.BuildSignableTokenData(token)));

            var sig = FromHex(c.GetProperty("witness_signature").GetString()!);
            Assert.True(Ed25519SigningService.Verify(pub, canonical, sig),
                "PoV witness signature failed to verify against the fixture public key.");
            Assert.Equal(c.GetProperty("witness_signature").GetString(), Hex(Ed25519SigningService.Sign(seed, canonical)));
        }
    }

    // ── shared IO ──────────────────────────────────────────────────────────────────────────────────

    private static void WriteJson(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOpts));
    }

    /// <summary>
    /// Locate the repo's <c>fixtures/</c> directory by walking up from the test bin directory. Robust to
    /// the build output location so any contributor (or CI runner) regenerates into the committed tree,
    /// not the test's working directory.
    /// </summary>
    private static string FindFixturesRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "fixtures");
            // Anchor on the known sibling fixtures/signal so we don't accidentally match a stray "fixtures".
            if (Directory.Exists(candidate) && Directory.Exists(Path.Combine(candidate, "signal")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate the repo 'fixtures' directory (with a 'signal' subdirectory) above " +
            AppContext.BaseDirectory + ". Run from within the aether-protocol working tree.");
    }
}

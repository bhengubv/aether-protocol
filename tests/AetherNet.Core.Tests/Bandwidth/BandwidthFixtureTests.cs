// SPDX-License-Identifier: MIT

using System.IO;
using System.Linq;
using System.Text.Json;
using AetherNet.Bandwidth;
using AetherNet.Transport.Bandwidth;
using Xunit;

namespace AetherNet.Core.Tests.Bandwidth;

/// <summary>
/// Drives the C# reference through the cross-language ABMF corpus at
/// <c>tests/cross-language/bandwidth-fixtures.json</c>. Every other AetherNet SDK
/// drives the SAME corpus and MUST produce identical results. This is the oracle
/// that proves numeric parity across all 8 languages — without it, "identical by
/// construction" is unverified (and the bdpBonus divergence proved construction
/// is not enough).
/// </summary>
public class BandwidthFixtureTests
{
    private static readonly JsonElement Root = LoadCorpus();
    private static readonly double Tol = Root.GetProperty("toleranceAbs").GetDouble();

    private static JsonElement LoadCorpus()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "cross-language", "bandwidth-fixtures.json");
            if (File.Exists(candidate))
                return JsonDocument.Parse(File.ReadAllText(candidate)).RootElement.Clone();
            dir = dir.Parent;
        }
        throw new FileNotFoundException("bandwidth-fixtures.json not found walking up from " + AppContext.BaseDirectory);
    }

    private static BandwidthConfidence ParseConfidence(string s) => s switch
    {
        "None" => BandwidthConfidence.None,
        "Low" => BandwidthConfidence.Low,
        "Medium" => BandwidthConfidence.Medium,
        "High" => BandwidthConfidence.High,
        _ => throw new ArgumentException($"bad confidence {s}"),
    };

    // ── probeAck ──────────────────────────────────────────────────────────────

    public static IEnumerable<object[]> ProbeAckCases() =>
        Root.GetProperty("probeAck").EnumerateArray().Select(e => new object[] { e.GetProperty("name").GetString()!, e });

    [Theory]
    [MemberData(nameof(ProbeAckCases))]
    public void ProbeAck_RttAndOwd_Exact(string name, JsonElement f)
    {
        _ = name;
        var ack = new BandwidthProbeAck(
            (uint)1,
            f.GetProperty("senderSendUs").GetInt64(),
            f.GetProperty("receiverReceiveUs").GetInt64(),
            f.GetProperty("receiverSendUs").GetInt64(),
            f.GetProperty("senderReceiveUs").GetInt64(),
            f.GetProperty("probeBytes").GetInt32());

        Assert.Equal(f.GetProperty("expectRttUs").GetInt64(), (long)ack.Rtt.TotalMicroseconds);
        Assert.Equal(f.GetProperty("expectForwardOwdUs").GetInt64(), (long)ack.ForwardOwd.TotalMicroseconds);
    }

    // ── rto ───────────────────────────────────────────────────────────────────

    public static IEnumerable<object[]> RtoCases() =>
        Root.GetProperty("rto").EnumerateArray().Select(e => new object[] { e.GetProperty("name").GetString()!, e });

    [Theory]
    [MemberData(nameof(RtoCases))]
    public void Rto_Clamped_MatchesRfc6298(string name, JsonElement f)
    {
        _ = name;
        var sample = new BandwidthSample(
            "T", 1_000_000, 900_000, 1000,
            TimeSpan.FromMilliseconds(f.GetProperty("srttMs").GetDouble()),
            TimeSpan.FromMilliseconds(f.GetProperty("rttVarMs").GetDouble()),
            TimeSpan.FromMilliseconds(10), 0.0, 0L, BandwidthConfidence.High, DateTimeOffset.UtcNow);

        Assert.Equal(f.GetProperty("expectRtoMs").GetDouble(), sample.Rto.TotalMilliseconds, precision: 1);
    }

    // ── phyCap ─────────────────────────────────────────────────────────────────

    public static IEnumerable<object[]> PhyCapCases() =>
        Root.GetProperty("phyCap").EnumerateArray().Select(e => new object[] { e.GetProperty("name").GetString()!, e });

    [Theory]
    [MemberData(nameof(PhyCapCases))]
    public void PhyCap_FromRssi_Exact(string name, JsonElement f)
    {
        _ = name;
        var e = new BandwidthEstimator("T", 10_000_000_000L);
        e.ApplyPhyHint(f.GetProperty("rssiDbm").GetInt32());
        Assert.Equal(f.GetProperty("expectCapBps").GetInt64(), e.CurrentSample.PhyCapBps);
    }

    // ── estimator ──────────────────────────────────────────────────────────────

    public static IEnumerable<object[]> EstimatorCases() =>
        Root.GetProperty("estimator").EnumerateArray().Select(e => new object[] { e.GetProperty("name").GetString()!, e });

    [Theory]
    [MemberData(nameof(EstimatorCases))]
    public void Estimator_DrivesToExpectedSample(string name, JsonElement f)
    {
        _ = name;
        var e = new BandwidthEstimator(f.GetProperty("transport").GetString()!, f.GetProperty("maxBps").GetInt64());

        foreach (var op in f.GetProperty("ops").EnumerateArray())
        {
            switch (op.GetProperty("op").GetString())
            {
                case "delivery":
                    e.RecordDelivery(op.GetProperty("bytes").GetInt32(),
                                     op.GetProperty("sendUs").GetInt64(),
                                     op.GetProperty("deliverUs").GetInt64());
                    break;
                case "loss":
                    e.RecordLoss(op.GetProperty("bytes").GetInt32());
                    break;
                case "phyHint":
                    e.ApplyPhyHint(op.GetProperty("rssiDbm").GetInt32());
                    break;
                case "gossip":
                    e.WarmFromGossip(op.GetProperty("btlBwBps").GetInt64(),
                                     TimeSpan.FromMilliseconds(op.GetProperty("rtPropMs").GetDouble()),
                                     ParseConfidence(op.GetProperty("confidence").GetString()!));
                    break;
                default: throw new ArgumentException("unknown op");
            }
        }

        var s = e.CurrentSample;
        var exp = f.GetProperty("expect");

        // Integer / enum fields — exact.
        if (exp.TryGetProperty("btlBwBps", out var v1))    Assert.Equal(v1.GetInt64(), s.BtlBwBps);
        if (exp.TryGetProperty("effectiveBps", out var v2)) Assert.Equal(v2.GetInt64(), s.EffectiveBps);
        if (exp.TryGetProperty("availableBps", out var v3)) Assert.Equal(v3.GetInt64(), s.AvailableBps);
        if (exp.TryGetProperty("bdpBytes", out var v4))    Assert.Equal(v4.GetInt64(), s.BdpBytes);
        if (exp.TryGetProperty("phyCapBps", out var v5))   Assert.Equal(v5.GetInt64(), s.PhyCapBps);
        if (exp.TryGetProperty("confidence", out var v6))  Assert.Equal(ParseConfidence(v6.GetString()!), s.Confidence);

        // Float fields — tolerance.
        if (exp.TryGetProperty("srttMs", out var f1))   Assert.Equal(f1.GetDouble(), s.Srtt.TotalMilliseconds, Tol);
        if (exp.TryGetProperty("rttVarMs", out var f2)) Assert.Equal(f2.GetDouble(), s.RttVar.TotalMilliseconds, Tol);
        if (exp.TryGetProperty("rtPropMs", out var f3)) Assert.Equal(f3.GetDouble(), s.RtProp.TotalMilliseconds, Tol);
        if (exp.TryGetProperty("lossRate", out var f4)) Assert.Equal(f4.GetDouble(), s.LossRate, Tol);
    }

    // ── director ───────────────────────────────────────────────────────────────

    public static IEnumerable<object[]> DirectorCases() =>
        Root.GetProperty("director").EnumerateArray().Select(e => new object[] { e.GetProperty("name").GetString()!, e });

    [Theory]
    [MemberData(nameof(DirectorCases))]
    public void Director_RecommendsExpectedTransport(string name, JsonElement f)
    {
        _ = name;
        var director = new BandwidthDirector();

        // Register one estimator per declared transport. Use a generous maxBps so
        // the PHY default does not cap the gossip-seeded values.
        foreach (var t in f.GetProperty("register").EnumerateArray())
            director.Register(new BandwidthEstimator(t.GetString()!, 10_000_000_000L));

        foreach (var g in f.GetProperty("gossips").EnumerateArray())
        {
            director.ApplyGossip(new BandwidthGossipPayload(
                g.GetProperty("peerUhid").GetString()!,
                g.GetProperty("transport").GetString()!,
                g.GetProperty("btlBwBps").GetInt64(),
                g.GetProperty("rtPropUs").GetInt64(),
                ParseConfidence(g.GetProperty("confidence").GetString()!),
                DateTimeOffset.UtcNow));
        }

        var rec = f.GetProperty("recommend");
        var result = director.RecommendTransport(rec.GetProperty("peerUhid").GetString()!, rec.GetProperty("payloadBytes").GetInt64());

        var expectEl = f.GetProperty("expectTransport");
        if (expectEl.ValueKind == JsonValueKind.Null)
            Assert.Null(result);
        else
            Assert.Equal(expectEl.GetString(), result);
    }
}

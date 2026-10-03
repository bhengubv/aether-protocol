// SPDX-License-Identifier: MIT
// Ported from Fieldwatch app/src/test/java/app/fieldwatch/domain/RadioFactsTest.kt (cf6562d).
// Copyright (c) 2026 Off Grid Pete LLC. See src/AetherNet.Aware/NOTICE.md.

using Xunit;

namespace AetherNet.Aware.Tests;

public class RadioFactsTests
{
    [Fact]
    public void ConnectableYesSurvivesScanResponse()
    {
        var adv = new RadioFacts { Connectable = true };
        var scanRsp = new RadioFacts { Connectable = false };
        Assert.True(adv.Merge(scanRsp).Connectable);
        Assert.True(scanRsp.Merge(adv).Connectable);
    }

    [Fact]
    public void ConnectableStaysNoUntilAConnectableAd()
    {
        var first = new RadioFacts { Connectable = false };
        var again = new RadioFacts { Connectable = false };
        Assert.False(first.Merge(again).Connectable);
    }

    [Fact]
    public void ConnectableNullDoesNotClear()
    {
        var known = new RadioFacts { Connectable = false };
        Assert.False(known.Merge(new RadioFacts()).Connectable);
        Assert.Null(new RadioFacts().Merge(new RadioFacts()).Connectable);
    }

    [Fact]
    public void EddystoneFramesAccumulateInsteadOfReplacing()
    {
        var uid = new RadioFacts { ServiceData = [new("FEAA", "00AABBCCDDEEFF00112233445566778899")] };
        var url = new RadioFacts { ServiceData = [new("FEAA", "1001676F6F676C6507")] };
        var tlm = new RadioFacts { ServiceData = [new("0000FEAA-0000-1000-8000-00805F9B34FB", "2000ABCD")] };
        var merged = uid.Merge(url).Merge(tlm);
        Assert.Equal(3, merged.ServiceData.Count);
        Assert.Equal(
            new HashSet<string> { "00", "10", "20" },
            merged.ServiceData.Select(s => s.DataHex[..2]).ToHashSet());
    }

    [Fact]
    public void OtherServiceDataStillReplacesByUuid()
    {
        var first = new RadioFacts { ServiceData = [new("FE2C", "AA")] };
        var second = new RadioFacts { ServiceData = [new("FE2C", "BBCC")] };
        var merged = first.Merge(second);
        Assert.Equal("BBCC", Assert.Single(merged.ServiceData).DataHex);
    }
}

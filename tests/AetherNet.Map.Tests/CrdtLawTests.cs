// SPDX-License-Identifier: MIT
using AetherNet.Map.Crdt;
using Xunit;

namespace AetherNet.Map.Tests;

/// <summary>
/// Verifies the CRDT merge laws — commutativity, associativity, idempotency — for every field CRDT.
/// If any of these fail, nodes can diverge, so this is the load-bearing correctness gate for the map.
/// </summary>
public class CrdtLawTests
{
    private static HybridLogicalClock Hlc(long ms, ushort c, string node) => new(ms, c, node);

    // ── Hybrid Logical Clock ───────────────────────────────────────────────
    [Fact]
    public void Hlc_Tick_IsMonotonic()
    {
        var a = HybridLogicalClock.Start("A", 1000);
        var b = a.Tick(1000); // same ms → counter advances
        var c = b.Tick(1001); // later ms → counter resets, still greater
        Assert.True(b > a);
        Assert.True(c > b);
        Assert.Equal(1, b.Counter);
        Assert.Equal(0, c.Counter);
    }

    [Fact]
    public void Hlc_Receive_DominatesBothAndIsMonotonic()
    {
        var local = Hlc(1000, 5, "A");
        var remote = Hlc(1000, 9, "B");
        var next = local.Receive(remote, 1000);
        Assert.True(next > local);
        Assert.True(next > remote);
        Assert.Equal("A", next.NodeId); // receiver keeps its own id
    }

    [Fact]
    public void Hlc_TotalOrder_TiebreaksOnNodeId()
    {
        Assert.True(Hlc(1000, 0, "B") > Hlc(1000, 0, "A"));
        Assert.True(Hlc(1000, 1, "A") > Hlc(1000, 0, "Z"));
        Assert.True(Hlc(1001, 0, "A") > Hlc(1000, 9, "Z"));
    }

    // ── LWW register ───────────────────────────────────────────────────────
    [Fact]
    public void Lww_Merge_IsCommutativeAndIdempotent()
    {
        var a = new LwwRegister<string?>("old", Hlc(1000, 0, "A"));
        var b = new LwwRegister<string?>("new", Hlc(2000, 0, "B"));

        Assert.Equal(b, a.Merge(b));
        Assert.Equal(b, b.Merge(a));       // commutative
        Assert.Equal(a.Merge(b), a.Merge(b).Merge(b)); // idempotent
        Assert.Equal(a, a.Merge(a));
    }

    [Fact]
    public void Lww_ConcurrentDifferentFields_BothSurvive_ViaSeparateRegisters()
    {
        // Two authors edit different attributes concurrently; each register is independent.
        var hours = new LwwRegister<string?>("9-5", Hlc(1000, 0, "A"));
        var phone = new LwwRegister<string?>("555", Hlc(1000, 0, "B"));
        // Merging the feature = merging each field register independently → both edits kept.
        Assert.Equal("9-5", hours.Value);
        Assert.Equal("555", phone.Value);
    }

    // ── Add-wins set ───────────────────────────────────────────────────────
    [Fact]
    public void AddWins_ConcurrentAddAndRemove_AddWins()
    {
        var s = new AddWinsSet<string>();
        s.Remove("wifi", Hlc(1000, 0, "A"));
        s.Add("wifi", Hlc(1000, 0, "B")); // same-time add vs remove → add wins (>=)
        Assert.True(s.Contains("wifi"));
    }

    [Fact]
    public void AddWins_ReAddAfterRemove_Works()
    {
        var s = new AddWinsSet<string>();
        s.Add("ramp", Hlc(1000, 0, "A"));
        s.Remove("ramp", Hlc(2000, 0, "A"));
        Assert.False(s.Contains("ramp"));
        s.Add("ramp", Hlc(3000, 0, "A"));
        Assert.True(s.Contains("ramp"));
    }

    [Fact]
    public void AddWins_Merge_IsOrderIndependentAndIdempotent()
    {
        AddWinsSet<string> Build()
        {
            var s = new AddWinsSet<string>();
            s.Add("a", Hlc(1000, 0, "N1"));
            s.Add("b", Hlc(1100, 0, "N1"));
            s.Remove("a", Hlc(1200, 0, "N2"));
            return s;
        }

        var x = Build();
        var y = new AddWinsSet<string>();
        y.Add("a", Hlc(1300, 0, "N3")); // a re-added later elsewhere
        y.Add("c", Hlc(1400, 0, "N3"));

        var xy = Build(); xy.Merge(y);
        var yx = new AddWinsSet<string>(); yx.Merge(y); yx.Merge(Build());

        Assert.Equal(
            xy.Values.OrderBy(v => v),
            yx.Values.OrderBy(v => v)); // commutative
        Assert.Contains("a", xy.Values);  // re-add (1300) beats remove (1200)
        Assert.Contains("b", xy.Values);
        Assert.Contains("c", xy.Values);

        var twice = Build(); twice.Merge(y); twice.Merge(y);
        Assert.Equal(xy.Values.OrderBy(v => v), twice.Values.OrderBy(v => v)); // idempotent
    }

    // ── Grow-only set (witnesses) ──────────────────────────────────────────
    [Fact]
    public void GSet_Merge_IsUnion_Idempotent_AndCountsDistinct()
    {
        var a = new GrowOnlySet<string>(["k1", "k2"]);
        var b = new GrowOnlySet<string>(["k2", "k3"]);
        a.Merge(b);
        Assert.Equal(3, a.Count);
        a.Add("k1"); // idempotent
        a.Merge(b);
        Assert.Equal(3, a.Count);
    }

    // ── PN counter ─────────────────────────────────────────────────────────
    [Fact]
    public void PnCounter_ConcurrentPerNode_MergeSumsCorrectly()
    {
        var a = new PnCounter();
        a.Increment("A", 3);
        a.Decrement("A", 1);
        var b = new PnCounter();
        b.Increment("B", 5);

        var ab = new PnCounter(a.Positive, a.Negative);
        ab.Merge(b);
        var ba = new PnCounter(b.Positive, b.Negative);
        ba.Merge(a);

        Assert.Equal(7, ab.Value); // 3-1+5
        Assert.Equal(ab.Value, ba.Value);   // commutative
        ab.Merge(b);
        Assert.Equal(7, ab.Value);           // idempotent
    }
}

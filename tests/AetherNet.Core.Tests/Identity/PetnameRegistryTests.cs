// SPDX-License-Identifier: MIT

using AetherNet.Identity;
using Xunit;

namespace AetherNet.Core.Tests.Identity;

public class PetnameRegistryTests
{
    private const string Alice = "BH8CZ-B09CA";
    private const string Bob = "DY5CF-84G9T";
    private const string Carol = "KXJB7-MN2P4";

    // ── Pinning (my authoritative choice) ────────────────────────────────────

    [Fact]
    public void Pin_ThenResolve_BothDirections()
    {
        var reg = new PetnameRegistry();
        Assert.True(reg.Pin(Alice, "Mom"));
        Assert.Equal("Mom", reg.NameFor(Alice));
        Assert.Equal(Alice, reg.ResolveName("Mom"));
        Assert.Equal(Alice, reg.ResolveName("mom")); // name lookup is case-insensitive
    }

    [Fact]
    public void Pin_CanonicalisesTheTag_SoADashlessInputResolvesTheSame()
    {
        var reg = new PetnameRegistry();
        reg.Pin("BH8CZB09CA", "Mom"); // no dash
        Assert.Equal("Mom", reg.NameFor(Alice)); // canonical XXXXX-XXXXX
    }

    [Theory]
    [InlineData(null, "Mom")]
    [InlineData("not-a-tag", "Mom")]
    [InlineData(Alice, null)]
    [InlineData(Alice, "   ")]
    public void Pin_RejectsInvalidTagsOrNames(string? tag, string? name)
        => Assert.False(new PetnameRegistry().Pin(tag, name));

    [Fact]
    public void Pin_RejectsAnOverlongName()
        => Assert.False(new PetnameRegistry().Pin(Alice, new string('x', PetnameRegistry.LongestName + 1)));

    // ── Proposals (a peer's suggestion) ──────────────────────────────────────

    [Fact]
    public void Propose_IsStored_WhenIHaveNotPinnedThatTag()
    {
        var reg = new PetnameRegistry();
        Assert.True(reg.Propose(Bob, "Bobby", Carol));
        Assert.Equal("Bobby", reg.NameFor(Bob));
        Assert.Equal(Bob, reg.ResolveName("Bobby"));
    }

    [Fact]
    public void Propose_NeverOverridesMyPin()
    {
        var reg = new PetnameRegistry();
        reg.Pin(Bob, "Robert");
        Assert.False(reg.Propose(Bob, "Bobby", Carol)); // my choice stands
        Assert.Equal("Robert", reg.NameFor(Bob));
    }

    [Fact]
    public void Reject_RemovesABinding()
    {
        var reg = new PetnameRegistry();
        reg.Propose(Bob, "Bobby", Carol);
        Assert.True(reg.Reject(Bob));
        Assert.Null(reg.NameFor(Bob));
        Assert.False(reg.Reject(Bob)); // nothing left to remove
    }

    // ── Resolution ambiguity ─────────────────────────────────────────────────

    [Fact]
    public void ResolveName_ReturnsNull_WhenTwoPinsShareTheName()
    {
        var reg = new PetnameRegistry();
        reg.Pin(Alice, "Sam");
        reg.Pin(Bob, "Sam");
        Assert.Null(reg.ResolveName("Sam")); // ambiguous — the person must disambiguate
    }

    [Fact]
    public void ResolveName_PrefersMyPinOverAProposalOfTheSameName()
    {
        var reg = new PetnameRegistry();
        reg.Pin(Alice, "Sam");
        reg.Propose(Bob, "Sam", Carol);
        Assert.Equal(Alice, reg.ResolveName("Sam")); // pin wins, unambiguously
    }

    [Fact]
    public void ResolveName_ReturnsNull_ForAnUnknownName()
        => Assert.Null(new PetnameRegistry().ResolveName("Nobody"));

    // ── Seed + gossip ────────────────────────────────────────────────────────

    [Fact]
    public void Seed_LandsAsAuthoritative_AndAProposalCannotOverrideIt()
    {
        var reg = new PetnameRegistry();
        var stored = reg.Seed([new Petname(Alice, "Mom", PetnameSource.Proposed)]); // source normalised to Seed
        Assert.Equal(1, stored);
        Assert.False(reg.Propose(Alice, "Someone", Bob));
        Assert.Equal("Mom", reg.NameFor(Alice));
    }

    [Fact]
    public void ExportProposals_OffersMyPinsAsProposals_ButNotOthersProposals()
    {
        var reg = new PetnameRegistry();
        reg.Pin(Alice, "Mom");
        reg.Propose(Bob, "Bobby", Carol); // someone else's proposal — must NOT be re-gossiped

        var export = reg.ExportProposals(myTag: Carol);

        var one = Assert.Single(export);
        Assert.Equal(Alice, one.Tag);
        Assert.Equal("Mom", one.Name);
        Assert.Equal(PetnameSource.Proposed, one.Source);
        Assert.Equal(Carol, one.ProposedByTag);
    }

    [Fact]
    public void ImportProposals_StoresThem_ButRespectsMyPins()
    {
        var reg = new PetnameRegistry();
        reg.Pin(Alice, "Mom"); // I already named Alice

        var stored = reg.ImportProposals(
        [
            new Petname(Alice, "AliceFromPeer", PetnameSource.Proposed, Bob), // ignored — my pin stands
            new Petname(Carol, "Caz", PetnameSource.Proposed, Bob),           // accepted
        ]);

        Assert.Equal(1, stored);
        Assert.Equal("Mom", reg.NameFor(Alice));
        Assert.Equal("Caz", reg.NameFor(Carol));
    }

    // ── Persistence ──────────────────────────────────────────────────────────

    [Fact]
    public void Bindings_SurviveAcrossRegistryInstances_OverASharedStore()
    {
        var store = new InMemoryPetnameStore();
        new PetnameRegistry(store).Pin(Alice, "Mom");

        var reopened = new PetnameRegistry(store);
        Assert.Equal("Mom", reopened.NameFor(Alice));
    }
}

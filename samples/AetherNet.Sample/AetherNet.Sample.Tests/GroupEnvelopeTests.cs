// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Data;
using AetherNet.Sample.Shared.Services;
using Xunit;

namespace AetherNet.Sample.Tests;

public class GroupEnvelopeTests
{
    private static GroupRecord AGroup() =>
        new("G0123456789AB", "Load-shedding crew", "KXJB7-MN2P4", 1_700_000_000_000);

    // ── News ──────────────────────────────────────────────────────────────────

    [Fact]
    public void News_carries_the_group_and_its_members()
    {
        string[] members = ["KXJB7-MN2P4", "DY5CF-84G9T"];

        var envelope = GroupEnvelope.Parse(GroupEnvelope.News(AGroup(), members));

        Assert.NotNull(envelope);
        Assert.Equal("new", envelope!.Kind);
        Assert.Equal("G0123456789AB", envelope.GroupId);
        Assert.Equal("Load-shedding crew", envelope.Name);
        Assert.Equal(members, envelope.Members);
    }

    // ── Messages ──────────────────────────────────────────────────────────────

    [Fact]
    public void Message_roundtrips_intact()
    {
        var json = GroupEnvelope.Message("G0123456789AB", "abc123", "KXJB7-MN2P4", "No tower no wifi");

        var envelope = GroupEnvelope.Parse(json);

        Assert.NotNull(envelope);
        Assert.Equal("msg", envelope!.Kind);
        Assert.Equal("abc123", envelope.MessageId);
        Assert.Equal("KXJB7-MN2P4", envelope.Sender);
        Assert.Equal("No tower no wifi", envelope.Body);
    }

    // ── Malformed input ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{}")]                          // belongs to no conversation
    [InlineData("{\"k\":\"msg\",\"g\":\"\"}")]  // empty group id, same problem
    public void Parse_rejects_malformed_input(string json) =>
        Assert.Null(GroupEnvelope.Parse(json));

    // ── Forward compatibility ─────────────────────────────────────────────────

    [Fact]
    public void Parse_ignores_a_field_from_a_newer_build()
    {
        const string fromTheFuture =
            "{\"v\":2,\"k\":\"msg\",\"g\":\"G0123456789AB\",\"i\":\"abc123\",\"b\":\"hello\",\"reactions\":[\"fire\"]}";

        var envelope = GroupEnvelope.Parse(fromTheFuture);

        Assert.NotNull(envelope);
        Assert.Equal("hello", envelope!.Body);
    }
}

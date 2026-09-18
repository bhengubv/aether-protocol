// SPDX-License-Identifier: MIT

using Xunit;

namespace AetherNet.Node.Tests;

public class GrantTests
{
    [Fact]
    public void A_fresh_request_is_awaiting_not_bound()
    {
        var grant = AppGrant.Requested("app.one");
        Assert.Equal(GrantState.AwaitingGrant, grant.State);
        Assert.False(grant.CanBind);
        Assert.Null(grant.GrantedAt);
    }

    [Fact]
    public void Granting_binds_and_stamps_the_time()
    {
        var at = System.DateTimeOffset.UnixEpoch;
        var grant = AppGrant.Requested("app.one").Granted(at);
        Assert.Equal(GrantState.Bound, grant.State);
        Assert.True(grant.CanBind);
        Assert.Equal(at, grant.GrantedAt);
    }

    [Fact]
    public void Revoking_clears_the_binding()
    {
        var grant = AppGrant.Requested("app.one").Granted(System.DateTimeOffset.UnixEpoch).RevokedNow();
        Assert.Equal(GrantState.Revoked, grant.State);
        Assert.False(grant.CanBind);
        Assert.Null(grant.GrantedAt);
    }

    [Theory]
    [InlineData(GrantState.Absent, GrantState.AwaitingGrant, true)]
    [InlineData(GrantState.AwaitingGrant, GrantState.Bound, true)]
    [InlineData(GrantState.AwaitingGrant, GrantState.Absent, true)]
    [InlineData(GrantState.Bound, GrantState.Revoked, true)]
    [InlineData(GrantState.Revoked, GrantState.AwaitingGrant, true)]
    [InlineData(GrantState.Absent, GrantState.Bound, false)]
    [InlineData(GrantState.Bound, GrantState.Bound, false)]
    [InlineData(GrantState.Revoked, GrantState.Bound, false)]
    [InlineData(GrantState.Absent, GrantState.Revoked, false)]
    public void Transitions_follow_the_state_machine(GrantState from, GrantState to, bool allowed)
        => Assert.Equal(allowed, GrantStates.CanTransition(from, to));
}

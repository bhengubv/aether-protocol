// SPDX-License-Identifier: MIT

using Xunit;

namespace AetherNet.Node.Host.Tests;

public class GrantStoreTests
{
    [Fact]
    public void An_unknown_app_is_absent()
    {
        var store = new InMemoryGrantStore();
        Assert.Equal(GrantState.Absent, store.Get("app.unknown").State);
    }

    [Fact]
    public void A_saved_grant_is_returned_and_listed()
    {
        var store = new InMemoryGrantStore();
        store.Save(AppGrant.Requested("app.one").Granted(System.DateTimeOffset.UnixEpoch));

        Assert.Equal(GrantState.Bound, store.Get("app.one").State);
        Assert.Single(store.All());
    }

    [Fact]
    public void Revoking_is_persisted()
    {
        var store = new InMemoryGrantStore();
        store.Save(AppGrant.Requested("app.one").Granted(System.DateTimeOffset.UnixEpoch));
        store.Save(store.Get("app.one").RevokedNow());

        Assert.Equal(GrantState.Revoked, store.Get("app.one").State);
    }
}

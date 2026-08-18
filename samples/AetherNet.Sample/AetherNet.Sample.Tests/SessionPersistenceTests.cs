// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Data;
using AetherNet.Sample.Shared.Services;
using AetherNet.Security.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// A conversation has to survive the app closing.
///
/// <para>
/// Sessions were held only in memory, and nothing in the app noticed. Every launch began with amnesia:
/// both phones rebuilt independently, each as X3DH <b>initiator</b>, and ended up holding different
/// root keys for the same pair — after which every message between them failed its authentication tag.
/// Chat appeared to survive because it repairs on a decrypt failure; a call had no such path and simply
/// never connected. It cost a full day of on-device debugging, and the persistence had already been
/// written and tested — it just had no public way to be switched on.
/// </para>
///
/// <para>
/// So the test that matters is not "the store round-trips a blob" but "the same two identities can
/// still read each other after both have been restarted".
/// </para>
/// </summary>
public class SessionPersistenceTests
{
    private const string Me = "QAVYZ-K8YFY";
    private const string Them = "KSQMM-T9G3E";

    // ── The store itself ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_stored_session_comes_back()
    {
        using var store = AetherStore.InMemory();
        var sessions = new StoredSignalSessions(store);
        var blob = new byte[] { 1, 2, 3, 4, 5 };

        await sessions.SaveAsync(Them, blob);

        Assert.Equal(blob, await sessions.LoadAsync(Them));
    }

    [Fact]
    public async Task A_peer_with_no_session_returns_nothing()
    {
        using var store = AetherStore.InMemory();

        Assert.Null(await new StoredSignalSessions(store).LoadAsync(Them));
    }

    [Fact]
    public async Task Saving_again_replaces_what_was_there()
    {
        using var store = AetherStore.InMemory();
        var sessions = new StoredSignalSessions(store);

        await sessions.SaveAsync(Them, new byte[] { 1 });
        await sessions.SaveAsync(Them, new byte[] { 2, 2 });

        Assert.Equal(new byte[] { 2, 2 }, await sessions.LoadAsync(Them));
    }

    /// <summary>A session judged unusable has to actually go, or the repair cannot replace it.</summary>
    [Fact]
    public async Task A_deleted_session_is_gone()
    {
        using var store = AetherStore.InMemory();
        var sessions = new StoredSignalSessions(store);
        await sessions.SaveAsync(Them, new byte[] { 1 });

        await sessions.DeleteAsync(Them);

        Assert.Null(await sessions.LoadAsync(Them));
    }

    [Fact]
    public async Task Stored_peers_are_listed_so_they_can_be_rehydrated()
    {
        using var store = AetherStore.InMemory();
        var sessions = new StoredSignalSessions(store);
        await sessions.SaveAsync(Them, new byte[] { 1 });
        await sessions.SaveAsync("KXJB7-MN2P4", new byte[] { 2 });

        var peers = await sessions.ListPeersAsync();

        Assert.Equal(2, peers.Count);
        Assert.Contains(Them, peers);
    }

    // ── The property the whole thing exists for ───────────────────────────────

    /// <summary>
    /// Two phones establish a session, both "restart", and can still read each other.
    ///
    /// <para>
    /// The restart is modelled by constructing brand-new <see cref="SignalProtocolService"/> instances
    /// over the same stores — which is exactly what launching the app again does. Before persistence was
    /// wired up this failed with <c>AuthenticationTagMismatch</c>, because each side came back with
    /// nothing and rebuilt as initiator.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Two_devices_can_still_read_each_other_after_both_restart()
    {
        using var myStore = AetherStore.InMemory();
        using var theirStore = AetherStore.InMemory();
        var mySessions = new StoredSignalSessions(myStore);
        var theirSessions = new StoredSignalSessions(theirStore);

        // First run: establish a session and exchange a message each way.
        var meBefore = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance, mySessions);
        var themBefore = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance, theirSessions);
        meBefore.SetLocalUhid(Me);      // a real app takes this from its identity at startup
        themBefore.SetLocalUhid(Them);

        await meBefore.ProcessPreKeyBundleAsync(await themBefore.GeneratePreKeyBundleAsync(Them));
        var first = await meBefore.EncryptAsync(Them, "before the restart"u8.ToArray());
        Assert.Equal("before the restart",
            System.Text.Encoding.UTF8.GetString(await themBefore.DecryptAsync(Me, first)));

        // Both apps close and reopen — new services, same stores.
        var meAfter = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance, mySessions);
        var themAfter = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance, theirSessions);
        meAfter.SetLocalUhid(Me);
        themAfter.SetLocalUhid(Them);

        Assert.True(meAfter.HasSession(Them), "the session did not survive the restart on this side");
        Assert.True(themAfter.HasSession(Me), "the session did not survive the restart on their side");

        var after = await meAfter.EncryptAsync(Them, "after the restart"u8.ToArray());

        Assert.Equal("after the restart",
            System.Text.Encoding.UTF8.GetString(await themAfter.DecryptAsync(Me, after)));
    }

    /// <summary>
    /// And the reverse direction, because a ratchet that only survives one way is not a session.
    /// </summary>
    [Fact]
    public async Task The_reply_direction_survives_a_restart_too()
    {
        using var myStore = AetherStore.InMemory();
        using var theirStore = AetherStore.InMemory();
        var mySessions = new StoredSignalSessions(myStore);
        var theirSessions = new StoredSignalSessions(theirStore);

        var me = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance, mySessions);
        var them = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance, theirSessions);
        me.SetLocalUhid(Me);
        them.SetLocalUhid(Them);
        await me.ProcessPreKeyBundleAsync(await them.GeneratePreKeyBundleAsync(Them));
        await them.DecryptAsync(Me, await me.EncryptAsync(Them, "hello"u8.ToArray()));

        var meAgain = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance, mySessions);
        var themAgain = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance, theirSessions);
        meAgain.SetLocalUhid(Me);
        themAgain.SetLocalUhid(Them);

        var reply = await themAgain.EncryptAsync(Me, "hello back"u8.ToArray());

        Assert.Equal("hello back",
            System.Text.Encoding.UTF8.GetString(await meAgain.DecryptAsync(Them, reply)));
    }

    [Fact]
    public void A_null_store_is_refused_rather_than_silently_disabling_persistence() =>
        Assert.Throws<ArgumentNullException>(() =>
            new SignalProtocolService(NullLogger<SignalProtocolService>.Instance, (ISignalSessionBlobStore)null!));
}

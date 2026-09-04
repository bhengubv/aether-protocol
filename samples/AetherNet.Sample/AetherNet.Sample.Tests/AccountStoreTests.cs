// SPDX-License-Identifier: MIT

using System;
using System.IO;
using AetherNet.Sample.Shared.Data;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// The account object — display name, avatar, recovery-backed-up state — must survive a relaunch.
/// It used to be a hard-coded "You" held only in the page, so a name lasted exactly as long as the
/// screen did. These tests hold the store to the promise that it is now on disk.
/// </summary>
public sealed class AccountStoreTests
{
    [Fact]
    public void Account_defaults_empty_before_anything_is_set()
    {
        using var store = AetherStore.InMemory();

        var acct = store.GetAccount();

        Assert.Equal(string.Empty, acct.DisplayName);
        Assert.Equal(string.Empty, acct.Avatar);
        Assert.Equal(0, acct.RecoveryBackedUpMs);
        Assert.False(acct.RecoveryBackedUp);
    }

    [Fact]
    public void Account_survives_a_reopen()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aether-acct-{Guid.NewGuid():N}.db");
        try
        {
            using (var store = new AetherStore(path))
            {
                // The identity row is written first (first run), then the profile edits land on it.
                store.SaveIdentity("KXJB7-MN2P4", new byte[] { 1, 2, 3, 4 });
                store.SaveAccount("Thabo", "T");
                store.SetRecoveryBackedUp(true);
            }

            // A fresh open is exactly what a relaunch is.
            using (var reopened = new AetherStore(path))
            {
                var acct = reopened.GetAccount();
                Assert.Equal("Thabo", acct.DisplayName);
                Assert.Equal("T", acct.Avatar);
                Assert.True(acct.RecoveryBackedUp);
            }
        }
        finally
        {
            foreach (var f in new[] { path, path + "-wal", path + "-shm" })
                try { File.Delete(f); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void Clearing_the_name_and_recovery_flag_round_trips()
    {
        using var store = AetherStore.InMemory();
        store.SaveIdentity("ABCDE-12345", new byte[] { 9 });

        store.SaveAccount("Lerato", "L");
        store.SetRecoveryBackedUp(true);
        Assert.True(store.GetAccount().RecoveryBackedUp);

        store.SaveAccount(string.Empty, string.Empty);
        store.SetRecoveryBackedUp(false);

        var acct = store.GetAccount();
        Assert.Equal(string.Empty, acct.DisplayName);
        Assert.False(acct.RecoveryBackedUp);
    }
}

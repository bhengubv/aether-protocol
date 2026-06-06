// SPDX-License-Identifier: MIT

using System.Text;
using AetherNet.Storage;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Unit tests for <see cref="InMemoryKeyValueStore"/>. The in-memory store is the
/// default <see cref="IKeyValueStore"/> for tests and demos; we verify the basic
/// CRUD contract, key isolation, defensive copy semantics, and concurrent-read
/// safety promised by the underlying <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey, TValue}"/>.
/// </summary>
public class InMemoryKeyValueStoreTests
{
    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public async Task PutThenGet_RoundTripsValue()
    {
        var store = new InMemoryKeyValueStore();
        var value = Bytes("hello-aether");

        await store.PutAsync("key-1", value);
        var roundTripped = await store.GetAsync("key-1");

        Assert.NotNull(roundTripped);
        Assert.Equal(value, roundTripped);
    }

    [Fact]
    public async Task Get_NonexistentKey_ReturnsNull()
    {
        var store = new InMemoryKeyValueStore();
        Assert.Null(await store.GetAsync("missing"));
    }

    [Fact]
    public async Task Remove_ReturnsTrueWhenPresent_FalseWhenAbsent()
    {
        var store = new InMemoryKeyValueStore();
        await store.PutAsync("k", Bytes("v"));

        Assert.True(await store.RemoveAsync("k"));
        Assert.False(await store.RemoveAsync("k"));
        Assert.Null(await store.GetAsync("k"));
    }

    [Fact]
    public async Task ContainsAsync_TracksLifecycle()
    {
        var store = new InMemoryKeyValueStore();
        Assert.False(await store.ContainsAsync("k"));

        await store.PutAsync("k", Bytes("v"));
        Assert.True(await store.ContainsAsync("k"));

        await store.RemoveAsync("k");
        Assert.False(await store.ContainsAsync("k"));
    }

    [Fact]
    public async Task Put_OverwritesExistingValue()
    {
        var store = new InMemoryKeyValueStore();
        await store.PutAsync("k", Bytes("original"));
        await store.PutAsync("k", Bytes("replaced"));

        var got = await store.GetAsync("k");
        Assert.Equal(Bytes("replaced"), got);
    }

    [Fact]
    public async Task MultipleKeys_AreIndependent()
    {
        var store = new InMemoryKeyValueStore();
        await store.PutAsync("alpha", Bytes("a"));
        await store.PutAsync("beta", Bytes("b"));
        await store.PutAsync("gamma", Bytes("g"));

        Assert.Equal(Bytes("a"), await store.GetAsync("alpha"));
        Assert.Equal(Bytes("b"), await store.GetAsync("beta"));
        Assert.Equal(Bytes("g"), await store.GetAsync("gamma"));

        await store.RemoveAsync("beta");

        Assert.NotNull(await store.GetAsync("alpha"));
        Assert.Null(await store.GetAsync("beta"));
        Assert.NotNull(await store.GetAsync("gamma"));
    }

    [Fact]
    public async Task Put_TakesDefensiveCopy_CallerMutationDoesNotLeak()
    {
        var store = new InMemoryKeyValueStore();
        var input = Bytes("immutable");

        await store.PutAsync("k", input);
        // Mutate the original buffer — the stored value must not change.
        input[0] = 0xFF;

        var stored = await store.GetAsync("k");
        Assert.NotNull(stored);
        Assert.Equal(Bytes("immutable"), stored);
    }

    [Fact]
    public async Task ListKeysAsync_EnumeratesAllKeys()
    {
        var store = new InMemoryKeyValueStore();
        await store.PutAsync("k1", Bytes("1"));
        await store.PutAsync("k2", Bytes("2"));
        await store.PutAsync("k3", Bytes("3"));

        var keys = new List<string>();
        await foreach (var k in store.ListKeysAsync()) keys.Add(k);

        Assert.Equal(3, keys.Count);
        Assert.Contains("k1", keys);
        Assert.Contains("k2", keys);
        Assert.Contains("k3", keys);
    }

    [Fact]
    public async Task ConcurrentReads_AreSafe()
    {
        var store = new InMemoryKeyValueStore();
        for (int i = 0; i < 50; i++)
        {
            await store.PutAsync($"k{i}", Bytes($"v{i}"));
        }

        // 32 readers, each fetching all 50 keys, must each see consistent values.
        var readers = Enumerable.Range(0, 32).Select(_ => Task.Run(async () =>
        {
            for (int i = 0; i < 50; i++)
            {
                var got = await store.GetAsync($"k{i}");
                Assert.Equal(Bytes($"v{i}"), got);
            }
        })).ToArray();

        await Task.WhenAll(readers);
    }
}

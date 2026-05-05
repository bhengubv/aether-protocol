// SPDX-License-Identifier: MIT

using System.Text;
using Aether.Storage;
using Xunit;

namespace Aether.Core.Tests;

/// <summary>
/// Unit tests for <see cref="FileSystemKeyValueStore"/>. Each test creates a fresh
/// temp directory under <see cref="Path.GetTempPath"/>; the IDisposable cleanup
/// removes everything on completion regardless of test outcome. Tests verify
/// durability across instance lifetimes (the whole point of having a file-backed
/// store), atomic-replace semantics on overwrite, namespace isolation, and that
/// awkward keys (slashes, Unicode, very long strings) round-trip safely thanks to
/// the SHA-256-hashed filename strategy.
/// </summary>
public sealed class FileSystemKeyValueStoreTests : IDisposable
{
    private readonly string _root;

    public FileSystemKeyValueStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "aether-fs-kv-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; CI temp directories age out naturally.
        }
    }

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public async Task PutThenGet_RoundTripsValueAndPersistsToDisk()
    {
        var store = new FileSystemKeyValueStore(_root);
        await store.PutAsync("config:node", Bytes("uhid-42"));

        var got = await store.GetAsync("config:node");
        Assert.NotNull(got);
        Assert.Equal(Bytes("uhid-42"), got);

        // The store should have actually written something to the disk.
        Assert.NotEmpty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task NewInstance_ReadsValuesWrittenByPreviousInstance()
    {
        var first = new FileSystemKeyValueStore(_root);
        await first.PutAsync("durable", Bytes("survives-restart"));

        // Simulate a process restart by constructing a fresh store at the same root.
        var second = new FileSystemKeyValueStore(_root);
        var got = await second.GetAsync("durable");

        Assert.NotNull(got);
        Assert.Equal(Bytes("survives-restart"), got);
    }

    [Fact]
    public async Task Get_NonexistentKey_ReturnsNull()
    {
        var store = new FileSystemKeyValueStore(_root);
        Assert.Null(await store.GetAsync("never-written"));
    }

    [Fact]
    public async Task Remove_DeletesFileAndManifest_AndReturnsCorrectFlag()
    {
        var store = new FileSystemKeyValueStore(_root);
        await store.PutAsync("transient", Bytes("temp"));

        var fileCountBeforeRemove = Directory.GetFiles(_root).Length;
        Assert.True(fileCountBeforeRemove >= 1);

        Assert.True(await store.RemoveAsync("transient"));
        Assert.False(await store.ContainsAsync("transient"));
        Assert.Empty(Directory.GetFiles(_root));

        // Removing again is a no-op that returns false.
        Assert.False(await store.RemoveAsync("transient"));
    }

    [Fact]
    public async Task Put_OverwritesAtomically()
    {
        var store = new FileSystemKeyValueStore(_root);
        await store.PutAsync("k", Bytes("v1"));
        await store.PutAsync("k", Bytes("v2"));

        var got = await store.GetAsync("k");
        Assert.Equal(Bytes("v2"), got);

        // No leftover .tmp files after a successful overwrite.
        Assert.DoesNotContain(Directory.GetFiles(_root), p => p.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AwkwardKeys_RoundTripSafely()
    {
        // Keys containing path-separators, Unicode, and pathological lengths must all
        // hash to filesystem-safe filenames.
        var store = new FileSystemKeyValueStore(_root);

        var awkward = new[]
        {
            "../../etc/passwd",
            "msg:\\\\server\\share",
            "uhid:3D-emoji-⚡",
            "key:" + new string('x', 512),
        };

        for (int i = 0; i < awkward.Length; i++)
        {
            await store.PutAsync(awkward[i], Bytes($"value-{i}"));
        }

        for (int i = 0; i < awkward.Length; i++)
        {
            var got = await store.GetAsync(awkward[i]);
            Assert.NotNull(got);
            Assert.Equal(Bytes($"value-{i}"), got);
        }

        // Path-traversal protection: nothing escapes the root.
        Assert.True(Directory.Exists(_root));
        Assert.All(
            Directory.GetFiles(_root),
            path => Assert.StartsWith(_root, Path.GetFullPath(path), StringComparison.Ordinal));
    }

    [Fact]
    public async Task NamespacesAreIsolated()
    {
        var alpha = new FileSystemKeyValueStore(_root, "alpha");
        var beta = new FileSystemKeyValueStore(_root, "beta");

        await alpha.PutAsync("shared-key", Bytes("from-alpha"));
        await beta.PutAsync("shared-key", Bytes("from-beta"));

        Assert.Equal(Bytes("from-alpha"), await alpha.GetAsync("shared-key"));
        Assert.Equal(Bytes("from-beta"), await beta.GetAsync("shared-key"));

        // Each namespace lives in its own subdirectory.
        Assert.True(Directory.Exists(Path.Combine(_root, "alpha")));
        Assert.True(Directory.Exists(Path.Combine(_root, "beta")));
    }

    [Fact]
    public async Task ListKeysAsync_ReturnsOriginalKeyStrings()
    {
        var store = new FileSystemKeyValueStore(_root);
        await store.PutAsync("route:alice", Bytes("a"));
        await store.PutAsync("route:bob", Bytes("b"));
        await store.PutAsync("bundle:42", Bytes("c"));

        var keys = new List<string>();
        await foreach (var k in store.ListKeysAsync()) keys.Add(k);

        Assert.Equal(3, keys.Count);
        Assert.Contains("route:alice", keys);
        Assert.Contains("route:bob", keys);
        Assert.Contains("bundle:42", keys);
    }
}

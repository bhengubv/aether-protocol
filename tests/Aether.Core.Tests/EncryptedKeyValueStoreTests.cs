// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aether.Models;
using Aether.Storage;
using Xunit;

namespace Aether.Core.Tests;

/// <summary>
/// Tests for <see cref="EncryptedKeyValueStore"/>: the AES-256-GCM wrapper that
/// turns any <see cref="IKeyValueStore"/> into an encrypted-at-rest store.
/// Covers the round trip, MAC failure on wrong-key reads, tamper detection,
/// key-version rotation, and composition with the existing KV adapters
/// (<see cref="KeyValueRouteStore"/>).
/// </summary>
public class EncryptedKeyValueStoreTests
{
    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    private static byte[] RandomKey()
    {
        var k = new byte[EncryptedKeyValueStore.KeySize];
        RandomNumberGenerator.Fill(k);
        return k;
    }

    [Fact]
    public async Task PutThenGet_RoundTripsOriginalBytes()
    {
        var inner = new InMemoryKeyValueStore();
        var key = RandomKey();
        var store = new EncryptedKeyValueStore(inner, new StaticDataAtRestKeyProvider(key));

        var value = Bytes("aether-state-payload");
        await store.PutAsync("signal:session:peer-1", value);

        var got = await store.GetAsync("signal:session:peer-1");

        Assert.NotNull(got);
        Assert.Equal(value, got);
    }

    [Fact]
    public async Task InnerStore_ContainsCiphertext_NotPlaintext()
    {
        var inner = new InMemoryKeyValueStore();
        var key = RandomKey();
        var store = new EncryptedKeyValueStore(inner, new StaticDataAtRestKeyProvider(key));

        var plaintext = Bytes("very-secret-route-table-entry");
        await store.PutAsync("route:peer-X", plaintext);

        // Verify the inner store does NOT hold the plaintext anywhere.
        var ciphertext = await inner.GetAsync("route:peer-X");
        Assert.NotNull(ciphertext);
        Assert.NotEqual(plaintext, ciphertext);

        // Wire format: 1 (version) + 12 (nonce) + N (ciphertext) + 16 (tag).
        var expectedLength = EncryptedKeyValueStore.VersionHeaderSize
            + EncryptedKeyValueStore.NonceSize
            + plaintext.Length
            + EncryptedKeyValueStore.TagSize;
        Assert.Equal(expectedLength, ciphertext.Length);

        // Version header: defaults to 1 for the single-key constructor.
        Assert.Equal((byte)1, ciphertext[0]);

        // The plaintext as a contiguous substring must NOT appear in the blob.
        Assert.DoesNotContain(Encoding.UTF8.GetString(plaintext), Encoding.UTF8.GetString(ciphertext));
    }

    [Fact]
    public async Task WrongKey_CannotDecrypt_ReturnsNullWithoutThrowing()
    {
        var inner = new InMemoryKeyValueStore();
        var keyA = RandomKey();
        var keyB = RandomKey();

        // Write under key A.
        var writer = new EncryptedKeyValueStore(inner, new StaticDataAtRestKeyProvider(keyA));
        await writer.PutAsync("k", Bytes("payload"));

        // Read under a DIFFERENT key (same version number, different bytes).
        var reader = new EncryptedKeyValueStore(inner, new StaticDataAtRestKeyProvider(keyB));
        var got = await reader.GetAsync("k");

        Assert.Null(got);
    }

    [Fact]
    public async Task UnknownKeyVersion_ReturnsNullWithoutThrowing()
    {
        var inner = new InMemoryKeyValueStore();
        var keyA = RandomKey();

        var writer = new EncryptedKeyValueStore(inner, new StaticDataAtRestKeyProvider(keyA));
        await writer.PutAsync("k", Bytes("payload"));

        // Build a provider whose only key is on a different version number.
        var keyB = RandomKey();
        var providerVersion2Only = new StaticDataAtRestKeyProvider(
            new Dictionary<int, byte[]> { [2] = keyB },
            currentVersion: 2);
        var reader = new EncryptedKeyValueStore(inner, providerVersion2Only);

        var got = await reader.GetAsync("k");
        Assert.Null(got);
    }

    [Fact]
    public async Task TamperedCiphertext_FailsAuthentication_ReturnsNull()
    {
        var inner = new InMemoryKeyValueStore();
        var key = RandomKey();
        var store = new EncryptedKeyValueStore(inner, new StaticDataAtRestKeyProvider(key));

        await store.PutAsync("k", Bytes("important-payload"));

        // Flip a byte in the ciphertext middle (definitely inside the AES output, not the version byte).
        var blob = await inner.GetAsync("k");
        Assert.NotNull(blob);
        var tamperIndex = EncryptedKeyValueStore.VersionHeaderSize
            + EncryptedKeyValueStore.NonceSize
            + 2;
        blob![tamperIndex] ^= 0x01;
        await inner.PutAsync("k", blob);

        var got = await store.GetAsync("k");
        Assert.Null(got);
    }

    [Fact]
    public async Task TamperedTag_FailsAuthentication_ReturnsNull()
    {
        var inner = new InMemoryKeyValueStore();
        var key = RandomKey();
        var store = new EncryptedKeyValueStore(inner, new StaticDataAtRestKeyProvider(key));

        await store.PutAsync("k", Bytes("important-payload"));

        var blob = await inner.GetAsync("k");
        Assert.NotNull(blob);
        // Flip a byte in the trailing 16-byte tag.
        blob![blob.Length - 1] ^= 0x01;
        await inner.PutAsync("k", blob);

        var got = await store.GetAsync("k");
        Assert.Null(got);
    }

    [Fact]
    public async Task TruncatedBlob_BelowMinimum_ReturnsNull()
    {
        var inner = new InMemoryKeyValueStore();
        var key = RandomKey();
        var store = new EncryptedKeyValueStore(inner, new StaticDataAtRestKeyProvider(key));

        // Write a payload that's shorter than the minimum well-formed blob.
        await inner.PutAsync("garbage", new byte[5]);

        var got = await store.GetAsync("garbage");
        Assert.Null(got);
    }

    [Fact]
    public async Task KeyRotation_OldVersionRemainsReadable_NewWritesUseNewKey()
    {
        var inner = new InMemoryKeyValueStore();
        var keyV1 = RandomKey();
        var keyV2 = RandomKey();

        // Phase 1: write under version 1.
        var v1Provider = new StaticDataAtRestKeyProvider(
            new Dictionary<int, byte[]> { [1] = keyV1 },
            currentVersion: 1);
        var v1Store = new EncryptedKeyValueStore(inner, v1Provider);
        await v1Store.PutAsync("legacy", Bytes("written-under-v1"));

        var legacyBlob = await inner.GetAsync("legacy");
        Assert.NotNull(legacyBlob);
        Assert.Equal((byte)1, legacyBlob![0]);

        // Phase 2: rotate. New provider holds BOTH versions, current=2.
        var rotatingProvider = new StaticDataAtRestKeyProvider(
            new Dictionary<int, byte[]> { [1] = keyV1, [2] = keyV2 },
            currentVersion: 2);
        var rotatingStore = new EncryptedKeyValueStore(inner, rotatingProvider);

        // Old value still decryptable via v1 key.
        var legacyRead = await rotatingStore.GetAsync("legacy");
        Assert.Equal(Bytes("written-under-v1"), legacyRead);

        // New writes use v2.
        await rotatingStore.PutAsync("fresh", Bytes("written-under-v2"));
        var freshBlob = await inner.GetAsync("fresh");
        Assert.NotNull(freshBlob);
        Assert.Equal((byte)2, freshBlob![0]);

        // After rewrap, every blob is on v2.
        var rewrapped = await rotatingStore.RewrapAsync();
        Assert.Equal(2, rewrapped); // legacy + fresh

        var legacyRewrapped = await inner.GetAsync("legacy");
        Assert.NotNull(legacyRewrapped);
        Assert.Equal((byte)2, legacyRewrapped![0]);

        // Phase 3: a v2-only provider can still read everything.
        var v2OnlyProvider = new StaticDataAtRestKeyProvider(
            new Dictionary<int, byte[]> { [2] = keyV2 },
            currentVersion: 2);
        var v2Store = new EncryptedKeyValueStore(inner, v2OnlyProvider);
        Assert.Equal(Bytes("written-under-v1"), await v2Store.GetAsync("legacy"));
        Assert.Equal(Bytes("written-under-v2"), await v2Store.GetAsync("fresh"));
    }

    [Fact]
    public async Task NoncesAreUnique_AcrossWritesOfSameValue()
    {
        var inner = new InMemoryKeyValueStore();
        var key = RandomKey();
        var store = new EncryptedKeyValueStore(inner, new StaticDataAtRestKeyProvider(key));

        var seen = new HashSet<string>();
        for (var i = 0; i < 32; i++)
        {
            await store.PutAsync($"k-{i}", Bytes("identical-plaintext-every-time"));
            var blob = await inner.GetAsync($"k-{i}");
            Assert.NotNull(blob);
            // Slice out just the nonce — version + nonce + ciphertext + tag.
            var nonce = new byte[EncryptedKeyValueStore.NonceSize];
            Buffer.BlockCopy(blob!, EncryptedKeyValueStore.VersionHeaderSize, nonce, 0, EncryptedKeyValueStore.NonceSize);
            Assert.True(seen.Add(Convert.ToBase64String(nonce)),
                "AES-GCM nonce reuse detected across PutAsync calls — fatal for confidentiality.");
        }
    }

    [Fact]
    public async Task RemoveContainsList_PassThroughInner()
    {
        var inner = new InMemoryKeyValueStore();
        var key = RandomKey();
        var store = new EncryptedKeyValueStore(inner, new StaticDataAtRestKeyProvider(key));

        Assert.False(await store.ContainsAsync("k"));
        await store.PutAsync("k", Bytes("v"));
        Assert.True(await store.ContainsAsync("k"));

        var listed = new List<string>();
        await foreach (var k in store.ListKeysAsync()) listed.Add(k);
        Assert.Single(listed);
        Assert.Equal("k", listed[0]);

        Assert.True(await store.RemoveAsync("k"));
        Assert.False(await store.ContainsAsync("k"));
        Assert.False(await store.RemoveAsync("k"));
    }

    [Fact]
    public async Task NullValue_ForMissingKey_StillNull()
    {
        var inner = new InMemoryKeyValueStore();
        var key = RandomKey();
        var store = new EncryptedKeyValueStore(inner, new StaticDataAtRestKeyProvider(key));

        Assert.Null(await store.GetAsync("does-not-exist"));
    }

    [Fact]
    public async Task EmptyValue_RoundTrips()
    {
        var inner = new InMemoryKeyValueStore();
        var key = RandomKey();
        var store = new EncryptedKeyValueStore(inner, new StaticDataAtRestKeyProvider(key));

        await store.PutAsync("k", Array.Empty<byte>());
        var got = await store.GetAsync("k");
        Assert.NotNull(got);
        Assert.Empty(got);
    }

    [Fact]
    public async Task WrapsKeyValueRouteStore_RoundTripsRouteEntry()
    {
        // End-to-end: wrap an inner KV with encryption, mount KeyValueRouteStore
        // on top, write/read a RouteEntry, verify the inner store holds ciphertext
        // (no plaintext UHIDs leak to disk).
        var inner = new InMemoryKeyValueStore();
        var key = RandomKey();
        var encrypted = new EncryptedKeyValueStore(inner, new StaticDataAtRestKeyProvider(key));
        var routes = new KeyValueRouteStore(encrypted);

        var entry = new RouteEntry
        {
            DestinationUhid = "aether:bob:42",
            NextHopUhid = "aether:relay:7",
            HopCount = 3,
            LatencyMs = 87.5,
            QualityScore = 0.92,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
        };

        await routes.SaveAsync(entry);

        // Verify inner store does NOT contain plaintext UHIDs anywhere in the blob.
        var innerKey = "route:aether:bob:42";
        var blob = await inner.GetAsync(innerKey);
        Assert.NotNull(blob);
        Assert.DoesNotContain("aether:bob:42", Encoding.UTF8.GetString(blob!));
        Assert.DoesNotContain("aether:relay:7", Encoding.UTF8.GetString(blob!));

        // Round-trip through the adapter still yields the original entry.
        var got = await routes.GetAsync("aether:bob:42");
        Assert.NotNull(got);
        Assert.Equal("aether:bob:42", got!.DestinationUhid);
        Assert.Equal("aether:relay:7", got.NextHopUhid);
        Assert.Equal(3, got.HopCount);
        Assert.Equal(87.5, got.LatencyMs);
        Assert.Equal(0.92, got.QualityScore);

        // GetAllAsync also works.
        var all = await routes.GetAllAsync();
        Assert.Single(all);
    }

    [Fact]
    public async Task LargePayload_RoundTrips()
    {
        var inner = new InMemoryKeyValueStore();
        var key = RandomKey();
        var store = new EncryptedKeyValueStore(inner, new StaticDataAtRestKeyProvider(key));

        // 1 MiB payload — exercise the buffer arithmetic on a non-trivial size.
        var big = new byte[1024 * 1024];
        RandomNumberGenerator.Fill(big);

        await store.PutAsync("k", big);
        var got = await store.GetAsync("k");
        Assert.NotNull(got);
        Assert.Equal(big, got);
    }

    [Fact]
    public void Constructor_RejectsNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new EncryptedKeyValueStore(null!, new StaticDataAtRestKeyProvider(RandomKey())));

        Assert.Throws<ArgumentNullException>(() =>
            new EncryptedKeyValueStore(new InMemoryKeyValueStore(), null!));
    }

    [Fact]
    public async Task WrongKeyOnInnerJsonValue_DoesNotLeakBytes()
    {
        // Defensive sanity check: even if the wrong-key decrypt happens to
        // succeed at the AES-GCM math level (it should not — GCM is AEAD),
        // a JSON-shaped plaintext should never round-trip into our adapters
        // when the wrong key is used.
        var inner = new InMemoryKeyValueStore();
        var keyA = RandomKey();
        var keyB = RandomKey();

        var entry = new RouteEntry { DestinationUhid = "u", NextHopUhid = "h", HopCount = 1 };
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(entry);

        var writer = new EncryptedKeyValueStore(inner, new StaticDataAtRestKeyProvider(keyA));
        await writer.PutAsync("route:u", jsonBytes);

        var reader = new EncryptedKeyValueStore(inner, new StaticDataAtRestKeyProvider(keyB));
        var routes = new KeyValueRouteStore(reader);

        // Wrong-key decrypt -> null -> adapter sees "no entry".
        Assert.Null(await routes.GetAsync("u"));
    }
}

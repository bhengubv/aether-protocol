// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace AetherMesh.Storage;

/// <summary>
/// Durable key-value store backed by one file per entry in a configurable root directory.
/// Writes are atomic on the local file system: bytes go to a temp file inside the same
/// directory and are then renamed over the target. Keys are sanitized to a hex hash
/// (with the original key recoverable from a sidecar manifest) so arbitrary key strings
/// — including paths, slashes, and Unicode — round-trip safely on every host OS.
///
/// This is a simple reference impl, not a database: it doesn't compact, doesn't transact
/// across multiple keys, and has no encryption-at-rest. Hosts that need any of those
/// supply their own <see cref="IKeyValueStore"/> implementation.
/// </summary>
public sealed class FileSystemKeyValueStore : IKeyValueStore
{
    private const string EntrySuffix = ".kv";
    private const string TempSuffix = ".tmp";
    private const string KeyManifestSuffix = ".key";

    private readonly string _root;

    /// <summary>
    /// Create a store rooted at <paramref name="rootDirectory"/>. The directory is created
    /// if it does not exist. Multiple stores can share a root with disjoint <paramref name="namespace"/> values.
    /// </summary>
    public FileSystemKeyValueStore(string rootDirectory, string? @namespace = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootDirectory);
        _root = string.IsNullOrEmpty(@namespace) ? rootDirectory : Path.Combine(rootDirectory, @namespace);
        Directory.CreateDirectory(_root);
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        var path = EntryPath(key);
        if (!File.Exists(path)) return null;
        return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task PutAsync(string key, byte[] value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);

        var entry = EntryPath(key);
        var temp = entry + TempSuffix;
        await File.WriteAllBytesAsync(temp, value, cancellationToken).ConfigureAwait(false);
        File.Move(temp, entry, overwrite: true);

        var keyManifest = entry + KeyManifestSuffix;
        if (!File.Exists(keyManifest))
            await File.WriteAllTextAsync(keyManifest, key, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        var entry = EntryPath(key);
        var existed = File.Exists(entry);
        if (existed)
        {
            File.Delete(entry);
            var manifest = entry + KeyManifestSuffix;
            if (File.Exists(manifest)) File.Delete(manifest);
        }
        return Task.FromResult(existed);
    }

    public Task<bool> ContainsAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return Task.FromResult(File.Exists(EntryPath(key)));
    }

    public async IAsyncEnumerable<string> ListKeysAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_root)) yield break;

        foreach (var manifest in Directory.EnumerateFiles(_root, "*" + EntrySuffix + KeyManifestSuffix))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string original;
            try
            {
                original = await File.ReadAllTextAsync(manifest, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                continue;
            }
            yield return original;
        }
    }

    private string EntryPath(string key)
        => Path.Combine(_root, HashKey(key) + EntrySuffix);

    private static string HashKey(string key)
    {
        // SHA-256 → hex makes a filesystem-safe, fixed-length filename for any input.
        Span<byte> hash = stackalloc byte[32];
        if (!SHA256.TryHashData(Encoding.UTF8.GetBytes(key), hash, out _))
            throw new InvalidOperationException("SHA-256 hash computation failed");
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

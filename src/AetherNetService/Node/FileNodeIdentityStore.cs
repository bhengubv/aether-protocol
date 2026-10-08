// SPDX-License-Identifier: MIT
#if ANDROID
using AetherNet.Identity;

namespace AetherNetService;

/// <summary>
/// First-cut storage for the node's one private key: the node app's own private files directory, which
/// Android sandboxes to this app. The key is minted once and read back on later runs, so the node keeps the
/// same AetherTag across launches — the whole point of one device, one node.
///
/// <para>
/// Hardening to a Keystore-sealed, biometric-gated store (so a locked screen surfaces as
/// <c>NodeUnavailable</c> and the key is never readable at rest) is the next pass. The cross-process bind is
/// independent of where the key sits, so it is built and proven first, then the store is sealed under it.
/// </para>
/// </summary>
internal sealed class FileNodeIdentityStore : INodeIdentityStore
{
    private readonly string _path;

    public FileNodeIdentityStore(string directory)
        => _path = System.IO.Path.Combine(directory, "aether-node.key");

    public bool Exists => System.IO.File.Exists(_path);

    public byte[]? Load() => Exists ? System.IO.File.ReadAllBytes(_path) : null;

    public void Save(byte[] privateKey) => System.IO.File.WriteAllBytes(_path, privateKey);
}
#endif

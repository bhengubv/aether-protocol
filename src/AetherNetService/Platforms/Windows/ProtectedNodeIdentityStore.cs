// SPDX-License-Identifier: MIT
using System.Security.Cryptography;
using AetherNet.Identity;

namespace AetherNetService;

/// <summary>
/// The device's one private key on Windows: a file in AetherNetService's folder, sealed by Windows for the person
/// signed in (DPAPI, <see cref="DataProtectionScope.CurrentUser"/>). Copied off the disk it opens for nobody; the gate
/// is the sign-in, as on a phone it is the lock. Minted once and read back on every start, so the computer keeps the
/// same AetherTag.
/// </summary>
internal sealed class ProtectedNodeIdentityStore : INodeIdentityStore
{
    /// <summary>Ties the sealed file to this use, so nothing else sealed for the same person opens as it.</summary>
    private static readonly byte[] Purpose = "AetherNetService identity"u8.ToArray();

    private readonly string _path;

    public ProtectedNodeIdentityStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "aether-node.key");
    }

    public bool Exists => File.Exists(_path);

    public byte[]? Load()
    {
        if (!Exists)
        {
            return null;
        }

        try
        {
            return ProtectedData.Unprotect(File.ReadAllBytes(_path), Purpose, DataProtectionScope.CurrentUser);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            // It is there and will not open — not "absent", or a new identity would be minted over it.
            throw new NodeIdentityUnavailableException("AetherNetService's identity is here but Windows will not open it now.", ex);
        }
    }

    public void Save(byte[] privateKey)
        => File.WriteAllBytes(_path, ProtectedData.Protect(privateKey, Purpose, DataProtectionScope.CurrentUser));
}

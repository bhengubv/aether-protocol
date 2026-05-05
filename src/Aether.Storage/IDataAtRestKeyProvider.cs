// SPDX-License-Identifier: MIT

namespace Aether.Storage;

/// <summary>
/// Supplies the AES-256 master key(s) used by <see cref="EncryptedKeyValueStore"/>
/// to encrypt and decrypt persisted values at rest.
///
/// <para>
/// Two responsibilities:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///       <see cref="CurrentVersion"/> tells the wrapper which key version to stamp
///       onto every newly written blob. Hosts increment this to roll the key.
///     </description>
///   </item>
///   <item>
///     <description>
///       <see cref="GetKey(int)"/> hands back the 32-byte AES-256 key for a given
///       version on read. During a key-rotation window, the provider keeps both
///       the old and new key so previously written blobs continue to decrypt.
///     </description>
///   </item>
/// </list>
///
/// <para>
/// Hosts derive these bytes however they like — from a passphrase via PBKDF2,
/// from the OS keychain (DPAPI / Keychain Services / Android Keystore), from a
/// hardware enclave, or from a remote KMS. The wrapper never sees the source.
/// </para>
///
/// <para>
/// All keys returned by <see cref="GetKey(int)"/> MUST be exactly 32 bytes
/// (AES-256). Implementations are responsible for keeping the key material in
/// memory only as long as needed; the wrapper does not pin or wipe.
/// </para>
/// </summary>
public interface IDataAtRestKeyProvider
{
    /// <summary>
    /// The key version stamped onto every blob written via this provider.
    /// Must be in the range [1, 255] so it fits in the single-byte version
    /// header of the encrypted blob format.
    /// </summary>
    int CurrentVersion { get; }

    /// <summary>
    /// Returns the 32-byte AES-256 key for the given <paramref name="version"/>,
    /// or null if the provider has no key for that version (the blob was
    /// written under a key that has since been retired). The wrapper treats a
    /// null result as "cannot decrypt — return null to caller".
    /// </summary>
    byte[]? GetKey(int version);
}

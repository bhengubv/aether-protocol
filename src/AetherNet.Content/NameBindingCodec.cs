// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Text;

namespace AetherNet.Content;

/// <summary>
/// Canonical, cross-language-stable encoding of the bytes an author signs to bind a directory name
/// to a piece of content at a given version. Binding = {nameHash, authorPublicKey, version, rootHash}.
/// The salted <c>nameHash</c> is signed (never the plaintext name), so any receiver can verify the
/// binding while the plaintext name stays off the wire.
/// </summary>
public static class NameBindingCodec
{
    // Domain-separation tag: a signature over this body can never be replayed as any other kind of
    // Aether signature.
    private const string Domain = "aether-name-binding-v1";
    private const byte FormatVersion = 1;

    /// <summary>
    /// Build the deterministic signable body. Layout (little-endian):
    /// <c>[u8 formatVersion][u32 len + utf8 domain][u32 len + utf8 nameHash]
    /// [u32 len + bytes authorPublicKey][i64 version][u32 len + utf8 rootHash]</c>.
    /// </summary>
    public static byte[] BuildSignableBody(string nameHash, byte[] authorPublicKey, long version, string rootHash)
    {
        ArgumentException.ThrowIfNullOrEmpty(nameHash);
        ArgumentNullException.ThrowIfNull(authorPublicKey);
        ArgumentException.ThrowIfNullOrEmpty(rootHash);

        using var ms = new MemoryStream();
        ms.WriteByte(FormatVersion);
        WriteLengthPrefixed(ms, Encoding.UTF8.GetBytes(Domain));
        WriteLengthPrefixed(ms, Encoding.UTF8.GetBytes(nameHash));
        WriteLengthPrefixed(ms, authorPublicKey);

        Span<byte> versionBytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(versionBytes, version);
        ms.Write(versionBytes);

        WriteLengthPrefixed(ms, Encoding.UTF8.GetBytes(rootHash));
        return ms.ToArray();
    }

    private static void WriteLengthPrefixed(Stream stream, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)data.Length);
        stream.Write(length);
        stream.Write(data);
    }
}

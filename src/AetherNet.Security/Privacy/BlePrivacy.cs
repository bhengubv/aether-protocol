// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Security.Cryptography;

namespace AetherNet.Security.Privacy;

/// <summary>
/// Bluetooth-LE tracking protection: a rotating Service UUID and IRK-based
/// Resolvable Private Addresses (RPA), so a mesh node is discoverable by its
/// peers without exposing a stable, trackable Bluetooth fingerprint on the air.
///
/// <list type="bullet">
/// <item>The Service UUID rotates every 15 minutes, HMAC-SHA256-derived from a
/// shared rotation key and the current time window. Every node in the same
/// window derives the same UUID, so peers still find each other — but a passive
/// scanner sees an identifier that changes and cannot be linked over time.</item>
/// <item>The node's stable id is removed from the advertisement; a peer that
/// holds the node's 128-bit Identity Resolving Key (IRK) resolves its rotating
/// 6-byte RPA instead (the BLE "ah" function).</item>
/// </list>
///
/// The window-based operations are deterministic and byte-identical across every
/// AetherNet SDK (verified against fixtures/bleprivacy/vectors.json). The time
/// window is encoded as a little-endian int64.
/// </summary>
public static class BlePrivacy
{
    /// <summary>Rotation period in seconds (15 minutes).</summary>
    public const int RotationSeconds = 900;

    /// <summary>The rotation window index for a Unix-seconds timestamp.</summary>
    public static long WindowFor(long unixSeconds) => unixSeconds / RotationSeconds;

    /// <summary>
    /// The rotating BLE Service UUID for a rotation key and time window. Every
    /// node sharing the rotation key derives the same UUID within the window,
    /// enabling mutual discovery with no static identifier on the air.
    /// </summary>
    public static string ServiceUuid(byte[] rotationKey, long window)
    {
        ArgumentNullException.ThrowIfNull(rotationKey);
        var mac = HMACSHA256.HashData(rotationKey, WindowBytes(window));
        return FormatUuid(mac.AsSpan(0, 16));
    }

    /// <summary>
    /// A 6-byte Resolvable Private Address for a 16-byte IRK and time window:
    /// <c>hash(3) || prand(3)</c>, where prand is HMAC-derived (with the RPA
    /// address-type bits set) and hash = AES-128(IRK, prand-block). Rotates every
    /// window; only a peer holding the IRK can link successive addresses.
    /// </summary>
    public static byte[] ResolvableAddress(byte[] irk, long window)
    {
        ArgumentNullException.ThrowIfNull(irk);
        if (irk.Length != 16)
            throw new ArgumentException("IRK must be 16 bytes.", nameof(irk));

        var prand = HMACSHA256.HashData(irk, WindowBytes(window)).AsSpan(0, 3).ToArray();
        prand[0] = (byte)((prand[0] & 0x3F) | 0x40); // RPA address-type bits (0b01)

        var hash = Ah(irk, prand);

        var rpa = new byte[6];
        Buffer.BlockCopy(hash, 0, rpa, 0, 3);
        Buffer.BlockCopy(prand, 0, rpa, 3, 3);
        return rpa;
    }

    /// <summary>
    /// True if <paramref name="rpa"/> was generated from <paramref name="irk"/>
    /// — i.e. this node recognises the peer behind the rotating address.
    /// </summary>
    public static bool ResolveAddress(byte[] irk, byte[] rpa)
    {
        ArgumentNullException.ThrowIfNull(irk);
        ArgumentNullException.ThrowIfNull(rpa);
        if (irk.Length != 16 || rpa.Length != 6) return false;

        var prand = rpa.AsSpan(3, 3).ToArray();
        var hash = Ah(irk, prand);
        return hash.AsSpan(0, 3).SequenceEqual(rpa.AsSpan(0, 3));
    }

    // BLE "ah" hash: AES-128-ECB(irk, 0^13 || prand), keep the first 3 bytes.
    private static byte[] Ah(byte[] irk, byte[] prand)
    {
        var block = new byte[16];
        Buffer.BlockCopy(prand, 0, block, 13, 3);

        using var aes = Aes.Create();
        aes.Key = irk;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;

        var ct = aes.EncryptEcb(block, PaddingMode.None);
        return ct.AsSpan(0, 3).ToArray();
    }

    private static byte[] WindowBytes(long window)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(b, window);
        return b;
    }

    private static string FormatUuid(ReadOnlySpan<byte> b) =>
        $"{Convert.ToHexStringLower(b[..4])}-{Convert.ToHexStringLower(b[4..6])}-" +
        $"{Convert.ToHexStringLower(b[6..8])}-{Convert.ToHexStringLower(b[8..10])}-" +
        $"{Convert.ToHexStringLower(b[10..16])}";
}

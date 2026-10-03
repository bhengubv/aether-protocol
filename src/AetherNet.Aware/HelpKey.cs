// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;

namespace AetherNet.Aware;

/// <summary>
/// The secret a person shares only with the guardians they choose. Their phone's help messages are sealed with it, so
/// only those guardians can read them. Choosing different guardians means making a new key.
/// </summary>
public sealed class HelpKey
{
    public const int Length = 32;

    private readonly byte[] _secret;

    private HelpKey(byte[] secret)
    {
        _secret = secret;
        LabelKey = Derive(secret, "aether-help-label-v1");
        SealKey = Derive(secret, "aether-help-seal-v1");
        CheckKey = Derive(secret, "aether-help-check-v1");
    }

    internal byte[] LabelKey { get; }

    internal byte[] SealKey { get; }

    internal byte[] CheckKey { get; }

    public static HelpKey Create() => new(RandomNumberGenerator.GetBytes(Length));

    public static HelpKey FromBytes(ReadOnlySpan<byte> secret)
    {
        if (secret.Length != Length)
        {
            throw new ArgumentException($"A help key is {Length} bytes.", nameof(secret));
        }
        return new HelpKey(secret.ToArray());
    }

    /// <summary>The key's bytes, to hand to a guardian inside an encrypted contact message.</summary>
    public byte[] ToBytes() => (byte[])_secret.Clone();

    private static byte[] Derive(byte[] secret, string label) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, salt: null, info: Encoding.ASCII.GetBytes(label));
}

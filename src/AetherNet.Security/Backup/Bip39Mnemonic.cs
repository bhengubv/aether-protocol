// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;

namespace AetherNet.Security.Backup;

/// <summary>
/// BIP-39 mnemonic codec over the official English wordlist. Converts between
/// entropy, the human-writable recovery phrase, and the derived seed.
///
/// This is the real, standard BIP-39 algorithm, verified against the official
/// Trezor test vectors (see fixtures/bip39/vectors.json) — a phrase produced
/// here restores on any conformant BIP-39 wallet, and every AetherNet language
/// SDK reproduces the same words and seed byte-for-byte.
///
/// <code>
///   entropy (16..32 bytes, multiple of 4)  --EntropyToMnemonic-->  phrase
///   phrase  --MnemonicToEntropy-->  entropy      (SHA-256 checksum enforced)
///   phrase  --MnemonicToSeed-->  64-byte seed     (PBKDF2-HMAC-SHA512, 2048 rounds)
/// </code>
/// </summary>
public static class Bip39Mnemonic
{
    private const int PbkdfIterations = 2048;
    private const int SeedLengthBytes = 64;

    // word -> index, built once from the embedded official wordlist.
    private static readonly Dictionary<string, int> WordIndex = BuildIndex();

    private static Dictionary<string, int> BuildIndex()
    {
        var words = Bip39Wordlist.Words;
        var map = new Dictionary<string, int>(words.Count, StringComparer.Ordinal);
        for (var i = 0; i < words.Count; i++) map[words[i]] = i;
        return map;
    }

    /// <summary>
    /// Encodes entropy as a BIP-39 mnemonic phrase (space-separated words).
    /// </summary>
    /// <param name="entropy">16, 20, 24, 28, or 32 bytes (128..256 bits).</param>
    public static string EntropyToMnemonic(byte[] entropy)
    {
        ArgumentNullException.ThrowIfNull(entropy);
        if (entropy.Length < 16 || entropy.Length > 32 || entropy.Length % 4 != 0)
            throw new ArgumentException(
                "Entropy must be 16, 20, 24, 28, or 32 bytes.", nameof(entropy));

        var entBits = entropy.Length * 8;
        var csBits = entBits / 32;                  // 4..8 checksum bits
        var checksum = SHA256.HashData(entropy)[0]; // only the top csBits are used

        // Read the big-endian bit stream entropy||checksum in 11-bit groups.
        var wordCount = (entBits + csBits) / 11;
        var words = new string[wordCount];

        for (var w = 0; w < wordCount; w++)
        {
            var index = 0;
            for (var b = 0; b < 11; b++)
            {
                var bitPos = w * 11 + b;
                int bit = bitPos < entBits
                    ? (entropy[bitPos >> 3] >> (7 - (bitPos & 7))) & 1
                    : (checksum >> (7 - (bitPos - entBits))) & 1;
                index = (index << 1) | bit;
            }
            words[w] = Bip39Wordlist.Words[index];
        }

        return string.Join(' ', words);
    }

    /// <summary>
    /// Decodes a BIP-39 mnemonic back to its entropy, enforcing the SHA-256
    /// checksum. Throws <see cref="FormatException"/> on an unknown word, a wrong
    /// word count, or a checksum mismatch — so a mistyped phrase is rejected
    /// rather than silently yielding the wrong secret.
    /// </summary>
    public static byte[] MnemonicToEntropy(string mnemonic)
    {
        ArgumentNullException.ThrowIfNull(mnemonic);
        var words = SplitWords(mnemonic);
        if (words.Length is not (12 or 15 or 18 or 21 or 24))
            throw new FormatException(
                $"Mnemonic must be 12, 15, 18, 21, or 24 words (got {words.Length}).");

        var totalBits = words.Length * 11;
        var csBits = totalBits / 33;
        var entBits = totalBits - csBits;
        var entropy = new byte[entBits / 8];
        var actualChecksum = 0;

        for (var w = 0; w < words.Length; w++)
        {
            if (!WordIndex.TryGetValue(words[w], out var index))
                throw new FormatException($"Unknown mnemonic word: '{words[w]}'.");

            for (var b = 0; b < 11; b++)
            {
                var bit = (index >> (10 - b)) & 1;
                var bitPos = w * 11 + b;
                if (bitPos < entBits)
                    entropy[bitPos >> 3] |= (byte)(bit << (7 - (bitPos & 7)));
                else
                    actualChecksum = (actualChecksum << 1) | bit;
            }
        }

        var expectedChecksum = SHA256.HashData(entropy)[0] >> (8 - csBits);
        if (actualChecksum != expectedChecksum)
            throw new FormatException("Mnemonic checksum is invalid.");

        return entropy;
    }

    /// <summary>
    /// Derives the 64-byte BIP-39 seed from a mnemonic and optional passphrase,
    /// using PBKDF2-HMAC-SHA512 with 2048 iterations and salt "mnemonic"+passphrase.
    /// Both inputs are NFKD-normalized per the spec.
    /// </summary>
    public static byte[] MnemonicToSeed(string mnemonic, string passphrase = "")
    {
        ArgumentNullException.ThrowIfNull(mnemonic);
        passphrase ??= string.Empty;

        var normalizedMnemonic =
            string.Join(' ', SplitWords(mnemonic)).Normalize(NormalizationForm.FormKD);
        var salt = ("mnemonic" + passphrase).Normalize(NormalizationForm.FormKD);

        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(normalizedMnemonic),
            Encoding.UTF8.GetBytes(salt),
            PbkdfIterations,
            HashAlgorithmName.SHA512,
            SeedLengthBytes);
    }

    /// <summary>
    /// Returns true if <paramref name="mnemonic"/> is a well-formed BIP-39 phrase
    /// with a valid checksum.
    /// </summary>
    public static bool IsValid(string mnemonic)
    {
        try
        {
            MnemonicToEntropy(mnemonic);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string[] SplitWords(string mnemonic) =>
        mnemonic.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
}

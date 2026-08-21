// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// The name and passphrase of a Circle's Wi-Fi Direct group, worked out rather than exchanged.
///
/// <para>
/// Everything else was tried first. Service discovery cannot complete between these handsets — both
/// answer queries and neither receives the reply, because both ends free-run <c>p2p_find</c> and every
/// query lands while the other is mid-search; the supplicant reports it as <c>Service Discovery Query
/// TX callback: success=0</c>. Sending the credentials over the link instead only moved the problem:
/// the credentials need a radio, and the radio is the thing being brought up. That is a deadlock, and
/// it is what a broker over Bluetooth was really papering over.
/// </para>
///
/// <para>
/// So nothing is sent. Both phones already hold the host's public key — it arrives when you add
/// somebody, and is checked against their AetherTag — so both can derive the same network name and
/// passphrase from it, independently, before either phone is even switched on. The host creates that
/// exact group; everyone else joins that exact name. No discovery, no exchange, no first-message
/// problem, and no dialog, because nothing is being negotiated.
/// </para>
///
/// <para>
/// <b>What this is and is not.</b> It is not a secret from somebody who has the host's public key —
/// anyone who has added the host can derive it and join the group. That is deliberate and it is the
/// same trust boundary as adding a contact. Group membership is not the security boundary here:
/// everything crossing the group is still sealed in a Signal session, so joining it buys an intruder
/// the ability to carry other people's ciphertext and nothing else.
/// </para>
/// </summary>
public static class GroupCredentials
{
    /// <summary>
    /// Android requires a Wi-Fi Direct network name to begin with this. It is not a convention we
    /// chose and not one we can opt out of — <c>setNetworkName</c> rejects anything else.
    /// </summary>
    private const string RequiredPrefix = "DIRECT-";

    /// <summary>Ties this derivation to this purpose, so the same key used elsewhere yields nothing here.</summary>
    private const string Info = "aether-wifi-direct-group-v1";

    /// <summary>
    /// Crockford's alphabet: no I, L, O or U, so the passphrase cannot be misread down a phone line
    /// and cannot accidentally spell anything.
    /// </summary>
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>
    /// The group the given host runs.
    /// </summary>
    /// <param name="hostPublicKey">
    ///   The public key of whichever phone hosts — <see cref="GroupRole"/> decides which that is.
    ///   Derived from the host alone rather than from a pair, so a third phone joining the same host
    ///   computes the same credentials as the second one did.
    /// </param>
    /// <remarks>
    /// Returns null for a missing key rather than inventing a group nobody else could compute. A
    /// contact whose public key never arrived cannot be met this way, and saying so is better than
    /// hosting something unjoinable.
    /// </remarks>
    public static WifiDirectCredentials? ForHost(byte[]? hostPublicKey)
    {
        if (hostPublicKey is not { Length: > 0 }) return null;

        // One derivation, split into two fields, so the name and the passphrase cannot drift apart
        // between the phone that hosts and the phone that joins.
        Span<byte> derived = stackalloc byte[16];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, hostPublicKey, derived,
            salt: ReadOnlySpan<byte>.Empty, info: Encoding.UTF8.GetBytes(Info));

        // 7 + 9 = 16 characters, inside Android's 32-character limit with room to spare.
        var name = RequiredPrefix + Encode(derived[..6]);

        // 15 characters against Android's 8-63. Long enough that guessing it is pointless, short
        // enough to be read aloud if anybody ever has to.
        var passphrase = Encode(derived[6..]);

        return new WifiDirectCredentials(name, passphrase);
    }

    /// <summary>Bytes as Crockford base32, five bits at a time.</summary>
    private static string Encode(ReadOnlySpan<byte> bytes)
    {
        var chars = new char[bytes.Length * 8 / 5];
        int bit = 0;

        for (var i = 0; i < chars.Length; i++)
        {
            var value = 0;
            for (var b = 0; b < 5; b++, bit++)
            {
                var source = bytes[bit / 8];
                var taken = (source >> (7 - (bit % 8))) & 1;
                value = (value << 1) | taken;
            }
            chars[i] = Alphabet[value];
        }

        return new string(chars);
    }
}

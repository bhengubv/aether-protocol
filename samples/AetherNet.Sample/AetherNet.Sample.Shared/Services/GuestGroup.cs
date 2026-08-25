// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// The network a phone raises to give somebody the app.
///
/// <para>
/// Not the same thing as the Circle's group at all, and the difference is the whole point.
/// <see cref="GroupCredentials"/> derives a name and passphrase from the host's public key so two
/// people who have already added each other can both compute it without exchanging anything. A
/// stranger holds no key and can derive nothing — so this one is minted fresh, and the tap carries it.
/// </para>
///
/// <para>
/// <b>The name is the thing being designed here.</b> Everything else we tried put an address in front
/// of somebody: a raw IP under a browser's "not secure" warning, which is precisely what people are
/// taught to back away from. A network with a person's name on it is something they join without
/// thinking, several times a week. Same bytes afterwards; a completely different act.
/// </para>
/// </summary>
public static class GuestGroup
{
    /// <summary>
    /// Android will not let a Wi-Fi Direct group be called anything else.
    /// </summary>
    /// <remarks>
    /// Not a convention we chose and not one we can opt out of — <c>setNetworkName</c> rejects any
    /// name without it. It is ugly and it is the price of the whole idea working, so it is spent
    /// deliberately rather than pretended away.
    /// </remarks>
    public const string RequiredPrefix = "DIRECT-";

    /// <summary>Android's ceiling for a network name.</summary>
    public const int LongestName = 32;

    /// <summary>What the network is called when we have no name for the person offering.</summary>
    public const string Anonymous = "Aether";

    /// <summary>
    /// Crockford's alphabet: no I, L, O or U, so a passphrase cannot be misread and cannot
    /// accidentally spell anything.
    /// </summary>
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>
    /// How long the passphrase is.
    /// </summary>
    /// <remarks>
    /// Sixteen characters of a 32-symbol alphabet is eighty bits, for a network that exists for a few
    /// minutes. Nobody ever reads it — the tap carries it — so the only thing length costs is bytes in
    /// an NDEF record, and the only thing shortness costs is the whole point.
    /// </remarks>
    public const int PassphraseLength = 16;

    /// <summary>
    /// A fresh network for one handover.
    /// </summary>
    /// <param name="who">
    ///   The giver, as a person rather than an address — a name if we have one, otherwise their
    ///   AetherTag. This is the only part a human being will ever read.
    /// </param>
    public static WifiDirectCredentials For(string? who)
    {
        var label = Label(who);

        // The prefix is forced, so the name is what is left of thirty-two characters after it.
        var room = LongestName - RequiredPrefix.Length;
        if (label.Length > room) label = label[..room];

        return new WifiDirectCredentials(RequiredPrefix + label, Passphrase());
    }

    /// <summary>What the person will see in their Wi-Fi list, minus the forced prefix.</summary>
    /// <remarks>
    /// Kept to characters that survive being shown in a Wi-Fi picker on any phone. A name with
    /// something exotic in it renders as a box on somebody's handset, and a box is not a person.
    /// </remarks>
    public static string Label(string? who)
    {
        var clean = new StringBuilder(LongestName);

        foreach (var c in (who ?? "").Trim())
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_' or ' ') clean.Append(c);
            if (clean.Length >= LongestName) break;
        }

        var label = clean.ToString().Trim();
        return label.Length == 0 ? Anonymous : $"{Anonymous} {label}";
    }

    /// <summary>A key nobody has to read, because the tap carries it.</summary>
    public static string Passphrase()
    {
        var chars = new char[PassphraseLength];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }
}

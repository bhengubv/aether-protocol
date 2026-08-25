// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Text;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// The Wi-Fi credentials a tap hands over, in the format a stock phone already understands.
///
/// <para>
/// This is Wi-Fi Simple Configuration — the Wi-Fi Alliance's own credential blob, carried in an NDEF
/// record of type <c>application/vnd.wfa.wsc</c>. It is what makes a phone offer to join a network
/// when you touch it against a router or a printer, and it has been in Android since long before any
/// of this. Nothing here is ours except the values.
/// </para>
///
/// <para>
/// <b>Why it matters more than it looks.</b> Every other way of getting a stranger's phone onto our
/// network ends in somebody reading an address. A raw IP under a browser's "not secure" warning is a
/// thing people are taught to back away from, and rightly. A network with a person's name on it is a
/// thing people join a dozen times a week without a second thought. Same bytes afterwards; completely
/// different act.
/// </para>
///
/// <para>
/// Fields are big-endian type-length-value throughout, which is the spec and is also the opposite of
/// how the rest of this app writes numbers — a byte order swapped here produces a record a phone
/// silently ignores, with nothing anywhere to say why.
/// </para>
/// </summary>
public static class WifiHandover
{
    /// <summary>The NDEF record type Android reads Wi-Fi credentials out of.</summary>
    public const string MimeType = "application/vnd.wfa.wsc";

    // Wi-Fi Simple Configuration attribute ids.
    private const ushort Credential = 0x100E;
    private const ushort NetworkIndex = 0x1026;
    private const ushort Ssid = 0x1045;
    private const ushort AuthType = 0x1003;
    private const ushort EncryptType = 0x100F;
    private const ushort NetworkKey = 0x1027;
    private const ushort MacAddress = 0x1020;

    /// <summary>WPA2 with a pre-shared key — what a Wi-Fi Direct group actually runs.</summary>
    private const ushort Wpa2Psk = 0x0020;

    /// <summary>AES. TKIP is long dead and offering it invites a phone to negotiate down to it.</summary>
    private const ushort Aes = 0x0008;

    /// <summary>An SSID is at most 32 bytes; a WPA passphrase is 8 to 63.</summary>
    public const int LongestSsid = 32;

    /// <summary>The shortest passphrase WPA2 permits.</summary>
    public const int ShortestPassphrase = 8;

    /// <summary>The longest.</summary>
    public const int LongestPassphrase = 63;

    /// <summary>
    /// One NDEF message carrying credentials for a network.
    /// </summary>
    /// <param name="ssid">The network's name — what the person will see their phone joining.</param>
    /// <param name="passphrase">The key. Never shown to anybody; the tap carries it.</param>
    public static byte[] Message(string ssid, string passphrase)
    {
        var payload = Credentials(ssid, passphrase);
        var type = Encoding.ASCII.GetBytes(MimeType);

        // Media-type record: MB | ME | SR, TNF 0x02 (a MIME type).
        var record = new byte[3 + type.Length + payload.Length];
        record[0] = 0x80 | 0x40 | 0x10 | 0x02;
        record[1] = (byte)type.Length;
        record[2] = (byte)payload.Length;
        type.CopyTo(record, 3);
        payload.CopyTo(record, 3 + type.Length);
        return record;
    }

    /// <summary>The credential blob itself, without the NDEF wrapper.</summary>
    public static byte[] Credentials(string ssid, string passphrase)
    {
        if (string.IsNullOrEmpty(ssid))
            throw new ArgumentException("A network has to have a name.", nameof(ssid));

        var name = Encoding.UTF8.GetBytes(ssid);
        if (name.Length > LongestSsid)
            throw new ArgumentException($"An SSID is at most {LongestSsid} bytes.", nameof(ssid));

        var key = Encoding.UTF8.GetBytes(passphrase ?? "");
        if (key.Length is < ShortestPassphrase or > LongestPassphrase)
            throw new ArgumentException(
                $"A WPA2 passphrase is {ShortestPassphrase} to {LongestPassphrase} bytes.", nameof(passphrase));

        var inner = new List<byte>(64);
        Add(inner, NetworkIndex, [0x01]);
        Add(inner, Ssid, name);
        Add(inner, AuthType, Be(Wpa2Psk));
        Add(inner, EncryptType, Be(Aes));
        Add(inner, NetworkKey, key);
        // Zeroes rather than the real address. A phone joins on the name and the key; broadcasting a
        // MAC on a tap would hand anyone who read it a stable identifier for this handset, which is
        // exactly the thing the rotating wire address exists to avoid.
        Add(inner, MacAddress, new byte[6]);

        var outer = new List<byte>(inner.Count + 4);
        Add(outer, Credential, inner.ToArray());
        return outer.ToArray();
    }

    /// <summary>
    /// Read credentials back out. The counterpart to <see cref="Credentials"/>, and the reason it can
    /// be trusted without holding two phones together.
    /// </summary>
    public static (string Ssid, string Passphrase)? Read(byte[]? credentials)
    {
        if (credentials is null || credentials.Length < 4) return null;

        var top = Fields(credentials);
        if (!top.TryGetValue(Credential, out var inner)) return null;

        var fields = Fields(inner);
        if (!fields.TryGetValue(Ssid, out var name)) return null;
        if (!fields.TryGetValue(NetworkKey, out var key)) return null;

        return (Encoding.UTF8.GetString(name), Encoding.UTF8.GetString(key));
    }

    /// <summary>
    /// Walk a type-length-value run, stopping at the first thing that does not fit.
    /// </summary>
    /// <remarks>
    /// Fed by whatever a reader hands back, so a length that runs off the end is expected input rather
    /// than an exceptional case.
    /// </remarks>
    private static Dictionary<ushort, byte[]> Fields(byte[] data)
    {
        var found = new Dictionary<ushort, byte[]>();
        var at = 0;

        while (at + 4 <= data.Length)
        {
            var id = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at));
            var length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at + 2));
            at += 4;

            if (length > data.Length - at) break;
            found[id] = data[at..(at + length)];
            at += length;
        }

        return found;
    }

    private static void Add(List<byte> into, ushort id, byte[] value)
    {
        into.AddRange(Be(id));
        into.AddRange(Be((ushort)value.Length));
        into.AddRange(value);
    }

    private static byte[] Be(ushort value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        return bytes;
    }
}

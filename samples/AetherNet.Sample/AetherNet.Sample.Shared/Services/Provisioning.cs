// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// The tap that asks Android to install us itself.
///
/// <para>
/// Every other route ends with a person being asked to do something — read an address, trust a page,
/// accept a file. This one does not. Android's own provisioning path takes a network, a location and
/// a <b>fingerprint</b>, joins the network, fetches what is at the location, refuses it unless the
/// bytes hash to that fingerprint, and installs it. The operating system does all of it, and nobody
/// reads an address, because the address is never rendered anywhere.
/// </para>
///
/// <para>
/// <b>The fingerprint is the interesting part, not the location.</b> A tap that says "here is a place"
/// is a link. A tap that says "here are the bytes I mean, and here is one place they happen to be" is
/// a claim that can be checked — so the bytes may come from the giver, or from anyone else in the room
/// already holding them, and a newcomer still cannot be handed something else. That is the same shape
/// AetherNet already uses for content, turned on the app itself.
/// </para>
///
/// <para>
/// <b>What is unproven, said plainly.</b> Android 10 stopped one phone <i>beaming</i> provisioning to
/// another and kept tags. We are a tag — a Type 4 tag, which is what <c>TouchMyBlood</c> has been all
/// evening — so whether the setup wizard accepts this from us is an open question nobody has written
/// down either way. It is settled by touching two phones, not by reading.
/// </para>
/// </summary>
public static class Provisioning
{
    /// <summary>The type Android's provisioning component listens for.</summary>
    public const string MimeType = "application/com.android.managedprovisioning";

    /// <summary>Which package is being installed.</summary>
    public const string PackageKey = "android.app.extra.PROVISIONING_DEVICE_ADMIN_PACKAGE_NAME";

    /// <summary>Where the installer can be fetched. Never shown to anybody — the wizard consumes it.</summary>
    public const string LocationKey =
        "android.app.extra.PROVISIONING_DEVICE_ADMIN_PACKAGE_DOWNLOAD_LOCATION";

    /// <summary>The fingerprint the fetched bytes must match, or nothing is installed.</summary>
    public const string ChecksumKey = "android.app.extra.PROVISIONING_DEVICE_ADMIN_PACKAGE_CHECKSUM";

    /// <summary>The network to join in order to reach that location.</summary>
    public const string SsidKey = "android.app.extra.PROVISIONING_WIFI_SSID";

    /// <summary>Its key.</summary>
    public const string PassphraseKey = "android.app.extra.PROVISIONING_WIFI_PASSWORD";

    /// <summary>How that network is protected.</summary>
    public const string SecurityKey = "android.app.extra.PROVISIONING_WIFI_SECURITY_TYPE";

    /// <summary>Leave the phone's own apps alone — we are joining it, not taking it over.</summary>
    public const string LeaveAppsKey = "android.app.extra.PROVISIONING_LEAVE_ALL_SYSTEM_APPS_ENABLED";

    /// <summary>What <see cref="SecurityKey"/> is set to for the groups we raise.</summary>
    public const string Wpa = "WPA";

    /// <summary>
    /// The fingerprint of an installer.
    /// </summary>
    /// <remarks>
    /// SHA-256, base64, url-safe alphabet, no padding — the shape this path has always read. Padding
    /// characters and the ordinary alphabet both travel badly through the places this string ends up,
    /// so neither is used.
    /// </remarks>
    public static string Fingerprint(byte[] installer)
    {
        ArgumentNullException.ThrowIfNull(installer);

        return Convert.ToBase64String(SHA256.HashData(installer))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    /// <summary>
    /// The payload itself: Java properties, one per line, which is the format this path speaks.
    /// </summary>
    public static string Properties(
        string package, string location, string fingerprint, string ssid, string passphrase)
    {
        if (string.IsNullOrWhiteSpace(package)) throw new ArgumentException("no package", nameof(package));
        if (string.IsNullOrWhiteSpace(location)) throw new ArgumentException("no location", nameof(location));
        if (string.IsNullOrWhiteSpace(fingerprint)) throw new ArgumentException("no fingerprint", nameof(fingerprint));
        if (string.IsNullOrWhiteSpace(ssid)) throw new ArgumentException("no network", nameof(ssid));
        if (string.IsNullOrWhiteSpace(passphrase)) throw new ArgumentException("no passphrase", nameof(passphrase));

        var text = new StringBuilder();

        // The network is named plainly, without the quotes a Wi-Fi configuration carries internally.
        // Provisioning adds those itself, and a name that arrives already quoted is looked for
        // literally — quotes and all — and never found.
        Line(text, SsidKey, ssid);
        Line(text, SecurityKey, Wpa);
        Line(text, PassphraseKey, passphrase);
        Line(text, PackageKey, package);
        Line(text, LocationKey, location);
        Line(text, ChecksumKey, fingerprint);
        Line(text, LeaveAppsKey, "true");

        return text.ToString();
    }

    private static void Line(StringBuilder text, string key, string value)
    {
        Escape(text, key, isKey: true);
        text.Append('=');
        Escape(text, value, isKey: false);
        text.Append('\n');
    }

    /// <summary>
    /// Properties escaping, which is not the same on both sides of the equals sign.
    /// </summary>
    /// <remarks>
    /// A key ends at the first unescaped space, colon or equals, so all three have to be escaped
    /// there. A value runs to the end of the line and only needs its backslashes and line breaks
    /// handled — plus a leading space, which would otherwise be eaten as padding. Our own passphrases
    /// are drawn from a small alphabet and would survive naive handling; a person's name inside a
    /// network name would not, and that is exactly what these carry.
    /// </remarks>
    private static void Escape(StringBuilder text, string raw, bool isKey)
    {
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            switch (c)
            {
                case '\\':
                    text.Append('\\').Append('\\');
                    break;
                case '\n':
                    text.Append('\\').Append('n');
                    break;
                case '\r':
                    text.Append('\\').Append('r');
                    break;
                case '\t':
                    text.Append('\\').Append('t');
                    break;
                case ' ' when isKey || i == 0:
                    text.Append('\\').Append(' ');
                    break;
                case '=' or ':' when isKey:
                    text.Append('\\').Append(c);
                    break;
                case '#' or '!' when isKey && i == 0:
                    text.Append('\\').Append(c);
                    break;
                default:
                    text.Append(c);
                    break;
            }
        }
    }

    /// <summary>
    /// The whole tap, ready to be handed to a reader.
    /// </summary>
    public static byte[] Message(
        string package, string location, string fingerprint, string ssid, string passphrase) =>
        MimeRecord(MimeType,
            Encoding.UTF8.GetBytes(Properties(package, location, fingerprint, ssid, passphrase)));

    /// <summary>
    /// One NDEF record of a given media type, first and last in its message.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This payload is too big to be a short record, and that is not a detail.</b> The keys alone
    /// run past two hundred bytes before a single value is written, and a short record cannot describe
    /// more than 255 — so the length field is four bytes and the short-record bit stays clear. Writing
    /// it the way the Wi-Fi handover record is written would declare a length of the real one modulo
    /// 256, and a reader would follow that off the end of the payload.
    /// </para>
    /// <para>
    /// Both forms are produced here rather than always taking the long one, because a reader is
    /// entitled to either and the short form is what every other record in this app emits.
    /// </para>
    /// </remarks>
    public static byte[] MimeRecord(string mime, byte[] payload)
    {
        if (string.IsNullOrWhiteSpace(mime)) throw new ArgumentException("no type", nameof(mime));
        ArgumentNullException.ThrowIfNull(payload);

        var type = Encoding.ASCII.GetBytes(mime);
        if (type.Length > byte.MaxValue) throw new ArgumentException("type is too long", nameof(mime));

        var brief = payload.Length <= byte.MaxValue;

        // MessageBegin | MessageEnd | (ShortRecord) | TNF 0x02, a media type.
        var flags = (byte)(0x80 | 0x40 | (brief ? 0x10 : 0x00) | 0x02);

        var record = new byte[2 + (brief ? 1 : 4) + type.Length + payload.Length];
        var at = 0;

        record[at++] = flags;
        record[at++] = (byte)type.Length;

        if (brief)
        {
            record[at++] = (byte)payload.Length;
        }
        else
        {
            record[at++] = (byte)(payload.Length >> 24);
            record[at++] = (byte)(payload.Length >> 16);
            record[at++] = (byte)(payload.Length >> 8);
            record[at++] = (byte)payload.Length;
        }

        type.CopyTo(record, at);
        at += type.Length;
        payload.CopyTo(record, at);

        return record;
    }
}

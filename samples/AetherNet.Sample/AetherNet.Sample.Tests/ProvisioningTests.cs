// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Services;
using System.Text;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// The tap that asks Android to install us itself.
///
/// <para>
/// Everything else we built ends with a person being asked to do something — read an address, trust a
/// page, accept a file — and a stranger is right to refuse all three. This one hands the operating
/// system a network, a fingerprint and a place, and it does the rest without rendering any of it.
/// </para>
///
/// <para>
/// Three things have to be exactly right or the tap is silently wasted, and none of them can be seen
/// by looking at a phone: the payload has to be properties Android can parse, the record has to
/// declare its own length honestly, and the fingerprint has to be the one shape this path reads.
/// </para>
/// </summary>
public class ProvisioningTests
{
    private const string Package = "com.bhengubv.aethernet";
    private const string Location = "http://192.168.49.1:40813/tmb/9b2993fde0092c4f/aether.apk";
    private const string Ssid = "DIRECT-Aether Y6TK9-EW9KK";
    private const string Passphrase = "8QK2M4TVXR7NPJ3W";

    private static string Fingerprint() => Provisioning.Fingerprint([1, 2, 3, 4]);

    private static string Payload() =>
        Provisioning.Properties(Package, Location, Fingerprint(), Ssid, Passphrase);

    // ── The fingerprint ──────────────────────────────────────────────────────

    /// <summary>
    /// It is the SHA-256 of the bytes, in the alphabet this path reads.
    /// </summary>
    /// <remarks>
    /// Computed against the hash directly rather than a copied constant, so the test says what the
    /// value means instead of only that it has not changed.
    /// </remarks>
    [Fact]
    public void Fingerprint_is_the_sha256_of_the_installer()
    {
        byte[] installer = [1, 2, 3, 4];

        var expected = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(installer))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

        Assert.Equal(expected, Provisioning.Fingerprint(installer));
    }

    /// <summary>
    /// Padding would be carried through the tap and rejected at the far end.
    /// </summary>
    [Fact]
    public void Fingerprint_carries_no_padding()
    {
        Assert.DoesNotContain('=', Provisioning.Fingerprint([9, 9, 9]));
    }

    /// <summary>
    /// The ordinary base64 alphabet contains two characters that do not survive the journey.
    /// </summary>
    /// <remarks>
    /// Sixty-four different inputs, because a plus or a slash appears in roughly half of hashes and a
    /// single sample would pass while broken.
    /// </remarks>
    [Fact]
    public void Fingerprint_never_contains_plus_or_slash()
    {
        for (var i = 0; i < 64; i++)
        {
            var print = Provisioning.Fingerprint([(byte)i, (byte)(i * 7), (byte)(i * 31)]);
            Assert.DoesNotContain('+', print);
            Assert.DoesNotContain('/', print);
        }
    }

    /// <summary>
    /// One byte different is a different app, and the whole design rests on that being caught.
    /// </summary>
    [Fact]
    public void Fingerprint_changes_when_a_single_byte_changes()
    {
        Assert.NotEqual(Provisioning.Fingerprint([1, 2, 3, 4]), Provisioning.Fingerprint([1, 2, 3, 5]));
    }

    /// <summary>A hash of forty-three characters is a hash; anything shorter is a stub.</summary>
    [Fact]
    public void Fingerprint_is_a_full_length_digest()
    {
        Assert.Equal(43, Provisioning.Fingerprint([0]).Length);
    }

    // ── The payload ──────────────────────────────────────────────────────────

    /// <summary>
    /// Every key the far end looks for is present.
    /// </summary>
    /// <remarks>
    /// Named individually rather than counted, because a missing network key and a missing checksum
    /// fail in completely different ways — one cannot reach us, the other installs anything.
    /// </remarks>
    [Theory]
    [InlineData(Provisioning.SsidKey)]
    [InlineData(Provisioning.SecurityKey)]
    [InlineData(Provisioning.PassphraseKey)]
    [InlineData(Provisioning.PackageKey)]
    [InlineData(Provisioning.LocationKey)]
    [InlineData(Provisioning.ChecksumKey)]
    [InlineData(Provisioning.LeaveAppsKey)]
    public void Payload_carries_every_key(string key)
    {
        Assert.Contains(key + "=", Payload(), StringComparison.Ordinal);
    }

    /// <summary>Each key sits on its own line, which is what makes it properties and not soup.</summary>
    [Fact]
    public void Payload_is_one_setting_per_line()
    {
        var lines = Payload().Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(7, lines.Length);
        Assert.All(lines, line => Assert.Contains('=', line));
    }

    /// <summary>
    /// The values arrive as they were given.
    /// </summary>
    [Fact]
    public void Payload_carries_the_values_unchanged()
    {
        var payload = Payload();

        Assert.Contains(Provisioning.SsidKey + "=" + Ssid, payload, StringComparison.Ordinal);
        Assert.Contains(Provisioning.PassphraseKey + "=" + Passphrase, payload, StringComparison.Ordinal);
        Assert.Contains(Provisioning.LocationKey + "=" + Location, payload, StringComparison.Ordinal);
    }

    /// <summary>
    /// A network name has a space in it, and a space is where a properties key ends.
    /// </summary>
    /// <remarks>
    /// The name is a value, so its space must survive untouched. Escaping it there would put a
    /// backslash into the network name and the phone would hunt for a network nobody is hosting.
    /// </remarks>
    [Fact]
    public void A_space_inside_a_network_name_is_left_alone()
    {
        Assert.Contains("=DIRECT-Aether Y6TK9-EW9KK\n", Payload(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A leading space is padding to a properties reader, so it has to be spelled out.
    /// </summary>
    [Fact]
    public void A_leading_space_in_a_value_is_escaped()
    {
        var payload = Provisioning.Properties(Package, Location, Fingerprint(), " Aether", Passphrase);

        Assert.Contains(Provisioning.SsidKey + "=\\ Aether", payload, StringComparison.Ordinal);
    }

    /// <summary>
    /// A backslash in a value is one character, not the start of an escape.
    /// </summary>
    [Fact]
    public void A_backslash_in_a_value_is_doubled()
    {
        var payload = Provisioning.Properties(Package, Location, Fingerprint(), "Aether", "a\\b");

        Assert.Contains(Provisioning.PassphraseKey + "=a\\\\b", payload, StringComparison.Ordinal);
    }

    /// <summary>
    /// A newline in a value would end the line early and turn the rest into a key nobody reads.
    /// </summary>
    [Fact]
    public void A_newline_in_a_value_cannot_end_the_line()
    {
        var payload = Provisioning.Properties(Package, Location, Fingerprint(), "Aether", "a\nb");
        var lines = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(7, lines.Length);
        Assert.Contains(Provisioning.PassphraseKey + "=a\\nb", payload, StringComparison.Ordinal);
    }

    /// <summary>Nothing worth handing over is missing.</summary>
    [Theory]
    [InlineData("", Location, "print", Ssid, Passphrase)]
    [InlineData(Package, "", "print", Ssid, Passphrase)]
    [InlineData(Package, Location, "", Ssid, Passphrase)]
    [InlineData(Package, Location, "print", "", Passphrase)]
    [InlineData(Package, Location, "print", Ssid, "")]
    public void Nothing_may_be_left_out(
        string package, string location, string print, string ssid, string passphrase)
    {
        Assert.Throws<ArgumentException>(
            () => Provisioning.Properties(package, location, print, ssid, passphrase));
    }

    // ── The record ───────────────────────────────────────────────────────────

    /// <summary>
    /// A reader takes the first record and ignores the rest, so ours is first and last.
    /// </summary>
    [Fact]
    public void The_record_is_alone_in_its_message()
    {
        var message = Provisioning.Message(Package, Location, Fingerprint(), Ssid, Passphrase);

        Assert.Equal(0x80, message[0] & 0x80);   // MessageBegin
        Assert.Equal(0x40, message[0] & 0x40);   // MessageEnd
    }

    /// <summary>It is a media type, which is how the far end knows to look at it at all.</summary>
    [Fact]
    public void The_record_is_a_media_type()
    {
        var message = Provisioning.Message(Package, Location, Fingerprint(), Ssid, Passphrase);

        Assert.Equal(0x02, message[0] & 0x07);
    }

    /// <summary>And the media type is the one Android's provisioning listens for.</summary>
    [Fact]
    public void The_record_names_the_provisioning_type()
    {
        var message = Provisioning.Message(Package, Location, Fingerprint(), Ssid, Passphrase);
        var type = Encoding.ASCII.GetString(message, 6, message[1]);

        Assert.Equal(Provisioning.MimeType, type);
    }

    /// <summary>
    /// <b>The payload is past 255 bytes, so the short form cannot describe it.</b>
    /// </summary>
    /// <remarks>
    /// This is the trap. Every other record in this app is short, and writing this one the same way
    /// declares its length modulo 256 — a number a reader believes, and then walks off the end of the
    /// payload holding a fraction of the settings.
    /// </remarks>
    [Fact]
    public void A_payload_past_the_short_limit_is_written_long()
    {
        var message = Provisioning.Message(Package, Location, Fingerprint(), Ssid, Passphrase);

        Assert.True(Payload().Length > byte.MaxValue,
            "the payload should be past the short-record limit; if it is not, this test proves nothing");

        Assert.Equal(0x00, message[0] & 0x10);   // ShortRecord clear
    }

    /// <summary>
    /// The declared length is the real one, in the order a reader reads it.
    /// </summary>
    [Fact]
    public void A_long_record_declares_its_true_length()
    {
        var payload = Encoding.UTF8.GetBytes(Payload());
        var message = Provisioning.Message(Package, Location, Fingerprint(), Ssid, Passphrase);

        var declared = (message[2] << 24) | (message[3] << 16) | (message[4] << 8) | message[5];

        Assert.Equal(payload.Length, declared);
        Assert.Equal(2 + 4 + Provisioning.MimeType.Length + payload.Length, message.Length);
    }

    /// <summary>
    /// A reader that follows the header lands exactly on the settings.
    /// </summary>
    /// <remarks>
    /// The whole walk, rather than a spot check: length fields are the one thing a phone trusts
    /// without verifying, so being off by one here is invisible until a tap does nothing.
    /// </remarks>
    [Fact]
    public void A_reader_following_the_header_recovers_the_settings()
    {
        var message = Provisioning.Message(Package, Location, Fingerprint(), Ssid, Passphrase);

        var typeLength = message[1];
        var payloadLength = (message[2] << 24) | (message[3] << 16) | (message[4] << 8) | message[5];
        var payload = Encoding.UTF8.GetString(message, 6 + typeLength, payloadLength);

        Assert.Equal(Payload(), payload);
    }

    /// <summary>Small payloads still take the short form every other record in this app uses.</summary>
    [Fact]
    public void A_small_payload_is_written_short()
    {
        var record = Provisioning.MimeType is { } type
            ? Provisioning.MimeRecord(type, [1, 2, 3])
            : throw new InvalidOperationException();

        Assert.Equal(0x10, record[0] & 0x10);   // ShortRecord set
        Assert.Equal(3, record[2]);
    }

    /// <summary>
    /// The boundary itself, which is where an off-by-one lives.
    /// </summary>
    [Theory]
    [InlineData(255, true)]
    [InlineData(256, false)]
    public void The_short_form_stops_at_two_hundred_and_fifty_five(int size, bool brief)
    {
        var record = Provisioning.MimeRecord("a/b", new byte[size]);

        Assert.Equal(brief ? 0x10 : 0x00, record[0] & 0x10);
        Assert.Equal(brief ? 2 + 1 + 3 + size : 2 + 4 + 3 + size, record.Length);
    }

    /// <summary>An empty payload is a tap that hands over nothing, and is still well formed.</summary>
    [Fact]
    public void An_empty_payload_is_still_a_record()
    {
        var record = Provisioning.MimeRecord("a/b", []);

        Assert.Equal(0x10, record[0] & 0x10);
        Assert.Equal(0, record[2]);
    }

    /// <summary>A record with no type is not a record.</summary>
    [Fact]
    public void A_record_needs_a_type()
    {
        Assert.Throws<ArgumentException>(() => Provisioning.MimeRecord("", [1]));
    }

    /// <summary>
    /// The Wi-Fi handover record this sits beside is untouched by any of it.
    /// </summary>
    /// <remarks>
    /// That one is proven on silicon — a stock phone read it and joined the network — so the guard
    /// here is against a shared record writer quietly changing what the proven tap emits.
    /// </remarks>
    [Fact]
    public void The_proven_wifi_record_is_unchanged()
    {
        var wifi = WifiHandover.Message(Ssid, Passphrase);

        Assert.Equal(0x80 | 0x40 | 0x10 | 0x02, wifi[0]);
        Assert.Equal(WifiHandover.MimeType.Length, wifi[1]);
    }
}

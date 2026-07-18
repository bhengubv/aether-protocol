// SPDX-License-Identifier: MIT

using System.Text;
using AetherNet.BitTorrent.Bencoding;
using Xunit;

namespace AetherNet.BitTorrent.Tests;

public class BencodeTests
{
    private static byte[] B(string ascii) => Encoding.ASCII.GetBytes(ascii);
    private static string S(byte[] bytes) => Encoding.ASCII.GetString(bytes);

    // ── Integers ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("i0e", 0L)]
    [InlineData("i42e", 42L)]
    [InlineData("i-42e", -42L)]
    [InlineData("i9223372036854775807e", long.MaxValue)]
    [InlineData("i-9223372036854775808e", long.MinValue)]
    public void Integer_roundtrips(string enc, long value)
    {
        var v = Bencode.Decode(B(enc));
        Assert.Equal(value, v.AsInteger());
        Assert.Equal(enc, S(v.Encode()));
    }

    [Theory]
    [InlineData("ie")]                        // empty
    [InlineData("i-0e")]                       // negative zero
    [InlineData("i03e")]                       // leading zero
    [InlineData("i-e")]                        // lone minus
    [InlineData("i1 e")]                       // non-digit
    [InlineData("i9223372036854775808e")]      // 64-bit overflow
    [InlineData("i1")]                         // unterminated
    public void Integer_rejects_malformed(string enc) =>
        Assert.Throws<BencodeException>(() => Bencode.Decode(B(enc)));

    // ── Strings ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("4:spam", "spam")]
    [InlineData("0:", "")]
    [InlineData("11:hello world", "hello world")]
    public void String_roundtrips(string enc, string text)
    {
        var v = Bencode.Decode(B(enc));
        Assert.Equal(text, v.AsText());
        Assert.Equal(enc, S(v.Encode()));
    }

    [Fact]
    public void String_holds_raw_non_text_bytes()
    {
        var raw = new byte[] { 0x00, 0xFF, 0x10, 0x3A }; // includes a ':' byte
        var enc = Bencode.Encode(new BencodeString(raw));
        Assert.Equal("4:", S(enc[..2]));
        Assert.Equal(raw, Bencode.Decode(enc).AsBytes());
    }

    [Theory]
    [InlineData("5:spam")]   // length exceeds data
    [InlineData("03:abc")]   // leading-zero length
    [InlineData("1")]        // no ':'
    public void String_rejects_malformed(string enc) =>
        Assert.Throws<BencodeException>(() => Bencode.Decode(B(enc)));

    // ── Lists ─────────────────────────────────────────────────────────────────

    [Fact]
    public void List_roundtrips()
    {
        var v = Bencode.Decode(B("l4:spam4:eggse"));
        var items = v.AsList();
        Assert.Equal(2, items.Count);
        Assert.Equal("spam", items[0].AsText());
        Assert.Equal("eggs", items[1].AsText());
        Assert.Equal("l4:spam4:eggse", S(v.Encode()));
    }

    [Theory]
    [InlineData("le")]                 // empty
    [InlineData("lli1ei2eeli3eee")]    // nested
    [InlineData("ld1:ai1eee")]         // list of dict
    public void List_shapes_roundtrip(string enc) =>
        Assert.Equal(enc, S(Bencode.Decode(B(enc)).Encode()));

    [Fact]
    public void Unterminated_list_rejected() =>
        Assert.Throws<BencodeException>(() => Bencode.Decode(B("l4:spam")));

    // ── Dictionaries ──────────────────────────────────────────────────────────

    [Fact]
    public void Dictionary_roundtrips()
    {
        var d = Bencode.Decode(B("d3:cow3:moo4:spam4:eggse")).AsDictionary();
        Assert.Equal("moo", d["cow"]!.AsText());
        Assert.Equal("eggs", d["spam"]!.AsText());
        Assert.Equal("d3:cow3:moo4:spam4:eggse", S(Bencode.Encode(d)));
    }

    [Fact]
    public void Empty_dictionary_roundtrips()
    {
        var v = Bencode.Decode(B("de"));
        Assert.Equal(0, v.AsDictionary().Count);
        Assert.Equal("de", S(v.Encode()));
    }

    [Fact]
    public void Dictionary_encode_sorts_keys_canonically()
    {
        var d = new BencodeDictionary();
        d.Add("spam", new BencodeString("eggs"));
        d.Add("cow", new BencodeString("moo")); // inserted out of canonical order
        Assert.Equal("d3:cow3:moo4:spam4:eggse", S(d.Encode()));
    }

    [Fact]
    public void Dictionary_rejects_duplicate_key() =>
        Assert.Throws<BencodeException>(() => Bencode.Decode(B("d1:a1:x1:a1:ye")));

    [Fact]
    public void Dictionary_rejects_non_string_key() =>
        Assert.Throws<BencodeException>(() => Bencode.Decode(B("di1e1:ae")));

    [Fact]
    public void Dictionary_missing_key_returns_null() =>
        Assert.Null(Bencode.Decode(B("d1:a1:be")).AsDictionary()["missing"]);

    // ── Structure / robustness ────────────────────────────────────────────────

    [Fact]
    public void Trailing_data_is_rejected() =>
        Assert.Throws<BencodeException>(() => Bencode.Decode(B("i1ei2e")));

    [Fact]
    public void Decode_with_consumed_allows_trailing()
    {
        var v = Bencode.Decode(B("i1e_rest"), out int consumed);
        Assert.Equal(1L, v.AsInteger());
        Assert.Equal(3, consumed);
    }

    [Fact]
    public void Empty_input_is_rejected() =>
        Assert.Throws<BencodeException>(() => Bencode.Decode(Array.Empty<byte>()));

    // ── Torrent-shaped structure ──────────────────────────────────────────────

    [Fact]
    public void Torrent_like_info_dict_roundtrips_byte_exact()
    {
        var info = new BencodeDictionary();
        info.Add("length", new BencodeInteger(1024));
        info.Add("name", new BencodeString("file.bin"));
        info.Add("piece length", new BencodeInteger(16384));
        info.Add("pieces", new BencodeString(new byte[20])); // one SHA-1 piece hash worth of bytes

        var root = new BencodeDictionary();
        root.Add("announce", new BencodeString("udp://tracker.example:6969/announce"));
        root.Add("info", info);

        var encoded = root.Encode();
        var decoded = Bencode.Decode(encoded);

        // Byte-exact round-trip — the property info-hash relies on.
        Assert.Equal(encoded, decoded.Encode());

        var di = decoded.AsDictionary()["info"]!.AsDictionary();
        Assert.Equal(1024L, di["length"]!.AsInteger());
        Assert.Equal("file.bin", di["name"]!.AsText());
        Assert.Equal(16384L, di["piece length"]!.AsInteger());
        Assert.Equal(20, di["pieces"]!.AsBytes().Length);
    }
}

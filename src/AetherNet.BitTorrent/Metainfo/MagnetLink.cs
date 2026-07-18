// SPDX-License-Identifier: MIT

namespace AetherNet.BitTorrent.Metainfo;

/// <summary>
/// A parsed BitTorrent v1 magnet link (<c>magnet:?xt=urn:btih:…</c>).
/// The v1 info-hash may be given as 40 hex characters or 32 base32 characters (BEP-9 / BEP-53).
/// </summary>
public sealed class MagnetLink
{
    /// <summary>20-byte BitTorrent v1 info-hash.</summary>
    public byte[] InfoHashV1 { get; }
    public string? DisplayName { get; }
    public IReadOnlyList<string> Trackers { get; }

    public string InfoHashV1Hex => Convert.ToHexString(InfoHashV1).ToLowerInvariant();

    private MagnetLink(byte[] infoHashV1, string? displayName, IReadOnlyList<string> trackers)
    {
        InfoHashV1 = infoHashV1;
        DisplayName = displayName;
        Trackers = trackers;
    }

    public static MagnetLink Parse(string magnet)
    {
        ArgumentException.ThrowIfNullOrEmpty(magnet);
        const string prefix = "magnet:?";
        if (!magnet.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new TorrentException("not a magnet URI (must start with 'magnet:?')");

        byte[]? infoHash = null;
        string? name = null;
        var trackers = new List<string>();

        foreach (var pair in magnet[prefix.Length..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq < 0) continue;
            string key = pair[..eq];
            string value = Uri.UnescapeDataString(pair[(eq + 1)..]);

            // Strip the optional multi-value suffix (xt.1, tr.2, …).
            int dot = key.IndexOf('.');
            if (dot >= 0) key = key[..dot];

            switch (key.ToLowerInvariant())
            {
                case "xt":
                    if (value.StartsWith("urn:btih:", StringComparison.OrdinalIgnoreCase))
                        infoHash = ParseBtih(value["urn:btih:".Length..]);
                    // urn:btmh: (v2 multihash) is handled when BitTorrent v2 lands.
                    break;
                case "dn":
                    name ??= value;
                    break;
                case "tr":
                    if (!string.IsNullOrWhiteSpace(value)) trackers.Add(value);
                    break;
            }
        }

        if (infoHash is null)
            throw new TorrentException("magnet URI has no v1 info-hash (xt=urn:btih:)");
        return new MagnetLink(infoHash, name, trackers);
    }

    private static byte[] ParseBtih(string value)
    {
        return value.Length switch
        {
            40 => Convert.FromHexString(value),
            32 => Base32Decode(value),
            _ => throw new TorrentException($"invalid btih info-hash length {value.Length} (expected 40 hex or 32 base32)"),
        };
    }

    private static byte[] Base32Decode(string s)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        s = s.TrimEnd('=').ToUpperInvariant();
        var output = new byte[s.Length * 5 / 8];
        int bits = 0, accumulator = 0, index = 0;
        foreach (char c in s)
        {
            int v = alphabet.IndexOf(c);
            if (v < 0) throw new TorrentException($"invalid base32 character '{c}' in info-hash");
            accumulator = (accumulator << 5) | v;
            bits += 5;
            if (bits >= 8)
            {
                output[index++] = (byte)((accumulator >> (bits - 8)) & 0xFF);
                bits -= 8;
            }
        }
        return output;
    }
}

// SPDX-License-Identifier: MIT

using System.Text;
using AetherNet.BitTorrent.Bencoding;
using AetherNet.BitTorrent.PeerWire;

namespace AetherNet.BitTorrent.Extensions;

/// <summary>
/// The BitTorrent extension protocol (BEP-10). Extended messages ride peer-wire message id 20: the
/// first payload byte is the extended sub-message id (0 = the extended handshake), the rest is a
/// bencoded body. The handshake advertises which named extensions a peer supports and the sub-id it
/// wants each one delivered on.
/// </summary>
public static class ExtensionProtocol
{
    public const byte ExtendedMessageId = 20;
    public const byte HandshakeSubId = 0;

    /// <summary>Wrap a bencoded extension body as a peer-wire message with the given sub-id.</summary>
    public static PeerMessage Wrap(byte subId, byte[] body)
    {
        var payload = new byte[1 + body.Length];
        payload[0] = subId;
        body.CopyTo(payload, 1);
        return PeerMessage.Unknown(ExtendedMessageId, payload);
    }

    /// <summary>Build the extended handshake (BEP-10) advertising our supported extensions.</summary>
    public static PeerMessage BuildHandshake(IReadOnlyDictionary<string, int> supported, int? metadataSize = null, int? listenPort = null, string? client = null)
    {
        var m = new BencodeDictionary();
        foreach (var kv in supported) m.Add(kv.Key, new BencodeInteger(kv.Value));

        var d = new BencodeDictionary();
        d.Add("m", m);
        if (metadataSize is { } size) d.Add("metadata_size", new BencodeInteger(size));
        if (listenPort is { } port) d.Add("p", new BencodeInteger(port));
        if (client is { } v) d.Add("v", new BencodeString(v));

        return Wrap(HandshakeSubId, d.Encode());
    }

    /// <summary>Split an extended message into (sub-id, bencoded/raw body).</summary>
    public static (byte SubId, byte[] Body) Split(PeerMessage message)
    {
        if (message.Id != ExtendedMessageId) throw new PeerWireException("not an extension-protocol message");
        if (message.Payload.Length < 1) throw new PeerWireException("extension message has no sub-id");
        return (message.Payload[0], message.Payload[1..]);
    }

    public static ExtensionHandshake ParseHandshake(PeerMessage message)
    {
        var (subId, body) = Split(message);
        if (subId != HandshakeSubId) throw new PeerWireException("not an extended handshake");

        var d = Bencode.Decode(body).AsDictionary();
        var m = (d["m"] ?? new BencodeDictionary()).AsDictionary();

        var supported = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (key, value) in m.SortedEntries())
            supported[Encoding.UTF8.GetString(key)] = (int)value.AsInteger();

        int? metadataSize = d["metadata_size"] is { } ms ? (int)ms.AsInteger() : null;
        return new ExtensionHandshake(supported, metadataSize);
    }
}

/// <summary>A peer's advertised extension support (from its BEP-10 handshake).</summary>
public sealed record ExtensionHandshake(IReadOnlyDictionary<string, int> Supported, int? MetadataSize)
{
    /// <summary>The sub-id the peer wants ut_metadata (BEP-9) messages on, if it supports them.</summary>
    public int? MetadataMessageId => Supported.TryGetValue("ut_metadata", out var id) ? id : null;

    /// <summary>The sub-id the peer wants ut_pex (BEP-11) messages on, if it supports them.</summary>
    public int? PexMessageId => Supported.TryGetValue("ut_pex", out var id) ? id : null;
}

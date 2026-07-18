// SPDX-License-Identifier: MIT

using AetherNet.BitTorrent.Bencoding;

namespace AetherNet.BitTorrent.Dht;

/// <summary>Thrown when a KRPC (DHT) message is malformed.</summary>
public sealed class KrpcException : Exception
{
    public KrpcException(string message) : base(message) { }
}

public enum KrpcType
{
    Query,
    Response,
    Error,
}

/// <summary>
/// A KRPC message (BEP-5) — the bencoded request/response/error envelope the DHT exchanges over UDP.
/// Query: <c>{t, y:"q", q:method, a:{args}}</c>. Response: <c>{t, y:"r", r:{returns}}</c>.
/// Error: <c>{t, y:"e", e:[code, message]}</c>.
/// </summary>
public sealed class KrpcMessage
{
    public required byte[] TransactionId { get; init; }
    public required KrpcType Type { get; init; }

    /// <summary>Method name (queries only).</summary>
    public string? Method { get; init; }

    /// <summary>Arguments (<c>a</c>) for a query, or return values (<c>r</c>) for a response.</summary>
    public BencodeDictionary Body { get; init; } = new();

    /// <summary>Error code + message (errors only).</summary>
    public (int Code, string Message)? Error { get; init; }

    public byte[] Encode()
    {
        var d = new BencodeDictionary();
        d.Add("t", new BencodeString(TransactionId));
        switch (Type)
        {
            case KrpcType.Query:
                d.Add("y", new BencodeString("q"));
                d.Add("q", new BencodeString(Method ?? throw new KrpcException("query is missing a method name")));
                d.Add("a", Body);
                break;
            case KrpcType.Response:
                d.Add("y", new BencodeString("r"));
                d.Add("r", Body);
                break;
            case KrpcType.Error:
                d.Add("y", new BencodeString("e"));
                var (code, message) = Error ?? (203, "Method Unknown");
                d.Add("e", new BencodeList(new BencodeValue[] { new BencodeInteger(code), new BencodeString(message) }));
                break;
        }
        return d.Encode();
    }

    public static KrpcMessage Decode(ReadOnlySpan<byte> bytes)
    {
        var d = Bencode.Decode(bytes).AsDictionary();
        var t = (d["t"] ?? throw new KrpcException("KRPC message missing 't'")).AsBytes();
        var y = (d["y"] ?? throw new KrpcException("KRPC message missing 'y'")).AsText();

        switch (y)
        {
            case "q":
                return new KrpcMessage
                {
                    TransactionId = t,
                    Type = KrpcType.Query,
                    Method = (d["q"] ?? throw new KrpcException("query missing 'q'")).AsText(),
                    Body = (d["a"] ?? new BencodeDictionary()).AsDictionary(),
                };
            case "r":
                return new KrpcMessage
                {
                    TransactionId = t,
                    Type = KrpcType.Response,
                    Body = (d["r"] ?? throw new KrpcException("response missing 'r'")).AsDictionary(),
                };
            case "e":
                var e = (d["e"] ?? throw new KrpcException("error missing 'e'")).AsList();
                if (e.Count < 2) throw new KrpcException("error 'e' must be [code, message]");
                return new KrpcMessage
                {
                    TransactionId = t,
                    Type = KrpcType.Error,
                    Error = ((int)e[0].AsInteger(), e[1].AsText()),
                };
            default:
                throw new KrpcException($"unknown KRPC message type '{y}'");
        }
    }
}

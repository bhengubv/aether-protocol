// SPDX-License-Identifier: MIT

using System.Text;
using AetherNet.Identity;

namespace AetherNet.Node;

/// <summary>
/// The wire format and negotiation for the bind handshake. One place, so every language SDK encodes the
/// same bytes — the vectors in <c>tests/cross-language/node-fixtures.json</c> pin it byte-identical.
///
/// <para>
/// The encoding is intentionally trivial: single-byte version, single-byte counts, and length-prefixed
/// (one byte, so up to 255 bytes) UTF-8 strings. It carries no key material and no secrets — only a
/// version, capability tags, a tag string, and a grant state — so it needs neither framing nor a MAC of
/// its own; the transport underneath it provides those.
/// </para>
/// <list type="bullet">
///   <item><description><c>Hello</c> = version:u8, capCount:u8, then capCount × (len:u8, UTF-8 bytes)</description></item>
///   <item><description><c>Ack</c>  = version:u8, tagLen:u8, tag UTF-8, capCount:u8, caps…, grant:u8</description></item>
/// </list>
/// </summary>
public static class AetherNodeHandshake
{
    /// <summary>The highest bind-protocol version this build speaks.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Encode a <see cref="AetherNodeHello"/> to its canonical wire bytes.</summary>
    public static byte[] Encode(AetherNodeHello hello)
    {
        var buf = new List<byte>(8)
        {
            AsByte(hello.ProtocolVersion, "protocol version"),
            AsByte(hello.Capabilities.Count, "capability count"),
        };
        foreach (var cap in hello.Capabilities)
        {
            WriteString(buf, cap);
        }
        return buf.ToArray();
    }

    /// <summary>Decode a <see cref="AetherNodeHello"/> from its canonical wire bytes.</summary>
    public static AetherNodeHello DecodeHello(ReadOnlySpan<byte> bytes)
    {
        var i = 0;
        var version = ReadByte(bytes, ref i, "protocol version");
        var count = ReadByte(bytes, ref i, "capability count");
        var caps = new string[count];
        for (var k = 0; k < count; k++)
        {
            caps[k] = ReadString(bytes, ref i);
        }
        return new AetherNodeHello(version, caps);
    }

    /// <summary>Encode a <see cref="AetherNodeHelloAck"/> to its canonical wire bytes.</summary>
    public static byte[] Encode(AetherNodeHelloAck ack)
    {
        var buf = new List<byte>(16) { AsByte(ack.ProtocolVersion, "protocol version") };
        WriteString(buf, ack.NodeTag.Value ?? string.Empty);
        buf.Add(AsByte(ack.Capabilities.Count, "capability count"));
        foreach (var cap in ack.Capabilities)
        {
            WriteString(buf, cap);
        }
        buf.Add((byte)ack.Grant);
        return buf.ToArray();
    }

    /// <summary>Decode a <see cref="AetherNodeHelloAck"/> from its canonical wire bytes.</summary>
    public static AetherNodeHelloAck DecodeAck(ReadOnlySpan<byte> bytes)
    {
        var i = 0;
        var version = ReadByte(bytes, ref i, "protocol version");
        var tagStr = ReadString(bytes, ref i);
        var count = ReadByte(bytes, ref i, "capability count");
        var caps = new string[count];
        for (var k = 0; k < count; k++)
        {
            caps[k] = ReadString(bytes, ref i);
        }
        var grant = (GrantState)ReadByte(bytes, ref i, "grant state");
        var tag = string.IsNullOrEmpty(tagStr) ? default : AetherNetTag.Parse(tagStr);
        return new AetherNodeHelloAck(version, tag, caps, grant);
    }

    /// <summary>
    /// Answer a consumer's <paramref name="clientHello"/>: agree on the lower of the two highest versions,
    /// keep only the capabilities both sides support, and stamp the app's current <paramref name="grant"/>.
    /// </summary>
    /// <exception cref="AetherNodeException">
    /// <see cref="AetherNodeErrorCode.VersionUnsupported"/> when there is no common version (>= 1).
    /// </exception>
    public static AetherNodeHelloAck Negotiate(
        AetherNodeHello clientHello,
        int serverVersion,
        IReadOnlyList<string> serverCapabilities,
        AetherNetTag nodeTag,
        GrantState grant)
    {
        var agreed = Math.Min(clientHello.ProtocolVersion, serverVersion);
        if (agreed < 1)
        {
            throw new AetherNodeException(
                AetherNodeErrorCode.VersionUnsupported,
                $"no common bind-protocol version (app {clientHello.ProtocolVersion}, node {serverVersion})");
        }

        var mutual = new List<string>();
        foreach (var cap in serverCapabilities)
        {
            if (Contains(clientHello.Capabilities, cap))
            {
                mutual.Add(cap);
            }
        }
        return new AetherNodeHelloAck(agreed, nodeTag, mutual, grant);
    }

    private static bool Contains(IReadOnlyList<string> list, string value)
    {
        foreach (var item in list)
        {
            if (string.Equals(item, value, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static void WriteString(List<byte> buf, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > byte.MaxValue)
        {
            throw new AetherNodeException(
                AetherNodeErrorCode.Internal, $"handshake string exceeds 255 bytes: {bytes.Length}");
        }
        buf.Add((byte)bytes.Length);
        buf.AddRange(bytes);
    }

    private static string ReadString(ReadOnlySpan<byte> bytes, ref int i)
    {
        var len = ReadByte(bytes, ref i, "string length");
        if (i + len > bytes.Length)
        {
            throw new AetherNodeException(AetherNodeErrorCode.Internal, "handshake truncated: string body");
        }
        var value = Encoding.UTF8.GetString(bytes.Slice(i, len));
        i += len;
        return value;
    }

    private static byte ReadByte(ReadOnlySpan<byte> bytes, ref int i, string what)
    {
        if (i >= bytes.Length)
        {
            throw new AetherNodeException(AetherNodeErrorCode.Internal, $"handshake truncated: {what}");
        }
        return bytes[i++];
    }

    private static byte AsByte(int value, string what)
    {
        if (value is < 0 or > byte.MaxValue)
        {
            throw new AetherNodeException(AetherNodeErrorCode.Internal, $"{what} out of range: {value}");
        }
        return (byte)value;
    }
}

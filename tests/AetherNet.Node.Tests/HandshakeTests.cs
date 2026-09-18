// SPDX-License-Identifier: MIT

using AetherNet.Identity;
using Xunit;

namespace AetherNet.Node.Tests;

public class HandshakeTests
{
    private static string Hex(byte[] bytes) => System.Convert.ToHexString(bytes).ToLowerInvariant();

    private static byte[] FromHex(string hex) => System.Convert.FromHexString(hex);

    [Fact]
    public void Hello_encodes_to_the_pinned_bytes()
    {
        Assert.Equal("0100", Hex(AetherNodeHandshake.Encode(new AetherNodeHello(1, System.Array.Empty<string>()))));
        Assert.Equal("02010178", Hex(AetherNodeHandshake.Encode(new AetherNodeHello(2, new[] { "x" }))));
        Assert.Equal(
            "0102047369676e0473656e64",
            Hex(AetherNodeHandshake.Encode(new AetherNodeHello(1, new[] { NodeCapabilities.Sign, NodeCapabilities.Send }))));
    }

    [Fact]
    public void Hello_round_trips()
    {
        var hello = new AetherNodeHello(1, new[] { "sign", "send", "inbox", "presence" });
        var decoded = AetherNodeHandshake.DecodeHello(AetherNodeHandshake.Encode(hello));
        Assert.Equal(hello.ProtocolVersion, decoded.ProtocolVersion);
        Assert.Equal(hello.Capabilities, decoded.Capabilities);
    }

    [Fact]
    public void Ack_encodes_to_the_pinned_bytes()
    {
        var ack = new AetherNodeHelloAck(1, AetherNetTag.Parse("ABCDE-FGHJK"), new[] { "sign" }, GrantState.Bound);
        Assert.Equal("010b41424344452d4647484a4b01047369676e02", Hex(AetherNodeHandshake.Encode(ack)));
    }

    [Fact]
    public void Ack_round_trips()
    {
        var ack = new AetherNodeHelloAck(
            1, AetherNetTag.Parse("ABCDE-FGHJK"), new[] { "sign", "presence" }, GrantState.Bound);
        var decoded = AetherNodeHandshake.DecodeAck(AetherNodeHandshake.Encode(ack));
        Assert.Equal(ack.ProtocolVersion, decoded.ProtocolVersion);
        Assert.Equal(ack.NodeTag, decoded.NodeTag);
        Assert.Equal(ack.Capabilities, decoded.Capabilities);
        Assert.Equal(ack.Grant, decoded.Grant);
    }

    [Fact]
    public void Ack_round_trips_an_empty_tag()
    {
        var ack = new AetherNodeHelloAck(1, default, System.Array.Empty<string>(), GrantState.AwaitingGrant);
        var decoded = AetherNodeHandshake.DecodeAck(AetherNodeHandshake.Encode(ack));
        Assert.False(decoded.NodeTag.IsValid);
        Assert.Equal(GrantState.AwaitingGrant, decoded.Grant);
    }

    [Fact]
    public void Negotiate_agrees_the_lower_version_and_the_mutual_capabilities()
    {
        var hello = new AetherNodeHello(3, new[] { "sign", "send", "unknown" });
        var ack = AetherNodeHandshake.Negotiate(
            hello,
            serverVersion: 1,
            serverCapabilities: new[] { "sign", "presence" },
            nodeTag: AetherNetTag.Parse("ABCDE-FGHJK"),
            grant: GrantState.Bound);

        Assert.Equal(1, ack.ProtocolVersion);
        Assert.Equal(new[] { "sign" }, ack.Capabilities);
        Assert.Equal(GrantState.Bound, ack.Grant);
    }

    [Fact]
    public void Negotiate_throws_a_typed_error_when_no_version_is_common()
    {
        var hello = new AetherNodeHello(0, System.Array.Empty<string>());
        var ex = Assert.Throws<AetherNodeException>(() =>
            AetherNodeHandshake.Negotiate(hello, 1, System.Array.Empty<string>(), default, GrantState.Absent));
        Assert.Equal(AetherNodeErrorCode.VersionUnsupported, ex.Code);
    }

    [Fact]
    public void Decode_rejects_a_truncated_buffer_with_a_typed_error()
    {
        var ex = Assert.Throws<AetherNodeException>(() => AetherNodeHandshake.DecodeHello(FromHex("0104")));
        Assert.Equal(AetherNodeErrorCode.Internal, ex.Code);
    }
}

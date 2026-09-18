// SPDX-License-Identifier: MIT

using System.Text.Json;
using AetherNet.Identity;
using Xunit;

namespace AetherNet.Node.Tests;

/// <summary>
/// Pins the handshake wire format against the cross-language fixture. Every language SDK asserts against
/// the same <c>node-fixtures.json</c>, so any drift in one implementation shows up here first.
/// </summary>
public class NodeFixtureTests
{
    private static string Hex(byte[] bytes) => System.Convert.ToHexString(bytes).ToLowerInvariant();

    private static string FixturePath()
    {
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "AetherNetProtocol.slnx")))
        {
            dir = dir.Parent;
        }
        Assert.True(dir is not null, "could not locate the repo root (AetherNetProtocol.slnx) above the test binaries");
        return System.IO.Path.Combine(dir!.FullName, "tests", "cross-language", "node-fixtures.json");
    }

    [Fact]
    public void Handshake_codec_matches_the_cross_language_fixture()
    {
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(FixturePath()));
        var root = doc.RootElement;

        foreach (var vector in root.GetProperty("hello").EnumerateArray())
        {
            var version = vector.GetProperty("protocolVersion").GetInt32();
            var caps = ReadStrings(vector.GetProperty("capabilities"));
            var hex = vector.GetProperty("hex").GetString()!;

            Assert.Equal(hex, Hex(AetherNodeHandshake.Encode(new AetherNodeHello(version, caps))));

            var decoded = AetherNodeHandshake.DecodeHello(System.Convert.FromHexString(hex));
            Assert.Equal(version, decoded.ProtocolVersion);
            Assert.Equal(caps, decoded.Capabilities);
        }

        foreach (var vector in root.GetProperty("ack").EnumerateArray())
        {
            var version = vector.GetProperty("protocolVersion").GetInt32();
            var tagStr = vector.GetProperty("nodeTag").GetString()!;
            var caps = ReadStrings(vector.GetProperty("capabilities"));
            var grant = System.Enum.Parse<GrantState>(vector.GetProperty("grant").GetString()!);
            var hex = vector.GetProperty("hex").GetString()!;

            var tag = string.IsNullOrEmpty(tagStr) ? default : AetherNetTag.Parse(tagStr);
            Assert.Equal(hex, Hex(AetherNodeHandshake.Encode(new AetherNodeHelloAck(version, tag, caps, grant))));

            var decoded = AetherNodeHandshake.DecodeAck(System.Convert.FromHexString(hex));
            Assert.Equal(version, decoded.ProtocolVersion);
            Assert.Equal(grant, decoded.Grant);
        }
    }

    private static string[] ReadStrings(JsonElement array)
    {
        var list = new System.Collections.Generic.List<string>();
        foreach (var item in array.EnumerateArray())
        {
            list.Add(item.GetString()!);
        }
        return list.ToArray();
    }
}

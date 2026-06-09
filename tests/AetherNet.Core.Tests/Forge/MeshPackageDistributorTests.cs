// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AetherNet.Content;
using AetherNet.Core.Tests.Fakes;
using AetherNet.Extensibility;
using AetherNet.Forge;
using AetherNet.Protocol;
using AetherNet.Routing;
using Xunit;

namespace AetherNet.Core.Tests.Forge;

public class MeshPackageDistributorTests
{
    private const string Local = "local-uhid";

    private static (MeshPackageDistributor sut, ContentService content, InMemoryForgeService forge, RecordingIncentiveProvider incentives) NewSut()
    {
        var sender = new FakeMeshSender(Local);
        var routing = new RoutingService(sender);
        var store = new InMemoryContentStore();
        var content = new ContentService(sender, routing, store);
        var forge = new InMemoryForgeService();
        var incentives = new RecordingIncentiveProvider();
        var sut = new MeshPackageDistributor(forge, content, incentives, Local);
        return (sut, content, forge, incentives);
    }

    [Fact]
    public async Task PublishAsync_StoresCacheEntry_AndContentMatches()
    {
        var (sut, _, forge, _) = NewSut();
        var payload = Encoding.UTF8.GetBytes("hello mesh");
        var packageId = "test:hello@1.0";

        var entry = await sut.PublishAsync(packageId, payload, "text/plain");

        Assert.Equal(packageId, entry.PackageId);
        Assert.Equal(payload.LongLength, entry.SizeBytes);
        Assert.NotNull(await forge.QueryAsync(packageId));
    }

    [Fact]
    public async Task TryFetchAsync_LocalCacheHit_ReturnsPayload()
    {
        var (sut, _, _, _) = NewSut();
        var payload = Encoding.UTF8.GetBytes("cached content");
        var packageId = "test:cached@1.0";

        await sut.PublishAsync(packageId, payload, "text/plain");
        var fetched = await sut.TryFetchAsync(packageId);

        Assert.NotNull(fetched);
        Assert.Equal(payload, fetched);
    }

    [Fact]
    public async Task TryFetchAsync_UnknownPackage_ReturnsNullAfterTimeout()
    {
        var (sut, _, _, _) = NewSut();
        // No publish — single-node mesh has nothing to find.
        var fetched = await sut.TryFetchAsync("test:unknown@1.0");
        Assert.Null(fetched);
    }

    [Fact]
    public async Task RecordChunkRelayAsync_DelegatesToIncentiveProvider()
    {
        var (sut, _, _, incentives) = NewSut();
        var packet = new MeshPacket
        {
            Type = PacketType.ChunkData,
            SourceUhid = "peer-a",
            DestinationUhid = "peer-b",
            Payload = new byte[] { 1, 2, 3 },
        };

        await sut.RecordChunkRelayAsync(packet);

        var relays = incentives.Relays.ToArray();
        Assert.Single(relays);
        Assert.Equal(Local, relays[0].RelayNodeUhid);
        Assert.Equal(PacketType.ChunkData, relays[0].Packet.Type);
    }

    [Fact]
    public void IntegrityHash_MatchesSha256()
    {
        var payload = Encoding.UTF8.GetBytes("integrity check");
        var hash = MeshPackageDistributor.IntegrityHash(payload);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(payload)), hash);
    }

    [Fact]
    public void IntegrityHash_NullPayload_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => MeshPackageDistributor.IntegrityHash(null!));
    }

    [Theory]
    [InlineData("MyTheme",        "skin:mytheme")]
    [InlineData("Dark Mode",      "skin:dark mode")]
    [InlineData("CamelCaseName",  "skin:camelcasename")]
    public void SkinPackageId_LowerCasesName(string input, string expected)
        => Assert.Equal(expected, MeshPackageDistributor.SkinPackageId(input));

    [Theory]
    [InlineData("milkdrop",       "preset1", "preset:milkdrop:preset1")]
    [InlineData("AVS",            "Effect",  "preset:avs:effect")]
    public void PresetPackageId_LowerCasesFamilyAndName(string family, string name, string expected)
        => Assert.Equal(expected, MeshPackageDistributor.PresetPackageId(family, name));

    [Theory]
    [InlineData("foo",            "1.0",     "plugin:foo@1.0")]
    [InlineData("MyPlugin",       "2.1.3",   "plugin:myplugin@2.1.3")]
    public void PluginPackageId_LowerCasesId_PreservesVersion(string id, string version, string expected)
        => Assert.Equal(expected, MeshPackageDistributor.PluginPackageId(id, version));

    [Fact]
    public void Constructor_NullForge_Throws()
    {
        var (_, content, _, incentives) = NewSut();
        Assert.Throws<ArgumentNullException>(() =>
            new MeshPackageDistributor(null!, content, incentives, Local));
    }

    [Fact]
    public void Constructor_NullContent_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new MeshPackageDistributor(new InMemoryForgeService(), null!, new RecordingIncentiveProvider(), Local));
    }

    [Fact]
    public void Constructor_NullIncentives_Throws()
    {
        var (_, content, _, _) = NewSut();
        Assert.Throws<ArgumentNullException>(() =>
            new MeshPackageDistributor(new InMemoryForgeService(), content, null!, Local));
    }

    [Fact]
    public void Constructor_EmptyLocalUhid_Throws()
    {
        var (_, content, _, incentives) = NewSut();
        Assert.Throws<ArgumentException>(() =>
            new MeshPackageDistributor(new InMemoryForgeService(), content, incentives, string.Empty));
    }

    /// <summary>
    /// In-memory incentive provider that records every relay so tests can
    /// assert call counts.
    /// </summary>
    private sealed class RecordingIncentiveProvider : IAetherNetIncentiveProvider
    {
        public sealed record RelayRecord(string RelayNodeUhid, MeshPacket Packet);
        public ConcurrentBag<RelayRecord> Relays { get; } = new();

        public Task RecordRelayAsync(string relayNodeUhid, MeshPacket packet, CancellationToken ct = default)
        {
            Relays.Add(new RelayRecord(relayNodeUhid, packet));
            return Task.CompletedTask;
        }
    }
}

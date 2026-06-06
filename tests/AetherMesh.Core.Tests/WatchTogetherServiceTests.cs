// SPDX-License-Identifier: MIT

using System.Text.Json;
using AetherMesh.Core.Tests.Fakes;
using AetherMesh.Protocol;
using AetherMesh.Streaming;
using AetherMesh.Streaming.Models;
using Xunit;

namespace AetherMesh.Core.Tests;

public class WatchTogetherServiceTests
{
    private const string Host = "host-uhid";
    private const string Follower = "follower-uhid";
    private const string Follower2 = "follower-2";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static (WatchTogetherService svc, FakeMeshSender sender, FakeRoutingService routing) NewService(string localUhid)
    {
        var sender = new FakeMeshSender(localUhid);
        var routing = new FakeRoutingService();
        var svc = new WatchTogetherService(sender, routing);
        return (svc, sender, routing);
    }

    private static MeshPacket Captured(FakeMeshSender sender, PacketType type, int skip = 0)
        => sender.Broadcasts.Where(p => p.Type == type).Skip(skip).First();

    // ─── Host lifecycle ───────────────────────────────────────────

    [Fact]
    public async Task HostAsync_CreatesHostStateAndBroadcastsJoin()
    {
        var (svc, sender, _) = NewService(Host);

        var session = await svc.HostAsync("root-hash-abc", "Movie Night");

        Assert.Equal(WatchState.Hosting, session.State);
        Assert.Equal(Host, session.HostUhid);
        Assert.Equal("root-hash-abc", session.ContentRootHash);
        Assert.Single(svc.GetActiveSessions());

        var join = Captured(sender, PacketType.WatchSync);
        var doc = JsonDocument.Parse(join.Payload);
        Assert.True(doc.RootElement.TryGetProperty("host_uhid", out _));
        var body = JsonSerializer.Deserialize<WatchJoinPayload>(join.Payload, JsonOptions)!;
        Assert.Equal(session.Id, body.SessionId);
        Assert.Equal("root-hash-abc", body.ContentRootHash);
    }

    [Fact]
    public async Task HostAsync_RejectsEmptyContentHash()
    {
        var (svc, _, _) = NewService(Host);

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            svc.HostAsync(string.Empty, "title"));
    }

    [Fact]
    public async Task HostAsync_RejectsEmptyTitle()
    {
        var (svc, _, _) = NewService(Host);

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            svc.HostAsync("root-hash", string.Empty));
    }

    // ─── Join announcement → follower discovers session ───────────

    [Fact]
    public async Task FollowerReceivesJoinAnnounce_RaisesSessionInvited()
    {
        var (hostSvc, hostSender, _) = NewService(Host);
        var (followerSvc, _, _) = NewService(Follower);

        var hosted = await hostSvc.HostAsync("root-hash", "Show");
        var join = Captured(hostSender, PacketType.WatchSync);

        WatchSession? invited = null;
        followerSvc.SessionInvited += (_, s) => invited = s;
        await followerSvc.HandleAsync(join);

        Assert.NotNull(invited);
        Assert.Equal(hosted.Id, invited!.Id);
        Assert.Equal(Host, invited.HostUhid);
        Assert.Equal(WatchState.Idle, invited.State);
    }

    [Fact]
    public async Task FollowAsync_TransitionsToFollowingState()
    {
        var (hostSvc, hostSender, _) = NewService(Host);
        var (followerSvc, _, _) = NewService(Follower);
        var hosted = await hostSvc.HostAsync("root-hash", "Show");
        await followerSvc.HandleAsync(Captured(hostSender, PacketType.WatchSync));

        await followerSvc.FollowAsync(hosted.Id);

        var session = followerSvc.GetActiveSessions().Single();
        Assert.Equal(WatchState.Following, session.State);
    }

    // ─── Play / Pause sync ───────────────────────────────────────

    [Fact]
    public async Task HostPlay_PropagatesToFollower_AndAppliesRttCompensatedPosition()
    {
        var (hostSvc, hostSender, _) = NewService(Host);
        var (followerSvc, _, _) = NewService(Follower);
        var hosted = await hostSvc.HostAsync("root-hash", "Show");
        await followerSvc.HandleAsync(Captured(hostSender, PacketType.WatchSync));
        await followerSvc.FollowAsync(hosted.Id);

        hostSender.Clear();
        await hostSvc.PlayAsync(hosted.Id, positionMs: 5_000);
        var play = Captured(hostSender, PacketType.WatchSync);

        WatchSession? applied = null;
        followerSvc.SyncApplied += (_, s) => applied = s;
        await followerSvc.HandleAsync(play);

        Assert.NotNull(applied);
        Assert.True(applied!.IsPlaying);
        // RTT compensation: position is bumped from 5000 by elapsed time on Play.
        // The exact delta depends on wall-clock between send and apply; just assert
        // the floor and that we applied the host's intent.
        Assert.True(applied.PositionMs >= 5_000);
        Assert.True(applied.PositionMs < 5_000 + 60_000); // less than 1 minute drift in test
    }

    [Fact]
    public async Task HostPause_PropagatesToFollower_WithoutRttCompensation()
    {
        var (hostSvc, hostSender, _) = NewService(Host);
        var (followerSvc, _, _) = NewService(Follower);
        var hosted = await hostSvc.HostAsync("root-hash", "Show");
        await followerSvc.HandleAsync(Captured(hostSender, PacketType.WatchSync));
        await followerSvc.FollowAsync(hosted.Id);

        hostSender.Clear();
        await hostSvc.PauseAsync(hosted.Id, positionMs: 7_500);
        var pause = Captured(hostSender, PacketType.WatchSync);

        WatchSession? applied = null;
        followerSvc.SyncApplied += (_, s) => applied = s;
        await followerSvc.HandleAsync(pause);

        Assert.NotNull(applied);
        Assert.False(applied!.IsPlaying);
        // Pause does NOT apply RTT compensation per the service spec.
        Assert.Equal(7_500, applied.PositionMs);
    }

    [Fact]
    public async Task HostSeek_PropagatesAuthoritativePositionToFollower()
    {
        var (hostSvc, hostSender, _) = NewService(Host);
        var (followerSvc, _, _) = NewService(Follower);
        var hosted = await hostSvc.HostAsync("root-hash", "Show");
        await followerSvc.HandleAsync(Captured(hostSender, PacketType.WatchSync));
        await followerSvc.FollowAsync(hosted.Id);

        hostSender.Clear();
        await hostSvc.SeekAsync(hosted.Id, positionMs: 60_000);
        var seek = Captured(hostSender, PacketType.WatchSync);

        WatchSession? applied = null;
        followerSvc.SyncApplied += (_, s) => applied = s;
        await followerSvc.HandleAsync(seek);

        Assert.NotNull(applied);
        Assert.Equal(60_000, applied!.PositionMs);
    }

    // ─── Reactions ───────────────────────────────────────────────

    [Fact]
    public async Task SendReactionAsync_BroadcastsAndRemoteRaisesReactionReceived()
    {
        var (hostSvc, hostSender, _) = NewService(Host);
        var (followerSvc, followerSender, _) = NewService(Follower);
        var hosted = await hostSvc.HostAsync("root-hash", "Show");
        await followerSvc.HandleAsync(Captured(hostSender, PacketType.WatchSync));
        await followerSvc.FollowAsync(hosted.Id);

        followerSender.Clear();
        await followerSvc.SendReactionAsync(hosted.Id, "love", positionMs: 12_345);

        var reaction = followerSender.Broadcasts.Single(p => p.Type == PacketType.WatchReaction);
        WatchReactionPayload? observed = null;
        hostSvc.ReactionReceived += (_, r) => observed = r;
        await hostSvc.HandleAsync(reaction);

        Assert.NotNull(observed);
        Assert.Equal(hosted.Id, observed!.SessionId);
        Assert.Equal("love", observed.Reaction);
        Assert.Equal(Follower, observed.SenderUhid);
        Assert.Equal(12_345, observed.PositionMs);
    }

    // ─── End / cleanup ───────────────────────────────────────────

    [Fact]
    public async Task EndAsync_OnHost_TransitionsStateAndRaisesSessionEnded()
    {
        var (svc, sender, _) = NewService(Host);
        var hosted = await svc.HostAsync("root-hash", "Show");
        sender.Clear();

        WatchSession? observed = null;
        svc.SessionEnded += (_, s) => observed = s;
        await svc.EndAsync(hosted.Id);

        Assert.NotNull(observed);
        Assert.Equal(WatchState.Ended, observed!.State);
        Assert.NotNull(observed.EndedAt);
        Assert.Empty(svc.GetActiveSessions());
    }

    [Fact]
    public async Task EndAsync_OnFollower_IsNoOp()
    {
        var (hostSvc, hostSender, _) = NewService(Host);
        var (followerSvc, followerSender, _) = NewService(Follower);
        var hosted = await hostSvc.HostAsync("root-hash", "Show");
        await followerSvc.HandleAsync(Captured(hostSender, PacketType.WatchSync));
        await followerSvc.FollowAsync(hosted.Id);
        followerSender.Clear();

        var raised = false;
        followerSvc.SessionEnded += (_, _) => raised = true;
        await followerSvc.EndAsync(hosted.Id);

        Assert.False(raised);
        Assert.Empty(followerSender.Broadcasts);
        // Follower remains in its session.
        Assert.Single(followerSvc.GetActiveSessions());
    }

    // ─── Security / robustness ───────────────────────────────────

    [Fact]
    public async Task HandleSync_DropsCommandFromNonHostSource()
    {
        var (svc, _, _) = NewService(Follower);

        // Inject a join from real host so follower learns the session.
        var sessionId = Guid.NewGuid();
        var joinPayload = JsonSerializer.SerializeToUtf8Bytes(new WatchJoinPayload
        {
            SessionId = sessionId,
            HostUhid = Host,
            ContentRootHash = "root-hash",
            Title = "Show",
            Mode = WatchMode.SharedFile,
        }, JsonOptions);
        await svc.HandleAsync(new MeshPacket
        {
            Type = PacketType.WatchSync,
            SourceUhid = Host,
            Payload = joinPayload,
        });
        await svc.FollowAsync(sessionId);

        // Now craft a sync command from a non-host source.
        var attackerCmd = JsonSerializer.SerializeToUtf8Bytes(new WatchSyncCommand
        {
            SessionId = sessionId,
            Kind = WatchSyncType.Seek,
            PositionMs = 999_999,
            PlaybackSpeed = 1.0,
            SentAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        }, JsonOptions);

        var raised = false;
        svc.SyncApplied += (_, _) => raised = true;
        await svc.HandleAsync(new MeshPacket
        {
            Type = PacketType.WatchSync,
            SourceUhid = "attacker", // NOT the host
            Payload = attackerCmd,
        });

        Assert.False(raised);
        // Position unchanged from initial 0.
        var session = svc.GetActiveSessions().Single();
        Assert.Equal(0, session.PositionMs);
    }

    [Fact]
    public async Task HostIgnoresOwnSyncCommand()
    {
        // The host must not double-apply its own broadcast.
        var (hostSvc, hostSender, _) = NewService(Host);
        var hosted = await hostSvc.HostAsync("root-hash", "Show");
        hostSender.Clear();

        await hostSvc.PlayAsync(hosted.Id, positionMs: 1_000);
        var play = hostSender.Broadcasts.Single(p =>
            p.Type == PacketType.WatchSync &&
            !JsonDocument.Parse(p.Payload).RootElement.TryGetProperty("host_uhid", out _));

        var raised = false;
        hostSvc.SyncApplied += (_, _) => raised = true;
        await hostSvc.HandleAsync(play);

        Assert.False(raised);
    }

    [Fact]
    public async Task HandleAsync_NonWatchPacketType_IsIgnored()
    {
        var (svc, _, _) = NewService(Host);
        var pkt = new MeshPacket
        {
            Type = PacketType.Data,
            SourceUhid = "other",
            Payload = new byte[] { 0 },
        };

        await svc.HandleAsync(pkt);

        Assert.Empty(svc.GetActiveSessions());
    }
}

// SPDX-License-Identifier: MIT

using AetherNet.Identity;
using Xunit;

namespace AetherNetNodeService.Host.Tests;

public class HelpHostTests
{
    private static readonly AetherNetTag Someone = AetherNetTag.FromPublicKey(new byte[32]);

    [Fact]
    public async Task ThePersonStartsItAndTheirAppSeesIt()
    {
        var help = new FakeHelp();
        var node = Node(help);

        Assert.False((await node.GetHelpAsync()).Mine.On);
        Assert.True(await node.StartHelpAsync(HelpKind.Help));
        Assert.Equal(HelpKind.Help, help.Started);

        var report = await node.GetHelpAsync();
        Assert.True(report.Mine.On);
        Assert.Equal(HelpKind.Help, report.Mine.Kind);
    }

    [Fact]
    public async Task ARefusalSaysWhyRatherThanPretending()
    {
        var help = new FakeHelp { CanSend = false, Why = "nobody chosen to ask yet" };
        var node = Node(help);

        Assert.False(await node.StartHelpAsync(HelpKind.Help));
        Assert.Equal("nobody chosen to ask yet", (await node.GetHelpAsync()).Mine.Why);
        Assert.False((await node.GetHelpAsync()).Mine.On);
    }

    [Fact]
    public async Task OnlyMarkingSafeEndsIt()
    {
        var help = new FakeHelp();
        var node = Node(help);
        await node.StartHelpAsync(HelpKind.Help);

        await node.MarkSafeAsync();
        var report = await node.GetHelpAsync();
        Assert.False(report.Mine.On);
        Assert.Equal(HelpKind.Safe, report.Mine.Kind);
        Assert.True(help.MarkedSafe);
    }

    [Fact]
    public async Task TheGuardiansAndTheOptionsReachTheNode()
    {
        var help = new FakeHelp();
        var node = Node(help);

        IReadOnlyList<HelpGuardian> chosen = [new HelpGuardian(Someone, "Sipho", HelpAlert.Quiet)];
        await node.SetHelpGuardiansAsync(chosen);
        Assert.Equal("Sipho", Assert.Single(help.Guardians).Name);
        Assert.Equal(HelpAlert.Quiet, help.Guardians[0].Alert);

        var triggers = new HelpTriggers(HelpTrigger.PowerButton, PowerPresses: 4);
        await node.SetHelpOptionsAsync(triggers, HelpAdvertForm.Registered16);
        Assert.Equal(triggers, help.Options);
        Assert.Equal(HelpAdvertForm.Registered16, help.Advert);

        await Assert.ThrowsAsync<ArgumentNullException>(() => node.SetHelpGuardiansAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => node.SetHelpOptionsAsync(null!, HelpAdvertForm.AetherNet128));
    }

    [Fact]
    public async Task AnAppIsToldWhenItChanges()
    {
        var help = new FakeHelp();
        var node = Node(help);
        var heard = new List<HelpReport>();
        using var subscription = node.Subscribe(new Recorder(heard));

        await node.StartHelpAsync(HelpKind.Walk);
        await node.MarkSafeAsync();

        Assert.Equal(2, heard.Count);
        Assert.Equal(HelpKind.Walk, heard[0].Mine.Kind);
        Assert.True(heard[0].Mine.On);
        Assert.Equal(HelpKind.Safe, heard[1].Mine.Kind);
        Assert.False(heard[1].Mine.On);
    }

    [Fact]
    public async Task AnAppStopsHearingOnceItLetsGo()
    {
        var help = new FakeHelp();
        var node = Node(help);
        var heard = new List<HelpReport>();
        var subscription = node.Subscribe(new Recorder(heard));
        subscription.Dispose();

        await node.StartHelpAsync(HelpKind.Help);
        Assert.Empty(heard);
    }

    [Fact]
    public async Task AServiceWithoutQuietHelpSaysSoRatherThanFailingQuietly()
    {
        var node = Node(help: null);

        Assert.Equal(HelpReport.None.Mine.On, (await node.GetHelpAsync()).Mine.On);
        Assert.Empty((await node.GetHelpAsync()).Watching);
        var thrown = await Assert.ThrowsAsync<AetherNodeException>(() => node.StartHelpAsync(HelpKind.Help));
        Assert.Equal(AetherNodeErrorCode.Internal, thrown.Code);
        await Assert.ThrowsAsync<AetherNodeException>(() => node.MarkSafeAsync());
        await Assert.ThrowsAsync<AetherNodeException>(() => node.SetHelpGuardiansAsync([]));
        await Assert.ThrowsAsync<AetherNodeException>(
            () => node.SetHelpOptionsAsync(new HelpTriggers(), HelpAdvertForm.AetherNet128));
    }

    private static AetherNodeService Node(INodeHelpSource? help) =>
        new(new FakeIdentity(), new FakeMessaging(), new FakeLink(), help: help);

    private sealed class FakeHelp : INodeHelpSource
    {
        private HelpReport _current = HelpReport.None;

        public bool CanSend { get; init; } = true;

        public string? Why { get; init; }

        public HelpKind? Started { get; private set; }

        public bool MarkedSafe { get; private set; }

        public IReadOnlyList<HelpGuardian> Guardians { get; private set; } = [];

        public HelpTriggers? Options { get; private set; }

        public HelpAdvertForm? Advert { get; private set; }

        public event Action? Changed;

        public HelpReport Current => _current;

        public bool Start(HelpKind kind)
        {
            if (!CanSend)
            {
                _current = new HelpReport { Mine = new HelpState { Why = Why } };
                Changed?.Invoke();
                return false;
            }

            Started = kind;
            _current = new HelpReport { Mine = new HelpState { On = true, Kind = kind } };
            Changed?.Invoke();
            return true;
        }

        public void MarkSafe()
        {
            MarkedSafe = true;
            _current = new HelpReport { Mine = new HelpState { On = false, Kind = HelpKind.Safe } };
            Changed?.Invoke();
        }

        public void SetGuardians(IReadOnlyList<HelpGuardian> guardians) => Guardians = guardians;

        public void SetOptions(HelpTriggers triggers, HelpAdvertForm advert)
        {
            Options = triggers;
            Advert = advert;
        }
    }

    private sealed class Recorder(List<HelpReport> heard) : IAetherNodeEvents
    {
        public void OnInbound(InboundMessage message) { }

        public void OnLinkChanged(NodeLinkStatus status) { }

        public void OnGrantChanged(GrantState state) { }

        public void OnHelpChanged(HelpReport report) => heard.Add(report);
    }

    private sealed class FakeIdentity : INodeIdentity
    {
        public ValueTask<AetherNetTag> GetOrMintAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Someone);

        public ValueTask<byte[]> GetPublicKeyAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new byte[32]);

        public ValueTask<byte[]> SignAsync(byte[] data, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new byte[64]);

        public ValueTask<byte[]> DeriveKeyAsync(string purpose, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new byte[32]);
    }

    private sealed class FakeMessaging : INodeMessaging
    {
        public event Action<InboundMessage>? Inbound;

        public Task<OutboundResult> SendAsync(AetherNetTag to, ReadOnlyMemory<byte> payload, Guid messageId, CancellationToken cancellationToken = default)
            => Task.FromResult(OutboundResult.Queued);

        public Task<IReadOnlyList<InboundMessage>> GetInboxAsync(int limit, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<InboundMessage>>([]);

        public void Unused() => Inbound?.Invoke(null!);
    }

    private sealed class FakeLink : INodeLinkSource
    {
        public event Action? Changed;

        public NodeLinkStatus Current => NodeLinkStatus.Offline;

        public void Unused() => Changed?.Invoke();
    }
}

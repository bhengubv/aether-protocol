// SPDX-License-Identifier: MIT

extern alias service;

using AetherNet.Sample.Shared.Pages;
using AetherNet.Sample.Tests.Fakes;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using service::AetherNet.Identity;
using service::AetherNetNodeService;
using service::AetherNetNodeService.Host;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// Aware's screen. A thin client: everything it shows comes from the node's one report, so these prove the screen
/// says what the node said — in a person's words, and with the thing that matters at the top.
/// </summary>
[Collection(ServiceInProcessCollection.Name)]
public class AwarePageTests : IDisposable
{
    private readonly TestContext _ctx = new();
    private readonly FakeNode _node = new();

    public AwarePageTests()
    {
        // AetherNetService's own Aware, over this test's node; the page reaches it through the menu, as on a phone.
        var service = new ServiceCollection();
        service.AddSingleton<IAetherNodeClient>(_node);
        service.AddSingleton(sp => new AwareService(sp.GetService<IAetherNodeClient>()));
        _ctx.Services.AddServiceInProcess(service);
    }

    private static DateTimeOffset Recently => DateTimeOffset.UtcNow.AddMinutes(-5);

    [Fact]
    public void WhatIsKeepingUpWithYouIsSaidFirstAndNamedForWhatItIs()
    {
        _node.Report = new AwareReport
        {
            On = true,
            Heard = 13,
            Named = 7,
            MovingWithYou = 1,
            Things =
            [
                new AwareThing
                {
                    Id = "ble:1",
                    Name = "Unknown tag",
                    What = "Apple AirTags",
                    Closeness = AwareCloseness.Here,
                    MovingWithYou = true,
                    FinderTag = true,
                    FirstHeard = Recently,
                    LastHeard = DateTimeOffset.UtcNow,
                },
                new AwareThing
                {
                    Id = "ble:2",
                    Name = "A pair of headphones",
                    Closeness = AwareCloseness.Near,
                    FirstHeard = Recently,
                    LastHeard = DateTimeOffset.UtcNow,
                },
            ],
        };

        var page = _ctx.RenderComponent<Shared.Pages.Aware>();

        // The alarming one, said first and in the words of somebody holding the phone.
        Assert.Contains("Moving with you", page.Markup);
        Assert.Contains("Unknown tag", page.Markup);
        Assert.Contains("Apple AirTags", page.Markup);
        Assert.Contains("kept up with you", page.Markup);
        Assert.Contains("in this room", page.Markup);

        // The rest is just what is around, and not shown twice.
        Assert.Contains("Around you", page.Markup);
        Assert.Contains("A pair of headphones", page.Markup);
        Assert.Contains("nearby", page.Markup);
        Assert.Equal(1, Occurrences(page.Markup, "Unknown tag"));

        // Counts in a person's words, never in decibels.
        Assert.Contains("13", page.Markup);
        Assert.DoesNotContain("dBm", page.Markup);
        Assert.DoesNotContain("RSSI", page.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WhenItIsNotListeningItSaysWhyAndWhatThatCosts()
    {
        _node.Report = new AwareReport
        {
            On = false,
            Why = "it needs permission to find devices nearby",
            Fixable = true,
        };

        var page = _ctx.RenderComponent<Shared.Pages.Aware>();

        Assert.Contains("Not listening", page.Markup);
        Assert.Contains("it needs permission to find devices nearby", page.Markup);

        // Consequences of on AND off, so a person can choose rather than only be told no.
        Assert.Contains("notices anything that follows you", page.Markup);
        Assert.Contains("hears nothing at all", page.Markup);
        Assert.Contains("carry on exactly as they are", page.Markup);
    }

    [Fact]
    public void WithNothingFollowingYouThereIsNoAlarmAtAll()
    {
        _node.Report = new AwareReport
        {
            On = true,
            Heard = 2,
            Things =
            [
                new AwareThing { Id = "ble:1", Name = "A fridge", Closeness = AwareCloseness.Near, LastHeard = DateTimeOffset.UtcNow },
                new AwareThing { Id = "ble:2", Name = "Gone thing", Closeness = AwareCloseness.Far, Gone = true, LastHeard = Recently },
            ],
        };

        var page = _ctx.RenderComponent<Shared.Pages.Aware>();

        Assert.DoesNotContain("Moving with you", page.Markup);
        Assert.Contains("A fridge", page.Markup);

        // Something that has left is still shown, so a person sees it go rather than wondering.
        Assert.Contains("Just left", page.Markup);
        Assert.Contains("Gone thing", page.Markup);
    }

    [Fact]
    public void AHeadWithNoNodeSaysSoRatherThanShowingAnEmptyRoom()
    {
        using var ctx = new TestContext();
        var service = new ServiceCollection();
        service.AddSingleton(_ => new AwareService(null));
        ctx.Services.AddServiceInProcess(service);

        var page = ctx.RenderComponent<Shared.Pages.Aware>();

        Assert.Contains("needs AetherNetService", page.Markup);
        Assert.DoesNotContain("Around you", page.Markup);
    }

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        var at = 0;
        while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += needle.Length;
        }

        return count;
    }

    public void Dispose()
    {
        _ctx.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class FakeNode : IAetherNodeClient
    {
        public AwareReport Report { get; set; } = AwareReport.None;

        public Task<AwareReport> GetAwareAsync(CancellationToken cancellationToken = default) => Task.FromResult(Report);

        public Task<OutboundResult> SendAsync(AetherNetTag to, ReadOnlyMemory<byte> payload, Guid messageId, CancellationToken cancellationToken = default)
            => Task.FromResult(OutboundResult.Sent);

        public IDisposable Subscribe(IAetherNodeEvents listener) => new Nothing();

        public Task<NodeLinkStatus> GetLinkAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(NodeLinkStatus.Offline);

        public Task<AetherNetTag> GetTagAsync(CancellationToken cancellationToken = default) => Task.FromResult(default(AetherNetTag));

        public Task<byte[]> GetPublicKeyAsync(CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());

        public Task<byte[]> SignAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());

        public Task<IReadOnlyList<InboundMessage>> GetInboxAsync(int limit = 50, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<InboundMessage>>(Array.Empty<InboundMessage>());

        private sealed class Nothing : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}

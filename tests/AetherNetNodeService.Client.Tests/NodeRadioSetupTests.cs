// SPDX-License-Identifier: MIT

using AetherNet.Identity;
using AetherNetNodeService.Client;
using Xunit;
using Mesh = AetherNet.Mesh;

namespace AetherNetNodeService.Client.Tests;

/// <summary>
/// The radios an app shows when they belong to AetherNetService rather than to the app. A phone was being told it was
/// not a phone, so these hold the line that what the node says is what the person is shown.
/// </summary>
public class NodeRadioSetupTests
{
    [Fact]
    public async Task APhoneIsNotToldItHasNoRadios()
    {
        var node = new FakeNode
        {
            Link = Link(
                new RadioStatus("Wi-Fi Direct", false, false, 0)
                {
                    Reason = "needs permission to find phones nearby",
                    Fixable = true,
                    NeedsPermission = true,
                },
                new RadioStatus("BLE", true, false, 1_000_000)),
        };
        var setup = new NodeRadioSetup(node, new FakeSettings());

        var radios = await setup.CheckAsync();

        Assert.True(setup.IsPhone);
        Assert.Equal(2, radios.Count);

        // The one waiting on a permission says so, and offers something to press.
        var wifi = radios.Single(r => r.Name == "Wi-Fi Direct");
        Assert.Equal(Mesh.RadioState.NeedsPermission, wifi.State);
        Assert.Equal("needs permission to find phones nearby", wifi.Detail);
        Assert.Equal("Allow", wifi.ActionLabel);

        // The working one is simply working — nothing to press.
        var ble = radios.Single(r => r.Name == "BLE");
        Assert.Equal(Mesh.RadioState.Ready, ble.State);
        Assert.Null(ble.ActionLabel);

        // Never the desktop answer, which is what a phone used to be given.
        Assert.DoesNotContain(radios, r => r.Detail.Contains("only exist on a phone", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PressingItOpensTheServicesOwnPageAndChecksAgain()
    {
        var node = new FakeNode
        {
            Link = Link(new RadioStatus("Wi-Fi Direct", false, false, 0)
            {
                Reason = "needs permission to find phones nearby",
                Fixable = true,
                NeedsPermission = true,
            }),
        };
        var settings = new FakeSettings();
        var setup = new NodeRadioSetup(node, settings);

        // Allowed on that page, the next check says so — the app asked again rather than remembering the refusal.
        node.OnAsked = () => node.Link = Link(new RadioStatus("Wi-Fi Direct", true, false, 1_000_000));
        var after = await setup.RequestAsync("Wi-Fi Direct");

        Assert.Equal(PermissionPage.AppInfo, settings.Opened);
        Assert.Equal(Mesh.RadioState.Ready, after.State);
    }

    [Fact]
    public async Task ASwitchedOffRadioIsNotSomethingToPressHere()
    {
        // The person switched it off in this app. That is theirs to undo here, not on the phone's settings page.
        var node = new FakeNode
        {
            Link = Link(new RadioStatus("BLE", false, false, 0) { On = false, Reason = "you switched it off" }),
        };
        var setup = new NodeRadioSetup(node, new FakeSettings());

        var ble = Assert.Single(await setup.CheckAsync());

        Assert.Equal(Mesh.RadioState.Unsupported, ble.State);
        Assert.Null(ble.ActionLabel);
        Assert.Equal("you switched it off", ble.Detail);
    }

    [Fact]
    public async Task AServiceThatIsNotAnsweringYetSaysNothingRatherThanNoRadios()
    {
        // The mistake this exists to undo: silence read as "this device has none".
        var setup = new NodeRadioSetup(new FakeNode { Throw = true });

        Assert.Empty(await setup.CheckAsync());
        Assert.False(setup.IsPhone);
    }

    private static NodeLinkStatus Link(params RadioStatus[] radios)
        => new(true, radios.FirstOrDefault()?.Name, radios);

    private sealed class FakeSettings : IAetherNetServiceSettings
    {
        public PermissionPage? Opened { get; private set; }

        public string PermissionName => "nearby devices";

        public bool Open()
        {
            Opened = PermissionPage.AppInfo;
            return true;
        }

        public bool Open(PermissionPage page)
        {
            Opened = page;
            return true;
        }
    }

    private sealed class FakeNode : IAetherNodeClient
    {
        public NodeLinkStatus Link = NodeLinkStatus.Offline;
        public bool Throw;
        public Action? OnAsked;

        public Task<NodeLinkStatus> GetLinkAsync(CancellationToken cancellationToken = default)
        {
            if (Throw)
            {
                throw new InvalidOperationException("the service is not answering");
            }

            // What the person did on the settings page happened before this check, so it is seen by this check.
            OnAsked?.Invoke();
            OnAsked = null;
            return Task.FromResult(Link);
        }

        public Task<OutboundResult> SendAsync(AetherNetTag to, ReadOnlyMemory<byte> payload, Guid messageId, CancellationToken cancellationToken = default)
            => Task.FromResult(OutboundResult.Sent);

        public IDisposable Subscribe(IAetherNodeEvents listener) => new Noop();

        public Task<AetherNetTag> GetTagAsync(CancellationToken cancellationToken = default) => Task.FromResult(default(AetherNetTag));

        public Task<byte[]> GetPublicKeyAsync(CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());

        public Task<byte[]> SignAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());

        public Task<IReadOnlyList<InboundMessage>> GetInboxAsync(int limit = 50, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<InboundMessage>>(Array.Empty<InboundMessage>());

        private sealed class Noop : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}

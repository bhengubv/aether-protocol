// SPDX-License-Identifier: MIT

using AetherNet.Transport.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using SIPSorcery.Net;
using Xunit;

namespace AetherNet.Transport.WebRtc.Tests;

/// <summary>
/// Verifies the DI extension wires the WebRTC transport into the container correctly — it surfaces in
/// the <see cref="ITransportService"/> enumerable that <c>TransportManager</c> draws on, and the
/// signalling helpers bind the expected implementation.
/// </summary>
public class WebRtcDiTests
{
    [Fact]
    public void AddWebRtcTransport_JoinsTheTransportLadder()
    {
        var services = new ServiceCollection();
        services.AddInMemoryWebRtcSignaling("node-1");
        services.AddWebRtcTransport("node-1", Array.Empty<RTCIceServer>());

        using var provider = services.BuildServiceProvider();

        var concrete = provider.GetService<WebRtcTransportService>();
        Assert.NotNull(concrete);
        Assert.NotNull(provider.GetService<IWebRtcSignaling>());

        var ladder = provider.GetServices<ITransportService>().ToList();
        var webrtc = Assert.Single(ladder, t => t is WebRtcTransportService);
        Assert.Equal("WebRTC P2P", webrtc.Name);
        Assert.Same(concrete, webrtc); // one shared singleton, surfaced through both shapes
    }

    [Fact]
    public void AddRelayWebRtcSignaling_BindsToTheNamedTransport()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new LoopbackTransport("relay"));
        services.AddRelayWebRtcSignaling<LoopbackTransport>();
        services.AddWebRtcTransport("node-1", Array.Empty<RTCIceServer>());

        using var provider = services.BuildServiceProvider();

        Assert.IsType<RelayWebRtcSignaling>(provider.GetService<IWebRtcSignaling>());
        Assert.NotNull(provider.GetService<WebRtcTransportService>());
    }
}

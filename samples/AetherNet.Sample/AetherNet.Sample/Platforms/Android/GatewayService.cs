// SPDX-License-Identifier: MIT
#if ANDROID
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using AetherNet.Sample.Shared.Services;
using AetherNet.Transport.Relay;
using Microsoft.Extensions.Logging;

namespace AetherNet.Sample.Platforms.Android;

/// <summary>
/// This phone putting its hand up: running the relay so the people in its Circle who have no data can
/// still reach each other.
///
/// <para>
/// It is opt-in and it stays opt-in. Carrying somebody else's traffic spends the volunteer's battery
/// and their data bundle, and in most of the world that bundle is the expensive part — so it is never
/// switched on quietly, and switching it off tells everyone rather than just going silent, which
/// would leave them pointed at a phone that has stopped answering.
/// </para>
/// </summary>
public sealed class GatewayService : IAsyncDisposable
{
    /// <summary>
    /// A high port, so no privilege is needed and nothing well-known is taken. It is only ever reached
    /// by phones a contact has handed the address to.
    /// </summary>
    public const int Port = 5200;

    private readonly ProxyDirectory _proxies;
    private readonly ChatService _chat;
    private readonly ILogger<GatewayService> _logger;
    private RelayServer? _server;

    public GatewayService(ProxyDirectory proxies, ChatService chat, ILogger<GatewayService>? logger = null)
    {
        _proxies = proxies ?? throw new ArgumentNullException(nameof(proxies));
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<GatewayService>.Instance;
    }

    /// <summary>Whether this phone is carrying traffic for others right now.</summary>
    public bool IsRunning => _server is { IsRunning: true };

    /// <summary>The address contacts were given, or null when not relaying.</summary>
    public string? Address { get; private set; }

    /// <summary>How many phones currently have something waiting here.</summary>
    public int Waiting => _server?.QueuedNodes ?? 0;

    /// <summary>
    /// Start carrying traffic, and tell the Circle where to find us.
    /// </summary>
    public async Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning) return true;

        var address = LocalAddress();
        if (address is null)
        {
            _logger.LogWarning("[Gateway] This phone has no address to offer");
            return false;
        }

        var server = new RelayServer(Port, null);
        if (!server.Start())
        {
            await server.DisposeAsync().ConfigureAwait(false);
            return false;
        }

        _server = server;
        Address = $"http://{address}:{Port}";
        _proxies.IsGateway = true;

        // Told inside each session, one contact at a time. There is no directory to publish to, and
        // that is the point — the address goes to people who already know us and nobody else.
        await _chat.OfferProxyToCircleAsync(Address, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("[Gateway] Relaying for the Circle at {Address}", Address);
        return true;
    }

    /// <summary>
    /// Stop carrying traffic, and say so. Going quiet instead would leave every contact pointing at a
    /// relay that no longer answers — which is indistinguishable, from their side, from the network
    /// being down.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _proxies.IsGateway = false;
        if (_server is null) return;

        await _chat.OfferProxyToCircleAsync(null, cancellationToken).ConfigureAwait(false);

        var server = _server;
        _server = null;
        Address = null;
        await server.DisposeAsync().ConfigureAwait(false);
        _logger.LogInformation("[Gateway] Stopped relaying");
    }

    /// <summary>
    /// Where this phone can be reached. Prefers a real routable address over the Wi-Fi Direct group's
    /// own subnet, because a relay that only works for phones already in the group is not a second leg
    /// — it is the first leg again, wearing a hat.
    /// </summary>
    private static string? LocalAddress()
    {
        try
        {
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                .Select(a => a.ToString())
                .ToArray();

            return candidates.FirstOrDefault(a => !a.StartsWith("192.168.49.", StringComparison.Ordinal))
                ?? candidates.FirstOrDefault();
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is null) return;
        var server = _server;
        _server = null;
        await server.DisposeAsync().ConfigureAwait(false);
    }
}
#endif

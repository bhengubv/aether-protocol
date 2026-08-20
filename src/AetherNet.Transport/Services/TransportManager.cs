// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using AetherNet.Transport.Abstractions;
using AetherNet.Transport.Models;
using AetherNet.Transport.NearLink;
using Microsoft.Extensions.Logging;

namespace AetherNet.Transport.Services;

/// <summary>
/// Multi-transport manager that routes packets through the best available transport.
///
/// Selection priority:
/// 1. NearLink — lowest power, highest range, 500+ peers, 20μs latency
/// 2. BLE — for small payloads (≤1KB), low power, ubiquitous
/// 3. Wi-Fi Direct — for large payloads, highest bandwidth
/// 4. CircleLink — extensible custom transport
/// 5. Additional transports — sorted by PowerCostRelative (ascending)
///
/// Falls through all available transports until one succeeds or all fail.
/// </summary>
public sealed class TransportManager : ITransportManager, IDisposable
{
    private const int BlePayloadThreshold = 1024; // 1KB

    private readonly IBleTransportService? _ble;
    private readonly ICircleLinkTransportService? _circleLink;
    private readonly IWifiDirectService? _wifiDirect;
    private readonly INearLinkTransportService? _nearLink;
    private readonly ITransportService[] _additionalTransports;
    private readonly ILogger<TransportManager> _logger;

    // Metrics tracking
    private long _bleSendCount;
    private long _bleBytesSent;
    private long _wifiDirectSendCount;
    private long _wifiDirectBytesSent;
    private long _nearLinkSendCount;
    private long _nearLinkBytesSent;
    private long _circleLinkSendCount;
    private long _circleLinkBytesSent;
    private long _additionalSendCount;
    private long _additionalBytesSent;
    private long _totalFailures;

    /// <inheritdoc />
    public event Action<string, byte[], string>? DataReceived;

    public TransportManager(
        ILogger<TransportManager> logger,
        IBleTransportService? ble = null,
        ICircleLinkTransportService? circleLink = null,
        IWifiDirectService? wifiDirect = null,
        INearLinkTransportService? nearLink = null,
        IEnumerable<ITransportService>? additionalTransports = null)
    {
        _logger = logger;
        _ble = ble;
        _circleLink = circleLink;
        _wifiDirect = wifiDirect;
        _nearLink = nearLink;

        // Filter out transports already handled by typed parameters to avoid double-routing
        var knownTypes = new HashSet<ITransportService>();
        if (_ble is not null) knownTypes.Add(_ble);
        if (_circleLink is not null) knownTypes.Add(_circleLink);
        if (_wifiDirect is not null) knownTypes.Add(_wifiDirect);

        _additionalTransports = (additionalTransports ?? [])
            .Where(t => !knownTypes.Contains(t))
            .OrderBy(t => t.PowerCostRelative)
            .ToArray();

        SubscribeToDataEvents();
    }

    /// <inheritdoc />
    public async Task<bool> SendAsync(string peerUhid, byte[] data, CancellationToken cancellationToken = default)
    {
        var dataLength = data.Length;

        // 1. NearLink — always preferred when available
        if (_nearLink is { IsAvailable: true })
        {
            if (await _nearLink.SendAsync(peerUhid, data, cancellationToken))
            {
                Interlocked.Increment(ref _nearLinkSendCount);
                Interlocked.Add(ref _nearLinkBytesSent, dataLength);
                _logger.LogDebug("Sent {Bytes} bytes to {Peer} via NearLink", dataLength, peerUhid);
                return true;
            }
        }

        // 2. BLE — preferred for small payloads (≤1KB)
        if (_ble is { IsAvailable: true } && dataLength <= BlePayloadThreshold)
        {
            if (await _ble.SendAsync(peerUhid, data, cancellationToken))
            {
                Interlocked.Increment(ref _bleSendCount);
                Interlocked.Add(ref _bleBytesSent, dataLength);
                _logger.LogDebug("Sent {Bytes} bytes to {Peer} via BLE", dataLength, peerUhid);
                return true;
            }
        }

        // 3. Wi-Fi Direct — preferred for larger payloads
        if (_wifiDirect is { IsAvailable: true })
        {
            if (await _wifiDirect.SendAsync(peerUhid, data, cancellationToken))
            {
                Interlocked.Increment(ref _wifiDirectSendCount);
                Interlocked.Add(ref _wifiDirectBytesSent, dataLength);
                _logger.LogDebug("Sent {Bytes} bytes to {Peer} via Wi-Fi Direct", dataLength, peerUhid);
                return true;
            }
        }

        // 4. CircleLink
        if (_circleLink is { IsAvailable: true })
        {
            if (await _circleLink.SendAsync(peerUhid, data, cancellationToken))
            {
                Interlocked.Increment(ref _circleLinkSendCount);
                Interlocked.Add(ref _circleLinkBytesSent, dataLength);
                _logger.LogDebug("Sent {Bytes} bytes to {Peer} via CircleLink", dataLength, peerUhid);
                return true;
            }
        }

        // 5. BLE fallback for large payloads (if NearLink and Wi-Fi Direct both failed)
        if (_ble is { IsAvailable: true } && dataLength > BlePayloadThreshold)
        {
            if (await _ble.SendAsync(peerUhid, data, cancellationToken))
            {
                Interlocked.Increment(ref _bleSendCount);
                Interlocked.Add(ref _bleBytesSent, dataLength);
                _logger.LogDebug("Sent {Bytes} bytes to {Peer} via BLE (fallback)", dataLength, peerUhid);
                return true;
            }
        }

        // 6. Additional transports (sorted by PowerCostRelative)
        foreach (var transport in _additionalTransports)
        {
            if (!transport.IsAvailable) continue;
            if (await transport.SendAsync(peerUhid, data, cancellationToken))
            {
                Interlocked.Increment(ref _additionalSendCount);
                Interlocked.Add(ref _additionalBytesSent, dataLength);
                _logger.LogDebug("Sent {Bytes} bytes to {Peer} via {Transport}", dataLength, peerUhid, transport.Name);
                return true;
            }
        }

        Interlocked.Increment(ref _totalFailures);
        _logger.LogWarning("Failed to send {Bytes} bytes to {Peer} — no transport available", dataLength, peerUhid);
        return false;
    }

    /// <inheritdoc />
    public async Task<bool> SendStreamAsync(string peerUhid, Stream stream, CancellationToken cancellationToken = default)
    {
        // 1. NearLink
        if (_nearLink is { IsAvailable: true })
        {
            if (await _nearLink.SendStreamAsync(peerUhid, stream, cancellationToken))
            {
                Interlocked.Increment(ref _nearLinkSendCount);
                _logger.LogDebug("Sent stream to {Peer} via NearLink", peerUhid);
                return true;
            }
            // Reset stream position for next attempt if possible
            if (stream.CanSeek) stream.Position = 0;
        }

        // 2. Wi-Fi Direct — best bandwidth for streams
        if (_wifiDirect is { IsAvailable: true })
        {
            if (await _wifiDirect.SendStreamAsync(peerUhid, stream, cancellationToken))
            {
                Interlocked.Increment(ref _wifiDirectSendCount);
                _logger.LogDebug("Sent stream to {Peer} via Wi-Fi Direct", peerUhid);
                return true;
            }
            if (stream.CanSeek) stream.Position = 0;
        }

        // 3. CircleLink
        if (_circleLink is { IsAvailable: true })
        {
            if (await _circleLink.SendStreamAsync(peerUhid, stream, cancellationToken))
            {
                Interlocked.Increment(ref _circleLinkSendCount);
                _logger.LogDebug("Sent stream to {Peer} via CircleLink", peerUhid);
                return true;
            }
            if (stream.CanSeek) stream.Position = 0;
        }

        // 4. BLE — last resort for streams (slow but functional)
        if (_ble is { IsAvailable: true })
        {
            if (await _ble.SendStreamAsync(peerUhid, stream, cancellationToken))
            {
                Interlocked.Increment(ref _bleSendCount);
                _logger.LogDebug("Sent stream to {Peer} via BLE", peerUhid);
                return true;
            }
            if (stream.CanSeek) stream.Position = 0;
        }

        // 5. Additional transports
        foreach (var transport in _additionalTransports)
        {
            if (!transport.IsAvailable) continue;
            if (await transport.SendStreamAsync(peerUhid, stream, cancellationToken))
            {
                Interlocked.Increment(ref _additionalSendCount);
                _logger.LogDebug("Sent stream to {Peer} via {Transport}", peerUhid, transport.Name);
                return true;
            }
            if (stream.CanSeek) stream.Position = 0;
        }

        Interlocked.Increment(ref _totalFailures);
        _logger.LogWarning("Failed to send stream to {Peer} — no transport available", peerUhid);
        return false;
    }

    /// <inheritdoc />
    public TransportMetrics GetMetrics() => new()
    {
        BleSendCount = Interlocked.Read(ref _bleSendCount),
        BleBytesSent = Interlocked.Read(ref _bleBytesSent),
        WifiDirectSendCount = Interlocked.Read(ref _wifiDirectSendCount),
        WifiDirectBytesSent = Interlocked.Read(ref _wifiDirectBytesSent),
        NearLinkSendCount = Interlocked.Read(ref _nearLinkSendCount),
        NearLinkBytesSent = Interlocked.Read(ref _nearLinkBytesSent),
        CircleLinkSendCount = Interlocked.Read(ref _circleLinkSendCount),
        CircleLinkBytesSent = Interlocked.Read(ref _circleLinkBytesSent),
        AdditionalSendCount = Interlocked.Read(ref _additionalSendCount),
        AdditionalBytesSent = Interlocked.Read(ref _additionalBytesSent),
        TotalFailures = Interlocked.Read(ref _totalFailures)
    };

    private void SubscribeToDataEvents()
    {
        if (_ble is not null)
            _ble.DataReceived += (sender, data) => DataReceived?.Invoke(sender, data, "BLE");

        if (_wifiDirect is not null)
            _wifiDirect.DataReceived += (sender, data) => DataReceived?.Invoke(sender, data, "Wi-Fi Direct");

        if (_nearLink is not null)
            _nearLink.DataReceived += (sender, data) => DataReceived?.Invoke(sender, data, "NearLink");

        if (_circleLink is not null)
            _circleLink.DataReceived += (sender, data) => DataReceived?.Invoke(sender, data, "CircleLink");

        foreach (var transport in _additionalTransports)
            transport.DataReceived += (sender, data) => DataReceived?.Invoke(sender, data, transport.Name);
    }

    public void Dispose()
    {
        // Unsubscribe is not strictly necessary since we hold the only references,
        // but we clear the event to prevent any further invocations.
        DataReceived = null;
    }
}

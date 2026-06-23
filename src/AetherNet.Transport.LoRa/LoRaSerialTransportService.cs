// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Ports;
using System.Text;
using AetherNet.Transport.Abstractions;
using Microsoft.Extensions.Logging;

namespace AetherNet.Transport.LoRa;

/// <summary>
/// Real LoRa (Aether Red / CircleLink) transport over a serial-attached LoRa module that speaks the
/// RYLR-class AT command set (Reyax RYLR896/RYLR998 and compatibles) on an SX127x/SX126x radio. This
/// is the "hardware adopted" path that <c>LoRaCircleLinkStub</c> documented: a real driver — it opens
/// the serial port, configures the radio, sends with <c>AT+SEND</c> and surfaces inbound <c>+RCV</c>
/// frames.
///
/// <para><b>Verification status:</b> this is genuinely implemented and it compiles, but it is
/// <b>runtime-UNVERIFIED</b> — it has not been exercised against a physical module (none on this build
/// machine). On-radio bring-up (two modules exchanging a frame) is the open verification step.
/// <see cref="IsAvailable"/> reflects whether the configured serial port actually opened.</para>
///
/// <para>The payload is the raw AetherNet packet, hex-encoded so it survives the AT text protocol.
/// LoRa is a connectionless broadcast-flood medium; address 0 is broadcast, and a registered peer
/// maps to its numeric LoRa node address via <see cref="RegisterPeer"/>. The AetherNet packet carries
/// its own end-to-end routing on top.</para>
/// </summary>
public sealed class LoRaSerialTransportService : ICircleLinkTransportService, IDisposable
{
    private readonly LoRaSerialOptions _options;
    private readonly ILogger<LoRaSerialTransportService>? _logger;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, ushort> _peerAddresses = new(StringComparer.Ordinal);
    private SerialPort? _port;
    private volatile bool _available;

    public LoRaSerialTransportService(LoRaSerialOptions options, ILogger<LoRaSerialTransportService>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    // ── ITransportService ───────────────────────────────────────────────────────
    public string Name => "Aether Red (LoRa/CircleLink)";
    public bool IsAvailable => _available;
    public long MaxBandwidthBps => 37_500;   // SF7 / BW125 kHz ≈ 37.5 kbps
    public int MaxRangeMeters => 15_000;     // up to ~15 km line-of-sight
    public int PowerCostRelative => 50;      // high TX power — chosen only when closer transports fail
    public int MaxConcurrentPeers => 255;

    public event Action<string, byte[]>? DataReceived;
    public event Action<string>? PeerConnected;
    public event Action<string>? PeerDisconnected;

    /// <summary>Opens the serial port and configures the radio. Sets <see cref="IsAvailable"/> on success.</summary>
    public bool Open()
    {
        lock (_gate)
        {
            if (_available) return true;
            try
            {
                var port = new SerialPort(_options.PortName, _options.BaudRate, Parity.None, 8, StopBits.One)
                {
                    NewLine = "\r\n",
                    ReadTimeout = 2000,
                    WriteTimeout = 2000,
                    Encoding = Encoding.ASCII,
                };
                port.Open();
                _port = port;
                Configure(port);
                port.DataReceived += OnSerialData;
                _available = true;
                _logger?.LogInformation(
                    "[LoRa] open on {Port} @ {Baud}, addr={Addr} net={Net} band={Band}Hz SF{SF}",
                    _options.PortName, _options.BaudRate, _options.Address, _options.NetworkId,
                    _options.BandHz, _options.SpreadingFactor);
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[LoRa] failed to open {Port}", _options.PortName);
                _available = false;
                return false;
            }
        }
    }

    private void Configure(SerialPort port)
    {
        SendAt(port, $"AT+ADDRESS={_options.Address}");
        SendAt(port, $"AT+NETWORKID={_options.NetworkId}");
        SendAt(port, $"AT+BAND={_options.BandHz}");
        SendAt(port,
            $"AT+PARAMETER={_options.SpreadingFactor},{_options.BandwidthIndex},{_options.CodingRate},{_options.PreambleLength}");
    }

    private static void SendAt(SerialPort port, string command)
    {
        port.WriteLine(command);
        try { _ = port.ReadLine(); } catch (TimeoutException) { /* tolerate a slow/echo-less module */ }
    }

    public Task<bool> SendAsync(string peerUhid, byte[] data, CancellationToken cancellationToken = default)
    {
        if (!_available || data is null || data.Length == 0) return Task.FromResult(false);
        var port = _port;
        if (port is null) return Task.FromResult(false);

        ushort address = peerUhid is not null && _peerAddresses.TryGetValue(peerUhid, out var mapped)
            ? mapped
            : (ushort)0; // 0 = broadcast (managed-flood mesh)
        var hex = Convert.ToHexString(data);
        try
        {
            lock (_gate)
            {
                port.WriteLine($"AT+SEND={address},{hex.Length},{hex}");
            }
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[LoRa] send to {Peer} failed", peerUhid);
            return Task.FromResult(false);
        }
    }

    public async Task<bool> SendStreamAsync(string peerUhid, Stream stream, CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        return await SendAsync(peerUhid, ms.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    public bool IsConnected(string peerUhid) => _available; // connectionless broadcast medium

    /// <summary>Maps an AetherNet peer UHID to a numeric LoRa node address (1–65535) for directed sends.</summary>
    public void RegisterPeer(string peerUhid, ushort address)
    {
        if (string.IsNullOrEmpty(peerUhid)) return;
        var isNew = !_peerAddresses.ContainsKey(peerUhid);
        _peerAddresses[peerUhid] = address;
        if (isNew) PeerConnected?.Invoke(peerUhid);
    }

    private void OnSerialData(object? sender, SerialDataReceivedEventArgs e)
    {
        var port = _port;
        if (port is null) return;
        try
        {
            while (port.IsOpen && port.BytesToRead > 0)
            {
                HandleLine(port.ReadLine().Trim());
            }
        }
        catch (TimeoutException) { /* partial line — wait for the next event */ }
        catch (Exception ex) { _logger?.LogDebug(ex, "[LoRa] read loop error"); }
    }

    private void HandleLine(string line)
    {
        // RYLR inbound frame: +RCV=<address>,<length>,<hexdata>,<rssi>,<snr>
        if (!line.StartsWith("+RCV=", StringComparison.Ordinal)) return;
        var parts = line.Substring(5).Split(',');
        if (parts.Length < 3) return;
        if (!ushort.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var address)) return;
        byte[] data;
        try { data = Convert.FromHexString(parts[2]); }
        catch (FormatException) { return; }
        DataReceived?.Invoke(address.ToString(CultureInfo.InvariantCulture), data);
    }

    public void Dispose()
    {
        string[] peers;
        lock (_gate)
        {
            _available = false;
            peers = _peerAddresses.Keys.ToArray();
            _peerAddresses.Clear();
            if (_port is not null)
            {
                try { _port.DataReceived -= OnSerialData; } catch { /* best effort */ }
                try { _port.Close(); } catch { /* best effort */ }
                try { _port.Dispose(); } catch { /* best effort */ }
                _port = null;
            }
        }
        foreach (var peer in peers) PeerDisconnected?.Invoke(peer); // registered peers are now unreachable
        DataReceived = null;
        PeerConnected = null;
        PeerDisconnected = null;
    }
}

/// <summary>Configuration for a RYLR-class serial LoRa module.</summary>
public sealed class LoRaSerialOptions
{
    /// <summary>Serial port the module is attached to, e.g. <c>"COM5"</c> or <c>"/dev/ttyUSB0"</c>.</summary>
    public required string PortName { get; init; }

    /// <summary>Serial baud rate (RYLR default 115200).</summary>
    public int BaudRate { get; init; } = 115200;

    /// <summary>This node's LoRa address (1–65535).</summary>
    public ushort Address { get; init; } = 1;

    /// <summary>RYLR network id — only modules on the same network id hear each other.</summary>
    public int NetworkId { get; init; } = 18;

    /// <summary>Carrier frequency in Hz. EU868 = 868_500_000; US915 = 915_000_000.</summary>
    public long BandHz { get; init; } = 868_500_000;

    /// <summary>Spreading factor (7–12). Higher = longer range, lower rate.</summary>
    public int SpreadingFactor { get; init; } = 9;

    /// <summary>RYLR bandwidth index (7 = 125 kHz, 8 = 250 kHz, 9 = 500 kHz).</summary>
    public int BandwidthIndex { get; init; } = 7;

    /// <summary>Coding rate (1 = 4/5 … 4 = 4/8).</summary>
    public int CodingRate { get; init; } = 1;

    /// <summary>Preamble length.</summary>
    public int PreambleLength { get; init; } = 12;
}

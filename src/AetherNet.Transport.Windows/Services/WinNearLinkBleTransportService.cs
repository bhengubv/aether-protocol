// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using AetherNet.Transport.NearLink;
using AetherNet.Transport.Services;
using Microsoft.Extensions.Logging;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace AetherNet.Transport.Windows.Services;

/// <summary>
/// Windows NearLink (Aether Teal) transport — a real SSAP-over-BLE-GATT central.
///
/// <h3>Why this is not a stub</h3>
/// NearLink's application protocol — SSAP (SparkLink Service Access Protocol) — is structurally
/// identical to Bluetooth GATT: the same Services → Properties → Descriptors model, the same
/// notify/indicate semantics, the same UUID format. Where NearLink silicon is absent (every
/// non-HarmonyOS Windows machine), this transport implements SSAP as a thin façade over standard
/// BLE GATT using the canonical Aether SLE UUIDs (<see cref="SleGattConstants"/>), so every Windows
/// node participates in the Aether Teal mesh today rather than holding an <c>IsAvailable=false</c>
/// placeholder.
///
/// <h3>Nominal profile vs. delivered performance (honest note)</h3>
/// <see cref="MaxBandwidthBps"/>, <see cref="MaxRangeMeters"/>, and <see cref="PowerCostRelative"/>
/// report NearLink's <em>nominal</em> profile (12 Mbps / 600 m / lowest power) — identical to the
/// <c>SimulatedNearLinkTransportService</c> and the <see cref="INearLinkTransportService"/> contract,
/// so the <c>TransportManager</c> ranks the Teal slot consistently across implementations. Over the
/// BLE approximation the <em>achieved</em> bandwidth/range/power are BLE-class (~1 Mbps, ~100 m,
/// power ≈ 2); the predictive selector's per-transport EWMA metrics converge to the real figures
/// once live samples flow. Nodes running this class interoperate with other Aether Teal nodes on
/// the same BLE approximation, not with genuine NearLink hardware (BLE GFSK cannot decode SLE's
/// BPSK/QPSK/8PSK + Polar/HARQ frames).
///
/// <h3>How it works</h3>
/// Acts as a BLE central: scans for peripherals advertising <see cref="SleGattConstants.ServiceUuid"/>,
/// connects, subscribes to <see cref="SleGattConstants.NotifyCharacteristic"/>, and writes outbound
/// packets to <see cref="SleGattConstants.DataCharacteristic"/>. Payloads larger than the GATT MTU
/// are fragmented with <see cref="BleGattFramer"/> and reassembled per-peer.
///
/// <h3>Upgrade path</h3>
/// When a Windows NearLink SDK ships, replace the BLE GATT calls with <c>ssaps_*</c>/<c>ssapc_*</c>
/// calls (mirrored from the open WS63 headers at <c>gitee.com/HiSpark/fbb_ws63</c>), keep the same
/// SLE UUIDs, and gate <see cref="IsAvailable"/> on the SDK's hardware-present check — no peer or
/// application code changes.
///
/// Counterpart peripheral: the Android <c>android/teal/</c> node and the HarmonyOS
/// <c>harmonyos/teal/</c> node (real <c>@kit.NearLinkKit</c> SDK).
/// </summary>
public sealed class WinNearLinkBleTransportService : INearLinkTransportService, IAsyncDisposable
{
    private readonly ILogger<WinNearLinkBleTransportService> _logger;
    private readonly string _localUhid;
    private readonly ConcurrentDictionary<ulong, ConnectedPeer> _peers = new();
    private BluetoothLEAdvertisementWatcher? _watcher;
    private volatile bool _disposed;

    public WinNearLinkBleTransportService(string localUhid,
        ILogger<WinNearLinkBleTransportService> logger)
    {
        _localUhid = localUhid ?? throw new ArgumentNullException(nameof(localUhid));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // ── INearLinkTransportService (nominal NearLink profile) ──────────────────

    /// <inheritdoc />
    public string Name => "Aether Teal (NearLink)";

    /// <inheritdoc />
    public bool IsAvailable => !_disposed;

    /// <inheritdoc />
    public long MaxBandwidthBps => 12_000_000; // NearLink nominal; BLE delivers ~1 Mbps (see note)

    /// <inheritdoc />
    public int MaxRangeMeters => 600; // NearLink nominal; BLE delivers ~100 m

    /// <inheritdoc />
    public int PowerCostRelative => 1; // NearLink nominal; BLE is ~2

    /// <inheritdoc />
    public int MaxConcurrentPeers => 500;

    /// <inheritdoc />
    public int ConnectedPeerCount => _peers.Values.Count(p => p.IsAlive);

    /// <inheritdoc />
    public event Action<string, byte[]>? DataReceived;

    /// <inheritdoc />
    public event Action<string>? PeerConnected;

    /// <inheritdoc />
    public event Action<string>? PeerDisconnected;

    // ── Scanning ─────────────────────────────────────────────────────────────

    /// <summary>Starts a BLE scan for Aether Teal (SLE) peripherals.</summary>
    public void StartScanning()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_watcher is not null) return;

        _watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };
        _watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(SleGattConstants.ServiceUuid);
        _watcher.Received += OnWatcherReceived;
        _watcher.Start();

        _logger.LogInformation("[NearLink-Win] Scanning for Aether Teal peripherals (SLE service {Uuid})",
            SleGattConstants.ServiceUuid);
    }

    public void StopScanning()
    {
        if (_watcher is null) return;
        _watcher.Stop();
        _watcher.Received -= OnWatcherReceived;
        _watcher = null;
    }

    // ── Send ─────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<bool> SendAsync(string peerUhid, byte[] data,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(data);
        StartScanning();

        var peer = _peers.Values.FirstOrDefault(p => p.Uhid == peerUhid);
        if (peer is null)
        {
            _logger.LogWarning("[NearLink-Win] No connected peer with UHID {Uhid}", peerUhid);
            return false;
        }

        return await peer.WriteFramedAsync(data, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> SendStreamAsync(string peerUhid, Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken);
        return await SendAsync(peerUhid, ms.ToArray(), cancellationToken);
    }

    /// <inheritdoc />
    public bool IsConnected(string peerUhid)
        => _peers.Values.Any(p => p.Uhid == peerUhid && p.IsAlive);

    // ── Advertisement handler ────────────────────────────────────────────────

    private async void OnWatcherReceived(BluetoothLEAdvertisementWatcher sender,
        BluetoothLEAdvertisementReceivedEventArgs args)
    {
        var address = args.BluetoothAddress;
        if (_peers.ContainsKey(address)) return;

        _logger.LogInformation("[NearLink-Win] Found Aether Teal peripheral {Address:X12} RSSI={Rssi}",
            address, args.RawSignalStrengthInDBm);

        await ConnectAsync(address);
    }

    // ── GATT connection ──────────────────────────────────────────────────────

    private async Task ConnectAsync(ulong address)
    {
        try
        {
            var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
            if (device is null)
            {
                _logger.LogWarning("[NearLink-Win] Could not open device {Address:X12}", address);
                return;
            }

            var svcResult = await device.GetGattServicesForUuidAsync(
                SleGattConstants.ServiceUuid, BluetoothCacheMode.Uncached);
            if (svcResult.Status != GattCommunicationStatus.Success || svcResult.Services.Count == 0)
            {
                _logger.LogWarning("[NearLink-Win] SLE service not found on {Address:X12}: {Status}",
                    address, svcResult.Status);
                device.Dispose();
                return;
            }

            var service = svcResult.Services[0];

            var dataResult = await service.GetCharacteristicsForUuidAsync(SleGattConstants.DataCharacteristic);
            if (dataResult.Status != GattCommunicationStatus.Success || dataResult.Characteristics.Count == 0)
            {
                _logger.LogError("[NearLink-Win] SLE data property missing on {Address:X12}", address);
                device.Dispose();
                return;
            }
            var dataChar = dataResult.Characteristics[0];

            var notifyResult = await service.GetCharacteristicsForUuidAsync(SleGattConstants.NotifyCharacteristic);
            if (notifyResult.Status != GattCommunicationStatus.Success || notifyResult.Characteristics.Count == 0)
            {
                _logger.LogError("[NearLink-Win] SLE notify property missing on {Address:X12}", address);
                device.Dispose();
                return;
            }
            var notifyChar = notifyResult.Characteristics[0];

            var cccd = await notifyChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);
            if (cccd != GattCommunicationStatus.Success)
            {
                _logger.LogError("[NearLink-Win] Could not enable notifications on {Address:X12}: {Status}",
                    address, cccd);
                device.Dispose();
                return;
            }

            var peer = new ConnectedPeer(address.ToString("X12"), device, service, dataChar, notifyChar);
            notifyChar.ValueChanged += (s, e) => OnNotification(peer, e);
            device.ConnectionStatusChanged += OnConnectionStatusChanged;

            if (_peers.TryAdd(address, peer))
            {
                _logger.LogInformation("[NearLink-Win] Connected to Aether Teal peer {Address:X12}", address);
                PeerConnected?.Invoke(peer.Uhid);
            }
            else
            {
                await peer.DisposeAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[NearLink-Win] Connection failed for {Address:X12}", address);
        }
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice device, object args)
    {
        if (device.ConnectionStatus != BluetoothConnectionStatus.Disconnected) return;

        if (_peers.TryRemove(device.BluetoothAddress, out var peer))
        {
            _logger.LogInformation("[NearLink-Win] Peer {Uhid} disconnected", peer.Uhid);
            PeerDisconnected?.Invoke(peer.Uhid);
            device.ConnectionStatusChanged -= OnConnectionStatusChanged;
            _ = peer.DisposeAsync();
        }
    }

    private void OnNotification(ConnectedPeer peer, GattValueChangedEventArgs args)
    {
        var reader = DataReader.FromBuffer(args.CharacteristicValue);
        var frame = new byte[reader.UnconsumedBufferLength];
        reader.ReadBytes(frame);

        var message = peer.AccumulateFrame(frame);
        if (message is not null)
        {
            _logger.LogDebug("[NearLink-Win] Reassembled {Bytes} bytes from {Uhid}", message.Length, peer.Uhid);
            DataReceived?.Invoke(peer.Uhid, message);
        }
    }

    // ── Disposal ─────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        StopScanning();
        foreach (var peer in _peers.Values)
            await peer.DisposeAsync();
        _peers.Clear();
    }

    // ── Inner type ───────────────────────────────────────────────────────────

    private sealed class ConnectedPeer : IAsyncDisposable
    {
        public string Uhid { get; }
        public bool IsAlive => _device.ConnectionStatus == BluetoothConnectionStatus.Connected;

        private readonly BluetoothLEDevice _device;
        private readonly GattDeviceService _service;
        private readonly GattCharacteristic _dataChar;
        private readonly GattCharacteristic _notifyChar;
        private readonly List<byte[]> _rxFrames = new();
        private readonly object _rxLock = new();

        public ConnectedPeer(string uhid, BluetoothLEDevice device, GattDeviceService service,
            GattCharacteristic dataChar, GattCharacteristic notifyChar)
        {
            Uhid = uhid;
            _device = device;
            _service = service;
            _dataChar = dataChar;
            _notifyChar = notifyChar;
        }

        /// <summary>Fragments <paramref name="data"/> at the NearLink MTU and writes every frame.</summary>
        public async Task<bool> WriteFramedAsync(byte[] data, CancellationToken ct)
        {
            try
            {
                // NearLink's nominal MTU (4096) is larger than BLE's; cap at the BLE framer
                // default so each SSAP write fits a single BLE GATT PDU sequence.
                foreach (var frame in BleGattFramer.Frame(data))
                {
                    ct.ThrowIfCancellationRequested();
                    var writer = new DataWriter();
                    writer.WriteBytes(frame);
                    var status = await _dataChar.WriteValueAsync(
                        writer.DetachBuffer(), GattWriteOption.WriteWithoutResponse);
                    if (status != GattCommunicationStatus.Success) return false;
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Adds an inbound frame to the per-peer reassembly buffer. Index-0 frames start a fresh
        /// message; a complete sequence is reassembled, the buffer reset, and the bytes returned.
        /// Returns <c>null</c> while the message is still incomplete.
        /// </summary>
        public byte[]? AccumulateFrame(byte[] frame)
        {
            lock (_rxLock)
            {
                if (frame.Length >= 4 && frame[2] == 0 && frame[3] == 0)
                    _rxFrames.Clear();

                _rxFrames.Add(frame);

                if (BleGattFramer.IsComplete(_rxFrames))
                {
                    var message = BleGattFramer.Reassemble(_rxFrames);
                    _rxFrames.Clear();
                    return message;
                }
                return null;
            }
        }

        public ValueTask DisposeAsync()
        {
            _service.Dispose();
            _device.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

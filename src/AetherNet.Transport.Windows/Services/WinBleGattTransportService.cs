// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using AetherNet.Transport.Abstractions;
using AetherNet.Transport.Models;
using Microsoft.Extensions.Logging;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace AetherNet.Transport.Windows.Services;

/// <summary>
/// Windows BLE GATT central transport for the Aether mesh protocol.
///
/// Acts as a BLE central (client): scans for peripherals advertising
/// <see cref="BleGattConstants.ServiceUuid"/>, connects, subscribes to
/// RX notifications, and writes Aether wire-format packets to the TX
/// characteristic.
///
/// Counterpart: <c>AetherNetGattServer</c> in the Android <c>ble-node</c> app.
/// </summary>
public sealed class WinBleGattTransportService : IBleTransportService, IAsyncDisposable
{
    private readonly ILogger<WinBleGattTransportService> _logger;
    private readonly string _localUhid;
    private readonly ConcurrentDictionary<ulong, ConnectedPeer> _peers = new();
    private BluetoothLEAdvertisementWatcher? _watcher;
    private volatile bool _disposed;

    public WinBleGattTransportService(string localUhid,
        ILogger<WinBleGattTransportService> logger)
    {
        _localUhid = localUhid ?? throw new ArgumentNullException(nameof(localUhid));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // ── ITransportService ────────────────────────────────────────────────────

    public string Name => "BLE-GATT-Win";
    public bool IsAvailable => !_disposed;
    public long MaxBandwidthBps => 2_000_000;
    public int MaxRangeMeters => 100;
    public int PowerCostRelative => 2;
    public int MaxConcurrentPeers => 7;

    public event Action<string, byte[]>? DataReceived;
    public event Action<BleAdvertisement>? AdvertisementReceived;

    // ── Scanning ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts BLE scanning for Aether peripherals.
    /// Automatically called by <see cref="SendAdvertisementAsync"/> and
    /// <see cref="SendAsync"/> if not already running.
    /// </summary>
    public void StartScanning()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_watcher is not null) return;

        _watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };
        _watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(BleGattConstants.ServiceUuid);
        _watcher.Received += OnWatcherReceived;
        _watcher.Start();

        _logger.LogInformation("[BLE-Win] Scanning for Aether peripherals (service {Uuid})",
            BleGattConstants.ServiceUuid);
    }

    public void StopScanning()
    {
        if (_watcher is null) return;
        _watcher.Stop();
        _watcher.Received -= OnWatcherReceived;
        _watcher = null;
    }

    // ── IBleTransportService ─────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<bool> SendAdvertisementAsync(BleAdvertisement advertisement,
        CancellationToken cancellationToken = default)
    {
        // Windows central role: we don't advertise, we scan.
        // This method is a no-op for the central; peripheral advertising
        // happens on the Android side.
        StartScanning();
        return Task.FromResult(true);
    }

    // ── ITransportService ────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<bool> SendAsync(string peerUhid, byte[] data,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        StartScanning();

        // Find an already-connected peer by UHID.
        var peer = _peers.Values.FirstOrDefault(p => p.Uhid == peerUhid);
        if (peer is null)
        {
            _logger.LogWarning("[BLE-Win] No connected peer with UHID {Uhid}", peerUhid);
            return false;
        }

        return await peer.WriteAsync(data, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> SendStreamAsync(string peerUhid, Stream stream,
        CancellationToken cancellationToken = default)
    {
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

        // Already connected to this device?
        if (_peers.ContainsKey(address)) return;

        _logger.LogInformation("[BLE-Win] Found Aether peripheral at {Address:X12} RSSI={Rssi}",
            address, args.RawSignalStrengthInDBm);

        // Surface as a logical advertisement.
        AdvertisementReceived?.Invoke(new BleAdvertisement
        {
            SourceUhid = address.ToString("X12"), // temporary until UHID is read from TX echo
            Rssi = args.RawSignalStrengthInDBm,
            ProtocolVersion = 2
        });

        await ConnectAsync(address, args.RawSignalStrengthInDBm);
    }

    // ── GATT connection ──────────────────────────────────────────────────────

    private async Task ConnectAsync(ulong address, short rssi)
    {
        try
        {
            var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
            if (device is null)
            {
                _logger.LogWarning("[BLE-Win] Could not get device for address {Address:X12}", address);
                return;
            }

            var svcResult = await device.GetGattServicesForUuidAsync(
                BleGattConstants.ServiceUuid,
                BluetoothCacheMode.Uncached);

            if (svcResult.Status != GattCommunicationStatus.Success
                || svcResult.Services.Count == 0)
            {
                _logger.LogWarning("[BLE-Win] GATT service not found on {Address:X12}: {Status}",
                    address, svcResult.Status);
                return;
            }

            var service = svcResult.Services[0];

            // Get TX characteristic (write without response).
            var txResult = await service.GetCharacteristicsForUuidAsync(BleGattConstants.TxCharacteristic);
            if (txResult.Status != GattCommunicationStatus.Success
                || txResult.Characteristics.Count == 0)
            {
                _logger.LogError("[BLE-Win] TX characteristic missing on {Address:X12}", address);
                return;
            }
            var txChar = txResult.Characteristics[0];

            // Get RX characteristic (notify).
            var rxResult = await service.GetCharacteristicsForUuidAsync(BleGattConstants.RxCharacteristic);
            if (rxResult.Status != GattCommunicationStatus.Success
                || rxResult.Characteristics.Count == 0)
            {
                _logger.LogError("[BLE-Win] RX characteristic missing on {Address:X12}", address);
                return;
            }
            var rxChar = rxResult.Characteristics[0];

            // Subscribe to notifications.
            var cccdStatus = await rxChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);

            if (cccdStatus != GattCommunicationStatus.Success)
            {
                _logger.LogError("[BLE-Win] Failed to enable notifications on {Address:X12}: {Status}",
                    address, cccdStatus);
                return;
            }

            var peer = new ConnectedPeer(address.ToString("X12"), device, service, txChar, rxChar);
            rxChar.ValueChanged += (s, e) => OnRxNotification(peer, e);

            if (_peers.TryAdd(address, peer))
            {
                _logger.LogInformation("[BLE-Win] Connected to Aether peripheral {Address:X12}", address);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BLE-Win] Connection failed for {Address:X12}", address);
        }
    }

    private void OnRxNotification(ConnectedPeer peer, GattValueChangedEventArgs args)
    {
        var reader = DataReader.FromBuffer(args.CharacteristicValue);
        var data = new byte[reader.UnconsumedBufferLength];
        reader.ReadBytes(data);

        _logger.LogDebug("[BLE-Win] RX notification from {Uhid}: {Bytes} bytes",
            peer.Uhid, data.Length);

        DataReceived?.Invoke(peer.Uhid, data);
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
        public string Uhid { get; set; }
        public bool IsAlive => _device.ConnectionStatus == BluetoothConnectionStatus.Connected;

        private readonly BluetoothLEDevice _device;
        private readonly GattDeviceService _service;
        private readonly GattCharacteristic _txChar;
        private readonly GattCharacteristic _rxChar;

        public ConnectedPeer(string uhid, BluetoothLEDevice device,
            GattDeviceService service,
            GattCharacteristic txChar, GattCharacteristic rxChar)
        {
            Uhid = uhid;
            _device = device;
            _service = service;
            _txChar = txChar;
            _rxChar = rxChar;
        }

        public async Task<bool> WriteAsync(byte[] data, CancellationToken ct)
        {
            try
            {
                var writer = new DataWriter();
                writer.WriteBytes(data);
                var status = await _txChar.WriteValueAsync(
                    writer.DetachBuffer(),
                    GattWriteOption.WriteWithoutResponse);
                return status == GattCommunicationStatus.Success;
            }
            catch (Exception)
            {
                return false;
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

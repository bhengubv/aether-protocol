// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using AetherNet.Transport.Abstractions;
using AetherNet.Transport.Services;
using Microsoft.Extensions.Logging;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace AetherNet.Transport.Windows.Services;

/// <summary>
/// Windows NFC (Aether White) transport — a real BLE-GATT central with an RSSI proximity gate.
///
/// <h3>Why this is not a stub</h3>
/// <c>Windows.Networking.Proximity</c> (PeerFinder/ProximityDevice) — the only NFC P2P API Windows
/// ever shipped — was built around the same "tap two devices" model as Android Beam, and Microsoft
/// removed the underlying NFP driver subsystem in Windows 11. Rather than leave a permanent
/// <c>IsAvailable=false</c> placeholder, this transport reproduces NFC's <b>proximity-as-security</b>
/// model over standard BLE GATT: it only connects to a peer whose advertisement is received above
/// <see cref="NfcGattConstants.ProximityThresholdDbm"/> (−40 dBm ≈ 5–10 cm physical separation).
/// Application code stays transport-agnostic — the bytes carried are raw NDEF/Aether payloads.
///
/// <h3>How it works</h3>
/// <list type="number">
///   <item><description>Scans for peripherals advertising <see cref="NfcGattConstants.ServiceUuid"/>
///     (built from the Aether NFC AID <c>F061657468657200</c>).</description></item>
///   <item><description>Ignores any advertisement weaker than −40 dBm — the "tap" gate.</description></item>
///   <item><description>Connects, subscribes to <see cref="NfcGattConstants.NotifyCharacteristic"/>,
///     and writes outbound bytes to <see cref="NfcGattConstants.WriteCharacteristic"/>.</description></item>
///   <item><description>Fragments payloads larger than the GATT MTU with
///     <see cref="BleGattFramer"/> and reassembles inbound frames per-peer.</description></item>
/// </list>
///
/// Counterpart peripheral: the Android <c>android/white/</c> node (which also exposes a real
/// PC/SC HCE path via the same AID for hardware NFC readers).
///
/// <h3>Upgrade path when NFC hardware is present</h3>
/// An ACR122U-class USB reader makes the PC/SC SELECT-AID path (<c>00 A4 04 00 08 F0 61 65 74 68
/// 65 72 00 00</c>) usable directly; if Microsoft ever re-ships a first-party P2P NFC API,
/// implement <see cref="ITransportService"/> over it with the same payload format. Neither changes
/// any peer or application code.
///
/// Source: NFC Forum NDEF 1.0 / SNEP 1.0; ACR122U PC/SC docs; Android HCE overview.
/// </summary>
public sealed class WinNfcBleTransportService : ITransportService, IAsyncDisposable
{
    private readonly ILogger<WinNfcBleTransportService> _logger;
    private readonly string _localUhid;
    private readonly ConcurrentDictionary<ulong, ConnectedPeer> _peers = new();
    private BluetoothLEAdvertisementWatcher? _watcher;
    private volatile bool _disposed;

    public WinNfcBleTransportService(string localUhid,
        ILogger<WinNfcBleTransportService> logger)
    {
        _localUhid = localUhid ?? throw new ArgumentNullException(nameof(localUhid));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // ── ITransportService ────────────────────────────────────────────────────

    /// <inheritdoc />
    public string Name => "Aether White (NFC)";

    /// <inheritdoc />
    public bool IsAvailable => !_disposed;

    /// <inheritdoc />
    public long MaxBandwidthBps => 848_000; // NFC 848 kbps max (ISO 14443)

    /// <inheritdoc />
    public int MaxRangeMeters => 0; // ~5 cm — effectively 0 in metres (proximity-gated)

    /// <inheritdoc />
    public int PowerCostRelative => 3;

    /// <inheritdoc />
    public int MaxConcurrentPeers => 1; // NFC is point-to-point

    /// <inheritdoc />
    public event Action<string, byte[]>? DataReceived;

    // ── Scanning ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts a proximity-gated BLE scan for Aether White peripherals. The watcher's
    /// signal-strength filter pre-screens at the OS level; a defensive re-check in the
    /// handler enforces the −40 dBm "tap" threshold exactly.
    /// </summary>
    public void StartScanning()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_watcher is not null) return;

        _watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active,
            // OS-level proximity pre-filter. OutOfRange is set well below the in-range gate
            // so a peer is only reported once it is genuinely close (NFC "tap" semantics).
            SignalStrengthFilter =
            {
                InRangeThresholdInDBm = NfcGattConstants.ProximityThresholdDbm,
                OutOfRangeThresholdInDBm = -70,
                OutOfRangeTimeout = TimeSpan.FromSeconds(2)
            }
        };
        _watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(NfcGattConstants.ServiceUuid);
        _watcher.Received += OnWatcherReceived;
        _watcher.Start();

        _logger.LogInformation(
            "[NFC-Win] Scanning for Aether White peripherals (service {Uuid}, RSSI gate {Gate} dBm)",
            NfcGattConstants.ServiceUuid, NfcGattConstants.ProximityThresholdDbm);
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
            _logger.LogWarning("[NFC-Win] No tapped peer with UHID {Uhid}", peerUhid);
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
        // The "tap" gate: NFC's physical-security model reproduced over BLE.
        if (args.RawSignalStrengthInDBm < NfcGattConstants.ProximityThresholdDbm)
            return;

        var address = args.BluetoothAddress;
        if (_peers.ContainsKey(address)) return;

        _logger.LogInformation("[NFC-Win] Tap detected: peripheral {Address:X12} RSSI={Rssi} dBm",
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
                _logger.LogWarning("[NFC-Win] Could not open device {Address:X12}", address);
                return;
            }

            var svcResult = await device.GetGattServicesForUuidAsync(
                NfcGattConstants.ServiceUuid, BluetoothCacheMode.Uncached);
            if (svcResult.Status != GattCommunicationStatus.Success || svcResult.Services.Count == 0)
            {
                _logger.LogWarning("[NFC-Win] Aether White service not found on {Address:X12}: {Status}",
                    address, svcResult.Status);
                device.Dispose();
                return;
            }

            var service = svcResult.Services[0];

            var writeResult = await service.GetCharacteristicsForUuidAsync(NfcGattConstants.WriteCharacteristic);
            if (writeResult.Status != GattCommunicationStatus.Success || writeResult.Characteristics.Count == 0)
            {
                _logger.LogError("[NFC-Win] Write characteristic missing on {Address:X12}", address);
                device.Dispose();
                return;
            }
            var writeChar = writeResult.Characteristics[0];

            var notifyResult = await service.GetCharacteristicsForUuidAsync(NfcGattConstants.NotifyCharacteristic);
            if (notifyResult.Status != GattCommunicationStatus.Success || notifyResult.Characteristics.Count == 0)
            {
                _logger.LogError("[NFC-Win] Notify characteristic missing on {Address:X12}", address);
                device.Dispose();
                return;
            }
            var notifyChar = notifyResult.Characteristics[0];

            var cccd = await notifyChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);
            if (cccd != GattCommunicationStatus.Success)
            {
                _logger.LogError("[NFC-Win] Could not enable notifications on {Address:X12}: {Status}",
                    address, cccd);
                device.Dispose();
                return;
            }

            var peer = new ConnectedPeer(address.ToString("X12"), device, service, writeChar, notifyChar);
            notifyChar.ValueChanged += (s, e) => OnNotification(peer, e);

            if (_peers.TryAdd(address, peer))
                _logger.LogInformation("[NFC-Win] Tapped Aether White peer {Address:X12}", address);
            else
                await peer.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[NFC-Win] Connection failed for {Address:X12}", address);
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
            _logger.LogDebug("[NFC-Win] Reassembled {Bytes} bytes from {Uhid}", message.Length, peer.Uhid);
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
        private readonly GattCharacteristic _writeChar;
        private readonly GattCharacteristic _notifyChar;
        private readonly List<byte[]> _rxFrames = new();
        private readonly object _rxLock = new();

        public ConnectedPeer(string uhid, BluetoothLEDevice device, GattDeviceService service,
            GattCharacteristic writeChar, GattCharacteristic notifyChar)
        {
            Uhid = uhid;
            _device = device;
            _service = service;
            _writeChar = writeChar;
            _notifyChar = notifyChar;
        }

        /// <summary>Fragments <paramref name="data"/> and writes every frame in order.</summary>
        public async Task<bool> WriteFramedAsync(byte[] data, CancellationToken ct)
        {
            try
            {
                foreach (var frame in BleGattFramer.Frame(data))
                {
                    ct.ThrowIfCancellationRequested();
                    var writer = new DataWriter();
                    writer.WriteBytes(frame);
                    var status = await _writeChar.WriteValueAsync(
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
        /// Adds an inbound frame to the per-peer reassembly buffer. A frame with index 0 starts
        /// a fresh message; when the buffer holds a complete sequence it is reassembled, the
        /// buffer is reset, and the reassembled bytes are returned. Returns <c>null</c> while
        /// the message is still incomplete.
        /// </summary>
        public byte[]? AccumulateFrame(byte[] frame)
        {
            lock (_rxLock)
            {
                // Frame header: [2] frame_count [2] frame_index. Index 0 = new message boundary.
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

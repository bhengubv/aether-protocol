// SPDX-License-Identifier: MIT
#if ANDROID
using Android.Bluetooth;
using Android.Bluetooth.LE;
using Android.Content;
using Android.OS;
using Java.Util;
using Microsoft.Extensions.Logging;
using AndroidApp = Android.App.Application;

namespace AetherNet.Sample.Platforms.Android.Transports;

/// <summary>
/// Real BLE (Bluetooth Low Energy) radio for AetherNet, native in the one APK. Each phone is
/// BOTH a peripheral (advertises + GATT server with an RX write char and a TX notify char) and
/// a central (scans for our service UUID + connects). The first side to connect becomes central
/// and writes to the other's RX; the peripheral notifies back over TX. A one-line UHID handshake
/// keys the peer, then every frame is a raw MeshPacket. No printers to confuse it — BLE filters
/// by our 128-bit service UUID.
/// </summary>
public sealed class AndroidBleTransportService : IRadio, IDisposable
{
    // Service/RX/TX are per-instance so the same GATT engine backs both BLE and the NearLink/SLE
    // façade (identical mechanics, different 128-bit UUIDs so the two don't cross-talk).
    private readonly UUID ServiceUuid;
    private readonly UUID RxUuid; // central → peripheral write
    private readonly UUID TxUuid; // peripheral → central notify
    private static readonly UUID CccdUuid = UUID.FromString("00002902-0000-1000-8000-00805f9b34fb")!;

    private readonly string _name;
    private readonly string _localUhid;
    private readonly ILogger _logger;
    private readonly BluetoothManager? _btManager;
    private readonly BluetoothAdapter? _adapter;

    private BluetoothLeAdvertiser? _advertiser;
    private AdvCallback? _advCallback;
    private BluetoothLeScanner? _scanner;
    private ScanCb? _scanCallback;

    private BluetoothGattServer? _gattServer;
    private BluetoothGattCharacteristic? _txChar;         // we notify on this (peripheral role)
    private BluetoothDevice? _peripheralPeer;             // the central that connected to us

    private BluetoothGatt? _gatt;                         // central role
    private BluetoothGattCharacteristic? _rxCharRemote;   // peer's RX we write to (central role)

    private volatile bool _linked;
    private volatile bool _disposed;
    private string? _peerTag;

    public AndroidBleTransportService(string name, string serviceUuid, string rxUuid, string txUuid,
        string localUhid, ILogger logger)
    {
        _name = name;
        ServiceUuid = UUID.FromString(serviceUuid)!;
        RxUuid = UUID.FromString(rxUuid)!;
        TxUuid = UUID.FromString(txUuid)!;
        _localUhid = localUhid;
        _logger = logger;
        _btManager = AndroidApp.Context.GetSystemService(Context.BluetoothService) as BluetoothManager;
        _adapter = _btManager?.Adapter;
    }

    public string Name => _name;
    // Available whenever Bluetooth is on — a device can always scan/connect (central) even if it
    // can't advertise (some phones, e.g. the P30 Lite, lack BLE peripheral support).
    public bool IsAvailable => _adapter is { IsEnabled: true };
    public bool IsLinked => _linked;
    public string? PeerTag => _peerTag;

    public event Action<string>? PeerLinked;
    public event Action<string, byte[]>? DataReceived;
    public event Action<string>? Status;

    private void L(string m) { global::Android.Util.Log.Info("AetherBLE", m); _logger.LogInformation("{M}", m); Status?.Invoke(m); }

    // ── Bring-up: advertise (peripheral) + scan (central) ───────────────────────

    public void Link()
    {
        if (_adapter is null || !_adapter.IsEnabled) { L("Bluetooth is off"); return; }
        L("linking — advertising + scanning for the AetherNet BLE service…");
        StartPeripheral();
        StartCentral();
    }

    private void StartPeripheral()
    {
        if (_adapter is null || !_adapter.IsMultipleAdvertisementSupported)
        {
            L("this phone can't BLE-advertise — running central-only (it will connect to the other)");
            return;
        }
        _gattServer = _btManager!.OpenGattServer(AndroidApp.Context!, new ServerCb(this));
        var service = new BluetoothGattService(ServiceUuid, GattServiceType.Primary);
        var rx = new BluetoothGattCharacteristic(RxUuid,
            GattProperty.Write | GattProperty.WriteNoResponse, GattPermission.Write);
        _txChar = new BluetoothGattCharacteristic(TxUuid, GattProperty.Notify, GattPermission.Read);
        _txChar.AddDescriptor(new BluetoothGattDescriptor(CccdUuid, GattDescriptorPermission.Read | GattDescriptorPermission.Write));
        service.AddCharacteristic(rx);
        service.AddCharacteristic(_txChar);
        _gattServer!.AddService(service);

        _advertiser = _adapter!.BluetoothLeAdvertiser;
        var settings = new AdvertiseSettings.Builder()!
            .SetAdvertiseMode(AdvertiseMode.LowLatency)!
            .SetConnectable(true)!
            .SetTxPowerLevel(AdvertiseTx.PowerHigh)!.Build();
        var data = new AdvertiseData.Builder()!
            .SetIncludeDeviceName(false)!
            .AddServiceUuid(new ParcelUuid(ServiceUuid))!.Build();
        _advCallback = new AdvCallback(this);
        _advertiser?.StartAdvertising(settings, data, _advCallback);
        L("peripheral: GATT server up, advertising");
    }

    private void StartCentral()
    {
        _scanner = _adapter!.BluetoothLeScanner;
        var filter = new ScanFilter.Builder()!.SetServiceUuid(new ParcelUuid(ServiceUuid))!.Build();
        var settings = new ScanSettings.Builder()!.SetScanMode(global::Android.Bluetooth.LE.ScanMode.LowLatency)!.Build();
        _scanCallback = new ScanCb(this);
        _scanner?.StartScan(new List<ScanFilter> { filter }, settings, _scanCallback);
        L("central: scanning for peers");
    }

    // ── Central side ────────────────────────────────────────────────────────────

    private void OnPeerFound(BluetoothDevice device)
    {
        if (_linked || _gatt is not null) return;      // one connection only
        L($"central: peer found ({device.Address}); connecting");
        try { _scanner?.StopScan(_scanCallback); } catch { }
        _gatt = device.ConnectGatt(AndroidApp.Context, false, new ClientCb(this), BluetoothTransports.Le);
    }

    private void OnClientConnected(BluetoothGatt gatt)
    {
        L("central: connected; requesting MTU + discovering services");
        gatt.RequestMtu(512);
    }

    private void OnServicesReady(BluetoothGatt gatt)
    {
        var svc = gatt.GetService(ServiceUuid);
        _rxCharRemote = svc?.GetCharacteristic(RxUuid);
        var tx = svc?.GetCharacteristic(TxUuid);
        if (_rxCharRemote is null || tx is null) { L("central: AetherNet chars missing"); return; }
        gatt.SetCharacteristicNotification(tx, true);
        var cccd = tx.GetDescriptor(CccdUuid);
        if (cccd is not null)
        {
            cccd.SetValue(BluetoothGattDescriptor.EnableNotificationValue!.ToArray());
            gatt.WriteDescriptor(cccd);   // BLE serialises GATT ops — send the handshake only once this completes
            L("central: subscribing…");
        }
        else { OnCccdWritten(); }
    }

    /// <summary>The notify-subscribe (CCCD) write completed — now it's safe to send the handshake.</summary>
    private void OnCccdWritten()
    {
        WriteToRemote(Encode("UHID:" + _localUhid));
        L("central: subscribed + handshake sent");
    }

    private void WriteToRemote(byte[] bytes)
    {
        if (_gatt is null || _rxCharRemote is null) return;
        _rxCharRemote.WriteType = GattWriteType.Default;
        _rxCharRemote.SetValue(bytes);
        _gatt.WriteCharacteristic(_rxCharRemote);
    }

    // ── Peripheral side ─────────────────────────────────────────────────────────

    private void OnServerWrite(BluetoothDevice device, byte[] value)
    {
        _peripheralPeer = device;
        HandleFrame(value, notifyBack: true);
    }

    private void NotifyPeer(byte[] bytes)
    {
        if (_gattServer is null || _txChar is null || _peripheralPeer is null) return;
        _txChar.SetValue(bytes);
        _gattServer.NotifyCharacteristicChanged(_peripheralPeer, _txChar, false);
    }

    // ── Shared frame handling ───────────────────────────────────────────────────

    private void HandleFrame(byte[] value, bool notifyBack)
    {
        if (value.Length > 5 && System.Text.Encoding.UTF8.GetString(value, 0, 5) == "UHID:")
        {
            _peerTag = System.Text.Encoding.UTF8.GetString(value, 5, value.Length - 5);
            _linked = true;
            L($"linked with {_peerTag}");
            PeerLinked?.Invoke(_peerTag);
            try { _advertiser?.StopAdvertising(_advCallback); } catch { }
            try { _scanner?.StopScan(_scanCallback); } catch { }
            if (notifyBack) NotifyPeer(Encode("UHID:" + _localUhid)); // peripheral answers the handshake
            return;
        }
        if (_peerTag is not null) DataReceived?.Invoke(_peerTag, value);
    }

    private static byte[] Encode(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    // ── IRadio ──────────────────────────────────────────────────────────────────

    public Task<bool> SendAsync(byte[] data)
    {
        if (!_linked) return Task.FromResult(false);
        if (_gatt is not null && _rxCharRemote is not null) WriteToRemote(data);   // we are central
        else NotifyPeer(data);                                                     // we are peripheral
        return Task.FromResult(true);
    }

    public void Stop() => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _advertiser?.StopAdvertising(_advCallback); } catch { }
        try { _scanner?.StopScan(_scanCallback); } catch { }
        try { _gatt?.Disconnect(); _gatt?.Close(); } catch { }
        try { _gattServer?.Close(); } catch { }
    }

    // ── Android callback adapters ───────────────────────────────────────────────

    private sealed class AdvCallback(AndroidBleTransportService o) : AdvertiseCallback
    {
        public override void OnStartFailure(AdvertiseFailure errorCode) => o.L($"advertise failed: {errorCode}");
    }

    private sealed class ScanCb(AndroidBleTransportService o) : ScanCallback
    {
        public override void OnScanResult(ScanCallbackType callbackType, ScanResult? result)
        {
            if (result?.Device is { } d) o.OnPeerFound(d);
        }
    }

    private sealed class ClientCb(AndroidBleTransportService o) : BluetoothGattCallback
    {
        public override void OnConnectionStateChange(BluetoothGatt? g, GattStatus status, ProfileState newState)
        {
            if (newState == ProfileState.Connected && g is not null) o.OnClientConnected(g);
        }
        public override void OnMtuChanged(BluetoothGatt? g, int mtu, GattStatus status)
        {
            g?.DiscoverServices();
        }
        public override void OnServicesDiscovered(BluetoothGatt? g, GattStatus status)
        {
            if (g is not null) o.OnServicesReady(g);
        }
        public override void OnDescriptorWrite(BluetoothGatt? g, BluetoothGattDescriptor? d, GattStatus status)
        {
            o.OnCccdWritten();
        }
        public override void OnCharacteristicChanged(BluetoothGatt? g, BluetoothGattCharacteristic? ch)
        {
            if (ch?.GetValue() is { } v) o.HandleFrame(v, notifyBack: false); // central got a TX notify
        }
    }

    private sealed class ServerCb(AndroidBleTransportService o) : BluetoothGattServerCallback
    {
        public override void OnCharacteristicWriteRequest(BluetoothDevice? device, int requestId,
            BluetoothGattCharacteristic? characteristic, bool preparedWrite, bool responseNeeded,
            int offset, byte[]? value)
        {
            if (device is not null && value is not null) o.OnServerWrite(device, value);
            if (responseNeeded)
                o._gattServer?.SendResponse(device, requestId, GattStatus.Success, offset, value);
        }

        // Must acknowledge the CCCD subscribe write, or the central's WriteDescriptor times out (~30s).
        public override void OnDescriptorWriteRequest(BluetoothDevice? device, int requestId,
            BluetoothGattDescriptor? descriptor, bool preparedWrite, bool responseNeeded,
            int offset, byte[]? value)
        {
            if (device is not null) o._peripheralPeer = device;
            if (responseNeeded)
                o._gattServer?.SendResponse(device, requestId, GattStatus.Success, offset, value);
        }

        public override void OnConnectionStateChange(BluetoothDevice? device, ProfileState status, ProfileState newState)
        {
            if (device is not null && newState == ProfileState.Connected) o._peripheralPeer = device;
        }
    }
}
#endif

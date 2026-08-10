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
/// Real BLE radio for AetherNet, native in the one APK. Each phone is BOTH a peripheral (advertises
/// + GATT server with an RX write char and a TX notify char) and a central (scans for our service
/// UUID + connects). The first to connect becomes central and writes to the other's RX; the
/// peripheral notifies back over TX.
///
/// A single GATT write/notify can only carry (MTU − 3) bytes, so every outbound message is
/// <b>fragmented</b> behind a tiny header and <b>reassembled</b> on the far side — that's what lets
/// BLE carry a whole signed card or a NamePublish, not just a short string. BLE also serialises GATT
/// operations, so fragments are sent <b>one at a time</b>, each gated on its send-complete callback.
/// </summary>
public sealed class AndroidBleTransportService : IRadio, IDisposable
{
    private readonly UUID ServiceUuid;
    private readonly UUID RxUuid; // central → peripheral write
    private readonly UUID TxUuid; // peripheral → central notify
    private static readonly UUID CccdUuid = UUID.FromString("00002902-0000-1000-8000-00805f9b34fb")!;

    // Frame kinds (first byte of every BLE frame).
    private const byte FrameHandshake = 0x01; // [0x01][uhid utf8]
    private const byte FrameFragment = 0x02;  // [0x02][msgId][idxLo idxHi][cntLo cntHi][payload]
    private const int FragHeader = 6;

    private readonly string _name;
    private readonly string _localUhid;
    private readonly string? _unavailableReason;
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
    private volatile int _mtu = 23;                       // negotiated ATT MTU (BLE default until raised)

    // Outbound queue — BLE serialises GATT ops, so one frame is in flight at a time.
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(3);

    private readonly object _sendLock = new();
    private readonly Queue<byte[]> _sendQueue = new();
    private bool _sending;
    private DateTime _sendStartedUtc;
    private byte _msgSeq;

    // Inbound reassembly, keyed by msgId.
    private readonly Dictionary<byte, Reassembly> _inbound = new();

    /// <param name="unavailableReason">
    /// Set when this instance stands in for a radio the device does not physically have — it then
    /// reports itself unavailable instead of quietly running over Bluetooth under another name.
    /// </param>
    public AndroidBleTransportService(string name, string serviceUuid, string rxUuid, string txUuid,
        string localUhid, ILogger logger, string? unavailableReason = null)
    {
        _unavailableReason = unavailableReason;
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

    /// <summary>
    /// Available whenever Bluetooth is on — a device can always scan/connect (central) even if it
    /// can't advertise (some phones, e.g. the P30 Lite, lack BLE peripheral support). An instance
    /// standing in for hardware the phone does not have is never available, whatever Bluetooth does.
    /// </summary>
    public bool IsAvailable => _unavailableReason is null && _adapter is { IsEnabled: true };

    /// <inheritdoc />
    public string? UnavailableReason => _unavailableReason
        ?? (_adapter is null ? "this phone has no Bluetooth"
            : !_adapter.IsEnabled ? "Bluetooth is switched off"
            : null);
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
        gatt.RequestMtu(517);
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
        EnqueueFrames(new[] { Handshake() });
        L($"central: subscribed + handshake queued (mtu {_mtu})");
    }

    // ── Peripheral side ─────────────────────────────────────────────────────────

    private void OnServerWrite(BluetoothDevice device, byte[] value)
    {
        _peripheralPeer = device;
        HandleFrame(value, notifyBack: true);
    }

    // ── Outbound: fragment → serialised send ────────────────────────────────────

    public Task<bool> SendAsync(byte[] data)
    {
        if (!_linked) return Task.FromResult(false);
        EnqueueFrames(Fragment(data));
        return Task.FromResult(true);
    }

    private byte[] Handshake()
    {
        var uhid = System.Text.Encoding.UTF8.GetBytes(_localUhid);
        var f = new byte[1 + uhid.Length];
        f[0] = FrameHandshake;
        Buffer.BlockCopy(uhid, 0, f, 1, uhid.Length);
        return f;
    }

    private List<byte[]> Fragment(byte[] data)
    {
        var mtu = _mtu > 0 ? _mtu : 23;
        var usable = Math.Max(1, mtu - 3 - FragHeader);           // ATT header is 3 bytes
        var count = Math.Max(1, (data.Length + usable - 1) / usable);
        byte id;
        lock (_sendLock) { id = unchecked(_msgSeq++); }
        var frames = new List<byte[]>(count);
        for (var i = 0; i < count; i++)
        {
            var off = i * usable;
            var len = Math.Min(usable, data.Length - off);
            var f = new byte[FragHeader + len];
            f[0] = FrameFragment;
            f[1] = id;
            f[2] = (byte)(i & 0xFF); f[3] = (byte)((i >> 8) & 0xFF);
            f[4] = (byte)(count & 0xFF); f[5] = (byte)((count >> 8) & 0xFF);
            Buffer.BlockCopy(data, off, f, FragHeader, len);
            frames.Add(f);
        }
        return frames;
    }

    private void EnqueueFrames(IEnumerable<byte[]> frames)
    {
        lock (_sendLock)
        {
            foreach (var f in frames) _sendQueue.Enqueue(f);
        }
        PumpSend();
    }

    private void PumpSend()
    {
        byte[]? frame;
        lock (_sendLock)
        {
            if (_sendQueue.Count == 0) return;
            if (_sending)
            {
                // A frame is still in flight. BLE only ever confirms via a callback, and if the stack
                // silently drops one the queue would wedge forever and every later packet would vanish
                // with no error. Time it out and carry on rather than going quiet.
                if (DateTime.UtcNow - _sendStartedUtc < SendTimeout) return;
                L($"send timed out after {SendTimeout.TotalMilliseconds:0}ms — releasing the queue");
            }
            _sending = true;
            _sendStartedUtc = DateTime.UtcNow;
            frame = _sendQueue.Dequeue();
        }
        SendFrameNow(frame);
    }

    // Called by the write-complete / notification-sent callbacks so the next fragment can go.
    private void OnFrameSent()
    {
        lock (_sendLock) { _sending = false; }
        PumpSend();
    }

    private void SendFrameNow(byte[] frame)
    {
        try
        {
            if (_gatt is not null && _rxCharRemote is not null)          // central → write
            {
                _rxCharRemote.WriteType = GattWriteType.Default;         // with response ⇒ OnCharacteristicWrite fires
                _rxCharRemote.SetValue(frame);
                var ok = _gatt.WriteCharacteristic(_rxCharRemote);
                L($"▶ central write {frame.Length}B accepted={ok}");
                if (!ok) OnFrameSent();                                  // stack refused it — don't wedge the queue
            }
            else if (_gattServer is not null && _txChar is not null && _peripheralPeer is not null) // peripheral → notify
            {
                _txChar.SetValue(frame);
                var ok = _gattServer.NotifyCharacteristicChanged(_peripheralPeer, _txChar, false); // ⇒ OnNotificationSent
                L($"▶ peripheral notify {frame.Length}B accepted={ok}");
                if (!ok) OnFrameSent();
            }
            else
            {
                L($"▶ dropped {frame.Length}B — no GATT path (central={_gatt is not null} peripheral={_peripheralPeer is not null})");
                lock (_sendLock) { _sending = false; }                  // no transport yet — stop cleanly
            }
        }
        catch (Exception ex)
        {
            L($"send failed: {ex.Message}");
            OnFrameSent();                                              // skip this frame, keep the queue moving
        }
    }

    // ── Inbound: dispatch handshake vs fragment; reassemble ─────────────────────

    private void HandleFrame(byte[] value, bool notifyBack)
    {
        if (value.Length == 0) return;

        if (value[0] == FrameHandshake)
        {
            _peerTag = System.Text.Encoding.UTF8.GetString(value, 1, value.Length - 1);
            _linked = true;
            L($"linked with {_peerTag}");
            PeerLinked?.Invoke(_peerTag);
            try { _advertiser?.StopAdvertising(_advCallback); } catch { }
            try { _scanner?.StopScan(_scanCallback); } catch { }
            if (notifyBack) EnqueueFrames(new[] { Handshake() });      // peripheral answers the handshake
            return;
        }

        if (value[0] == FrameFragment)
        {
            var full = Reassemble(value);
            L($"◀ fragment {value.Length}B{(full is not null ? $" — message complete, {full.Length}B" : "")}");
            if (full is not null && _peerTag is not null)
                DataReceived?.Invoke(_peerTag, full);
        }
    }

    private byte[]? Reassemble(byte[] frame)
    {
        if (frame.Length < FragHeader) return null;
        var id = frame[1];
        var idx = frame[2] | (frame[3] << 8);
        var cnt = frame[4] | (frame[5] << 8);
        if (cnt <= 0 || idx < 0 || idx >= cnt) return null;

        lock (_inbound)
        {
            if (!_inbound.TryGetValue(id, out var asm) || asm.Count != cnt)
            {
                asm = new Reassembly(cnt);
                _inbound[id] = asm;
            }
            if (asm.Parts[idx] is null)
            {
                var payload = new byte[frame.Length - FragHeader];
                Buffer.BlockCopy(frame, FragHeader, payload, 0, payload.Length);
                asm.Parts[idx] = payload;
                asm.Have++;
            }
            if (asm.Have < asm.Count) return null;

            var total = 0;
            foreach (var p in asm.Parts) total += p!.Length;
            var full = new byte[total];
            var o = 0;
            foreach (var p in asm.Parts) { Buffer.BlockCopy(p!, 0, full, o, p!.Length); o += p!.Length; }
            _inbound.Remove(id);
            return full;
        }
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

    private sealed class Reassembly
    {
        public Reassembly(int count) { Count = count; Parts = new byte[count][]; }
        public int Count { get; }
        public byte[]?[] Parts { get; }
        public int Have { get; set; }
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
            o._mtu = mtu;
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
        public override void OnCharacteristicWrite(BluetoothGatt? g, BluetoothGattCharacteristic? ch, GattStatus status)
        {
            o.L($"write complete status={status}");
            o.OnFrameSent();   // a fragment write completed — release the next
        }

        public override void OnCharacteristicChanged(BluetoothGatt? g, BluetoothGattCharacteristic? ch)
        {
            var v = ch?.GetValue();
            o.L($"notify in (legacy cb) {v?.Length.ToString() ?? "null"}B");
            if (v is not null) o.HandleFrame(v, notifyBack: false); // central got a TX notify
        }

        // Android 13+ calls this overload instead of the deprecated one above. Harmless on older
        // devices; without it a phone on 13+ would silently receive nothing.
        public override void OnCharacteristicChanged(BluetoothGatt g, BluetoothGattCharacteristic ch, byte[] value)
        {
            o.L($"notify in (value cb) {value?.Length ?? 0}B");
            if (value is not null) o.HandleFrame(value, notifyBack: false);
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

        public override void OnNotificationSent(BluetoothDevice? device, GattStatus status)
        {
            o.OnFrameSent();   // a notify was flushed — release the next fragment
        }

        public override void OnMtuChanged(BluetoothDevice? device, int mtu)
        {
            o._mtu = mtu;
        }

        public override void OnConnectionStateChange(BluetoothDevice? device, ProfileState status, ProfileState newState)
        {
            if (device is not null && newState == ProfileState.Connected) o._peripheralPeer = device;
        }
    }
}
#endif

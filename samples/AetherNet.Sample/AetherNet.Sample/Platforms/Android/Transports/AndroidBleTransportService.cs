// SPDX-License-Identifier: MIT
#if ANDROID
using Android.Bluetooth;
using Android.Bluetooth.LE;
using Android.Content;
using Android.OS;
using Java.Util;
using Microsoft.Extensions.Logging;
using AndroidApp = Android.App.Application;

using AetherNet.Sample.Shared.Services;

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
    /// <summary>
    /// What this radio advertises and scans for.
    /// </summary>
    /// <remarks>
    /// Not readonly, because it is the meeting. One fixed id for the whole app means every phone
    /// running AetherNet answers every other one within range — which is discovery, and discovery is
    /// precisely what must not happen: a radio that will link to anybody is a radio that will carry
    /// anybody's traffic. Advertising the meeting means only the person whose tag you were handed can
    /// see this phone, and only they can be seen by it.
    /// </remarks>
    private UUID ServiceUuid;
    private readonly UUID RxUuid; // central → peripheral write
    private readonly UUID TxUuid; // peripheral → central notify
    private static readonly UUID CccdUuid = UUID.FromString("00002902-0000-1000-8000-00805f9b34fb")!;


    private readonly string _name;
    private readonly string _localUhid;
    private readonly byte[] _routingKey;
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
    private volatile bool _relinking;                     // rebuilding after a drop; ignore further drops
    private volatile bool _disposed;

    // Proof of life — the rules live in LinkLiveness so they can be reasoned about away from a radio.
    private readonly LinkLiveness _liveness = new();
    private System.Threading.Timer? _watchdog;
    private string? _peerTag;
    private string? _peerErid;
    private volatile int _mtu = 23;                       // negotiated ATT MTU (BLE default until raised)

    // Outbound queue — BLE serialises GATT ops, so one frame is in flight at a time.
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(3);

    private readonly object _sendLock = new();
    private readonly Queue<byte[]> _sendQueue = new();

    /// <summary>
    /// How many frames may wait for the radio. At 20ms of speech per frame this is about half a
    /// second — deep enough that a burst of chat is never touched, shallow enough that audio delay
    /// cannot creep up over the length of a call.
    /// </summary>
    private const int MaxQueuedFrames = 24;
    private int _dropReports;
    private bool _sending;
    private DateTime _sendStartedUtc;
    private byte _msgSeq;

    private readonly MeshFraming.Reassembler _reassembler = new();

    /// <param name="unavailableReason">
    /// Set when this instance stands in for a radio the device does not physically have — it then
    /// reports itself unavailable instead of quietly running over Bluetooth under another name.
    /// </param>
    public AndroidBleTransportService(string name, string serviceUuid, string rxUuid, string txUuid,
        string localUhid, ILogger logger, string? unavailableReason = null, byte[]? routingKey = null)
    {
        _unavailableReason = unavailableReason;
        _name = name;
        ServiceUuid = UUID.FromString(serviceUuid)!;
        RxUuid = UUID.FromString(rxUuid)!;
        TxUuid = UUID.FromString(txUuid)!;
        _localUhid = localUhid;
        // Must come from the identity SECRET. Deriving it from the AetherTag would be theatre: the
        // tag is public — printed on QR codes, read aloud — so every address it ever produced could
        // be computed by anyone, which is worse than sending the tag, because it looks private.
        _routingKey = routingKey ?? throw new ArgumentNullException(nameof(routingKey),
            "A rotating wire address needs a key derived from the identity secret, not from the public tag.");
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

    /// <inheritdoc />
    /// <remarks>A switched-off adapter is a tap away; a phone with no Bluetooth in it is not.</remarks>
    public bool IsFixable => _unavailableReason is null && _adapter is { IsEnabled: false };

    /// <summary>
    /// What this actually carries between two handsets, measured — not what the spec promises, and
    /// not what MTU arithmetic suggests.
    ///
    /// <para>
    /// BLE advertises megabits. Counted during live calls on 2026-08-20, these two phones moved
    /// 9–10 packets a second in ONE direction: 13 central writes accepted against 1,487 deferred.
    /// That is about eleven kilobits — ample for chat, and hopeless for voice by a factor of five.
    /// </para>
    ///
    /// <para>
    /// The ceiling is not a tuning problem. A GATT connection carries one operation in flight at a
    /// time, and a write issued while another is outstanding is refused on the spot. MTU changes how
    /// many bytes ride each operation; it does not change how many operations fit in a second, and a
    /// voice frame is bound by the second, not by the byte.
    /// </para>
    /// </summary>
    // Two wrong numbers stood here before this one. 5_000 was read off a link still on the 23-byte
    // default MTU. 100_000 replaced it by arithmetic — MTU 517 times fifty a second — and was
    // labelled "measured" without anyone having measured it. This one was counted. The number is
    // load-bearing twice over: Widest() picks the radio traffic leaves on from it, and the codec
    // sizes itself from it, so a flattering figure buys a call that cannot work and will not say so.
    public long MaxBandwidthBps => 11_000;
    /// <summary>
    /// Linked means a peer is there <b>and</b> there is a way to reach them. Both halves, or it is not
    /// a link.
    ///
    /// <para>
    /// This used to report the first half alone, and the two come up separately: a handshake arriving
    /// inbound sets the peer, while the ability to answer needs the central to have subscribed to the
    /// notify characteristic. Between those two moments this radio said it was linked and could not
    /// send a byte — which is exactly what "app→radio 391B on BLE linked=True sent=False" was, over
    /// and over, on a phone whose queue filled with frames it had nowhere to put.
    /// </para>
    ///
    /// <para>
    /// The damage was not the wasted frames. The mesh picks a radio by asking which one is linked, so
    /// a radio claiming a link it cannot use takes the traffic and drops it, while a radio that could
    /// have carried it sits idle. Saying "not linked" is what lets the mesh route around it.
    /// </para>
    /// </summary>
    public bool IsLinked => _linked && HasSendPath;

    /// <summary>
    /// Whether there is a live GATT path out of here — a remote characteristic to write to as the
    /// central, or a subscribed peer to notify as the peripheral.
    /// </summary>
    private bool HasSendPath =>
        (_gatt is not null && _rxCharRemote is not null) ||
        (_gattServer is not null && _txChar is not null && _peripheralPeer is not null);
    public string? PeerTag => _peerTag;

    public event Action<string>? PeerLinked;
    public event Action<string, byte[]>? DataReceived;
    public event Action<string>? Status;

    private void L(string m) { global::Android.Util.Log.Info("AetherBLE", m); _logger.LogInformation("{M}", m); Status?.Invoke(m); }

    // ── Bring-up: advertise (peripheral) + scan (central) ───────────────────────

    /// <summary>
    /// Come up to meet one particular person, rather than to meet anybody.
    /// </summary>
    /// <remarks>
    /// The meeting becomes the service id — the thing a peripheral advertises and a central filters
    /// its scan on, which is the whole of the matching in Bluetooth. Nothing else about the bring-up
    /// changes; it simply stops answering strangers.
    /// </remarks>
    public void Link(AetherNet.Sample.Shared.Services.Meeting meeting)
    {
        var wanted = UUID.FromString(meeting.Uuid().ToString())!;
        _told = true;

        // Already advertising this one. Tearing the link down to rebuild it identically is how a
        // healthy link ends up re-handshaking on a loop.
        if (!wanted.Equals(ServiceUuid))
        {
            L($"meeting {meeting.PeerTag} — advertising for them alone");
            ServiceUuid = wanted;

            // The old service and the old scan filter are for somebody else now.
            if (_linked || _gatt is not null || _peripheralPeer is not null)
                ResetLink("meeting somebody else");
        }

        Link();
    }

    /// <summary>Whether this radio has been told who it is meeting.</summary>
    private bool _told;

    public void Link()
    {
        // Registered even when the adapter is currently off, so the radio is picked up the moment the
        // person turns Bluetooth back on — and, more importantly, so we are told BEFORE it goes away.
        WatchAdapterState();

        // Nobody named, nobody met.
        //
        // Coming up without a meeting means advertising one id for the whole app and answering any
        // phone running it — which is discovery, the thing that must not happen. It also beat the
        // meeting to it: BLE linked to a stranger within a second of launch, and the mesh then skipped
        // it as "already linked" when the real meeting arrived. So the promiscuous door is shut, and
        // this radio waits to be told.
        if (!_told) { L("waiting to be told who to meet"); return; }

        if (_adapter is null || !_adapter.IsEnabled) { L("Bluetooth is off"); return; }
        if (LinkLooksAlive()) { L("already linked"); return; }

        // Rebuild, and ignore the disconnects our own teardown provokes.
        _relinking = true;
        try
        {
            if (_linked || _gatt is not null || _peripheralPeer is not null)
                ResetLink("the link stopped answering");

            L("linking — advertising + scanning for the AetherNet BLE service…");
            StartPeripheral();
            StartCentral();
            StartWatchdog();
        }
        finally { _relinking = false; }
    }

    /// <summary>
    /// Is this link actually carrying traffic, or does it only look connected?
    /// <para>
    /// A flag is not enough. When the other phone's app restarts, its GATT server goes with it, but
    /// Android can leave our connection object alive and never report a disconnect — so we keep
    /// writing into a socket nobody is reading and hear nothing back. That state passed an
    /// <c>IsLinked</c> check happily, which is exactly how a phone got stranded: every message left,
    /// no receipt ever returned, and re-linking was skipped as unnecessary.
    /// </para>
    /// <para>
    /// Since every message is now acknowledged, silence after we have spoken is real evidence. If our
    /// last send is newer than anything we have heard, and that was a while ago, the link is dead
    /// whatever the flag says.
    /// </para>
    /// </summary>
    private bool LinkLooksAlive()
    {
        if (!_linked) return false;

        var haveTransport = (_gatt is not null && _rxCharRemote is not null) || _peripheralPeer is not null;
        if (!haveTransport) return false;

        // Only an unanswered question counts against the link.
        return !_liveness.IsLost(DateTime.UtcNow);
    }

    /// <summary>
    /// Watch the link even when nobody is looking at it. Checking only when the user taps Connect is
    /// useless: at that moment the link has just been made and is healthy by definition. The state we
    /// need to catch — connected object, dead peer, silence — appears minutes later, mid-conversation,
    /// and only a standing check will see it.
    /// </summary>
    private void StartWatchdog()
    {
        _watchdog ??= new System.Threading.Timer(_ =>
        {
            if (_disposed || _relinking || !_linked) return;

            var now = DateTime.UtcNow;

            if (_liveness.ShouldPing(now))
            {
                // Quiet. Ask outright rather than assuming the worst — and asking regularly also keeps
                // Android from reaping a connection it thinks nobody is using.
                _liveness.NotePingSent(now);
                EnqueueFrames(new[] { new[] { MeshFraming.FramePing } });
                return;
            }

            if (_liveness.IsLost(now))
                OnLinkLost($"nothing back from the peer for {LinkLiveness.PongWithin.TotalSeconds:0}s");
        }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    /// <summary>Drop everything tied to the current connection. Does not start looking again.</summary>
    private void ResetLink(string why)
    {
        L($"link reset ({why})");

        _linked = false;
        _peerTag = null;
        _rxCharRemote = null;
        _peripheralPeer = null;
        _mtu = 23;
        _liveness.Reset();

        try { _gatt?.Close(); } catch { }
        _gatt = null;

        lock (_sendLock)
        {
            _sendQueue.Clear();      // queued frames belong to a peer that is gone
            _sending = false;
        }
        // reassembly state belongs to the shared framing and is rebuilt on the next fragment
    }

    /// <summary>
    /// The peer went away — the other phone closed the app, walked out of range, or turned Bluetooth
    /// off. Android tells us once and never again, so everything that was tied to that connection has
    /// to be let go here: the flag the UI reads, the GATT client, the device we were notifying, and
    /// anything queued for a peer that can no longer receive it.
    /// <para>
    /// Then we start looking again. Without this the radio stays convinced it is still connected,
    /// the Connect button stays disabled because the app believes it has a link, and the peripheral
    /// never resumes advertising — so the two phones can never find each other again without both
    /// apps being restarted. That is what stranded merlin.
    /// </para>
    /// </summary>
    private void OnLinkLost(string why)
    {
        if (_disposed || _relinking) return;
        var had = _linked || _gatt is not null || _peripheralPeer is not null;
        if (!had) return;

        // Tearing the old server down to rebuild it makes Android report *another* disconnect. Doing
        // the rebuild inline therefore re-enters this method and recurses until the stack gives out —
        // which is exactly how this crashed the app the first time. Latch, then rebuild off this
        // callback thread once the radio has settled.
        _relinking = true;

        L($"link lost ({why}) — clearing and listening again");
        ResetLink(why);
        Status?.Invoke("link lost");

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            _relinking = false;
            if (!_disposed && !_linked) Link();   // advertise + scan again so the link can re-form
        });
    }

    private void StartPeripheral()
    {
        if (_adapter is null || !_adapter.IsMultipleAdvertisementSupported)
        {
            L("this phone can't BLE-advertise — running central-only (it will connect to the other)");
            return;
        }
        // Re-linking must not stack a second server and advertiser on top of the first.
        try { if (_advCallback is not null) _advertiser?.StopAdvertising(_advCallback); } catch { }
        try { _gattServer?.Close(); } catch { }
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
        try { if (_scanCallback is not null) _scanner?.StopScan(_scanCallback); } catch { }
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

        // Refuse the whole packet when the radio is behind — and never drop individual fragments.
        //
        // A packet goes out as several BLE writes that the far side reassembles. Dropping one fragment
        // does not lose one packet: it corrupts the packet that fragment belonged to, and the orphans
        // left behind splice into whatever arrives next. Tried exactly that for one build and the audio
        // went from choppy to silent, which is precisely what a reassembler fed half-packets sounds
        // like.
        //
        // Refusing here keeps every packet whole and puts the decision where it belongs: the caller
        // knows whether its packet is disposable. The voice queue drops its oldest frame and carries
        // on; chat sees a failed send and re-sends, which it already knows how to do.
        lock (_sendLock)
        {
            if (_sendQueue.Count > MaxQueuedFrames)
            {
                if (++_dropReports % 50 == 1)
                    L($"radio is behind ({_sendQueue.Count} frames queued) — refusing packets until it catches up");
                return Task.FromResult(false);
            }
        }

        EnqueueFrames(Fragment(data));
        return Task.FromResult(true);
    }

    /// <summary>
    /// The opening frame. Built by the shared framing rather than here: it is protocol, not platform,
    /// and a second copy on each radio head is how the two drifted apart in the first place.
    /// </summary>
    private byte[] Handshake() => MeshFraming.Handshake(_routingKey);

    private IReadOnlyList<byte[]> Fragment(byte[] data)
    {
        byte id;
        lock (_sendLock) { id = unchecked(_msgSeq++); }
        return MeshFraming.Fragment(data, _mtu, id);
    }

    /// <summary>
    /// Queue frames for the radio, and never let the queue become the delay.
    ///
    /// <para>
    /// This was unbounded. A microphone produces fifty frames a second regardless of what the radio is
    /// doing, and this drains one frame per write-complete callback — so any shortfall accumulated
    /// here, forever. On two phones it reached roughly thirty seconds of backed-up audio: every word
    /// arrived, in order, half a minute late. The call service above bounds its own queue, but a
    /// bounded queue draining into an unbounded one is only a slower way to grow.
    /// </para>
    ///
    /// <para>
    /// Over the cap, the OLDEST frame goes. For speech that is plainly right — nobody wants half a
    /// minute of backlog played at them. It is right for the rest too: everything on this radio is
    /// either real-time, or a message whose delivery is already tracked and re-sent by the layer that
    /// cares about it. Half a second of queue is far more than a burst of chat ever needs, so what
    /// actually gets dropped here is voice, which is exactly the traffic that should be.
    /// </para>
    /// </summary>
    /// <summary>
    /// Queue a packet's fragments. Once a packet is admitted, every one of its fragments goes — the
    /// queue depth is enforced in <see cref="SendAsync"/>, on whole packets, because a half-sent packet
    /// is worse than no packet at all.
    /// </summary>
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

    /// <summary>
    /// The stack refused the frame. Put it back and try again — do NOT treat it as sent.
    ///
    /// <para>
    /// This was the single biggest thing wrong with voice on this radio. Android's GATT stack allows
    /// one operation in flight per connection and returns false for anything offered while the previous
    /// is outstanding. The old code called OnFrameSent() on a refusal — marking the frame delivered and
    /// moving to the next — so a refused write silently threw the frame away. Measured on a live call:
    /// 820 refusals against 44 accepted writes. Ninety-five per cent of the audio never reached the
    /// air, and the log reported a healthy fifty writes a second, because it was counting attempts.
    /// </para>
    ///
    /// <para>
    /// Back at the FRONT of the queue, because a packet is several fragments the far side reassembles
    /// in order — a frame returning in the wrong place corrupts its packet as surely as dropping it.
    /// </para>
    /// </summary>
    private void Requeue(byte[] frame)
    {
        lock (_sendLock)
        {
            var rest = _sendQueue.ToArray();
            _sendQueue.Clear();
            _sendQueue.Enqueue(frame);
            foreach (var f in rest) _sendQueue.Enqueue(f);
            _sending = false;
        }

        // The stack refuses while an operation is outstanding, so the completion callback usually
        // restarts us. This is the safety net for when it does not.
        _ = Task.Delay(RetryAfter).ContinueWith(_ => { if (!_disposed) PumpSend(); });
    }

    /// <summary>How long to wait before offering a refused frame again.</summary>
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMilliseconds(6);

    /// <summary>
    /// Account for a frame offered to the radio, and say something occasionally.
    ///
    /// <para>
    /// Emphatically not one line per frame. At fifty frames a second, writing to logcat, to the logger
    /// and raising a UI event for every one is real work on the very thread meant to be feeding the
    /// radio — the instrumentation was competing with the thing it measured.
    /// </para>
    /// </summary>
    private void Sent(bool accepted, byte[] frame, string how)
    {
        if (accepted) _framesAccepted++; else _framesRefused++;

        if ((_framesAccepted + _framesRefused) % 250 == 0)
            L($"▶ {how}: {_framesAccepted} on the air, {_framesRefused} deferred (radio busy), {frame.Length}B");
    }

    private int _pathReports;
    private int _framesAccepted;
    private int _framesRefused;

    private void SendFrameNow(byte[] frame)
    {
        try
        {
            if (_gatt is not null && _rxCharRemote is not null)          // central → write
            {
                _rxCharRemote.WriteType = GattWriteType.Default;         // with response ⇒ OnCharacteristicWrite fires
                _rxCharRemote.SetValue(frame);
                var ok = _gatt.WriteCharacteristic(_rxCharRemote);
                Sent(ok, frame, "central write");
                if (!ok) { Requeue(frame); return; }                     // refused ⇒ back-pressure, NOT delivery
            }
            else if (_gattServer is not null && _txChar is not null && _peripheralPeer is not null) // peripheral → notify
            {
                _txChar.SetValue(frame);
                var ok = _gattServer.NotifyCharacteristicChanged(_peripheralPeer, _txChar, false); // ⇒ OnNotificationSent
                Sent(ok, frame, "peripheral notify");
                if (!ok) { Requeue(frame); return; }                     // refused ⇒ back-pressure, NOT delivery
            }
            else
            {
                // Hold it rather than throw it away. The path comes up moments after the peer does —
                // the central still has to subscribe — and a frame discarded in that window is a hole
                // in the middle of a packet the far side is reassembling, which is worse than late.
                //
                // The queue is bounded by MaxQueuedFrames above, so a path that never arrives cannot
                // grow this without limit; it fills, SendAsync starts refusing, and IsLinked now says
                // "not linked" so the mesh sends over something that works instead.
                if (++_pathReports % 50 == 1)
                    L($"▶ holding {frame.Length}B — no GATT path yet (central={_gatt is not null} peripheral={_peripheralPeer is not null})");
                Requeue(frame);
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
        _liveness.RecordInbound(DateTime.UtcNow);   // the peer is demonstrably still there

        if (value[0] == MeshFraming.FramePing) { EnqueueFrames(new[] { new[] { MeshFraming.FramePong } }); return; }
        if (value[0] == MeshFraming.FramePong) return;   // liveness only — nothing else to do with it

        if (value[0] == MeshFraming.FrameHandshake)
        {
            // A rotating address, not an identity. Who the peer actually is arrives inside the first
            // message they send — the long-term tag never travels in clear, which is the whole point.
            _peerErid = System.Text.Encoding.UTF8.GetString(value, 1, value.Length - 1);
            _linked = true;
            L($"linked with a peer at {_peerErid}");
            PeerLinked?.Invoke(_peerErid);
            try { _advertiser?.StopAdvertising(_advCallback); } catch { }
            try { _scanner?.StopScan(_scanCallback); } catch { }
            if (notifyBack) EnqueueFrames(new[] { Handshake() });      // peripheral answers the handshake
            return;
        }

        if (value[0] == MeshFraming.FrameFragment)
        {
            var full = Reassemble(value);
            L($"◀ fragment {value.Length}B{(full is not null ? $" — message complete, {full.Length}B" : "")}");
            if (full is null) return;

            // The peer names itself inside the message it just sent, so this is where we learn who is
            // on the other end — after the link exists, never before it.
            LearnPeerFrom(full);
            DataReceived?.Invoke(_peerTag ?? _peerErid ?? "", full);
        }
    }

    private byte[]? Reassemble(byte[] frame) => _reassembler.Accept(frame);

    /// <summary>
    /// Read the peer's AetherTag out of a message they sent us. The handshake deliberately carries no
    /// identity, so this is the first point at which the other end has a name.
    /// </summary>
    private void LearnPeerFrom(byte[] packetBytes)
    {
        if (_peerTag is not null) return;
        try
        {
            var source = AetherNet.Protocol.PacketSerializer.Deserialize(packetBytes).SourceUhid;
            if (string.IsNullOrEmpty(source)) return;
            _peerTag = source;
            L($"peer is {_peerTag}");
            PeerLinked?.Invoke(_peerTag);
        }
        catch { /* not a packet we can read — the link still stands */ }
    }

    public void Stop() => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { if (_stateReceiver is not null) AndroidApp.Context!.UnregisterReceiver(_stateReceiver); } catch { }
        _stateReceiver = null;
        ReleaseRadio();
    }

    /// <summary>
    /// Hand every Bluetooth object back. Safe to call at any time and more than once.
    /// </summary>
    private void ReleaseRadio()
    {
        try { _watchdog?.Dispose(); } catch { }
        _watchdog = null;

        try { if (_advCallback is not null) _advertiser?.StopAdvertising(_advCallback); } catch { }
        try { if (_scanCallback is not null) _scanner?.StopScan(_scanCallback); } catch { }
        try { _gatt?.Disconnect(); _gatt?.Close(); } catch { }
        try { _gattServer?.Close(); } catch { }

        _advertiser = null;
        _scanner = null;
        _gatt = null;
        _gattServer = null;
        _txChar = null;
        _rxCharRemote = null;
        _peripheralPeer = null;
        _linked = false;
        _peerTag = null;
        _peerErid = null;
        _liveness.Reset();

        lock (_sendLock) { _sendQueue.Clear(); _sending = false; }
    }

    // ── The adapter going away ──────────────────────────────────────────────────

    private StateReceiver? _stateReceiver;

    /// <summary>
    /// Watch for Bluetooth being switched off, and let go of everything before it goes.
    ///
    /// <para>
    /// Android announces <c>STATE_TURNING_OFF</c> <b>before</b> the stack is torn down, which is the
    /// only moment an app can release cleanly. Ignoring it means the framework tries to dismantle a
    /// stack while this app still holds an open GATT server, a running advertiser, a scanner, a live
    /// client connection, and a watchdog timer still pushing pings into it.
    /// </para>
    ///
    /// <para>
    /// That is not theoretical. On 2026-08-15 turning Bluetooth off on a Redmi while this transport was
    /// linked took <b>system_server</b> down with it — zygote and system_server restarted (kernel uptime
    /// untouched at 8 days, so the phone never rebooted, but the whole Android runtime did). An app must
    /// never be able to do that to the device it is running on.
    /// </para>
    /// </summary>
    private void WatchAdapterState()
    {
        if (_stateReceiver is not null) return;

        _stateReceiver = new StateReceiver(this);
        AndroidApp.Context!.RegisterReceiver(_stateReceiver, new IntentFilter(BluetoothAdapter.ActionStateChanged));
    }

    private void OnAdapterState(State state)
    {
        switch (state)
        {
            // Released here rather than at STATE_OFF: by then the stack is already going and the
            // release is exactly what the framework is waiting on.
            case State.TurningOff:
            case State.Off:
                if (_gattServer is null && _gatt is null && _advertiser is null) return;
                L("Bluetooth is going off — releasing the radio");
                ReleaseRadio();
                OnLinkLost("Bluetooth was switched off");
                break;

            case State.On:
                L("Bluetooth is back — listening again");
                if (!_disposed) Link();
                break;
        }
    }

    private sealed class StateReceiver(AndroidBleTransportService o) : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent?.Action != BluetoothAdapter.ActionStateChanged) return;
            o.OnAdapterState((State)intent.GetIntExtra(BluetoothAdapter.ExtraState, (int)State.Off));
        }
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
            else if (newState == ProfileState.Disconnected) o.OnLinkLost($"central disconnected, status={status}");
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
            // Deliberately not proof of life: this completion comes from the peer's Bluetooth
            // controller, not from its app. Both were seen succeeding on a link where nothing reached
            // either app — see LinkLiveness.
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
            // Answer FIRST, then do the work — and do the work on another thread.
            //
            // A write-with-response does not complete on the far side until this response is sent, and
            // Android allows one GATT operation in flight per connection: while a write is outstanding
            // it refuses every other. So whatever runs before SendResponse is time the PEER cannot
            // send anything at all.
            //
            // OnServerWrite reassembles the packet, decrypts it, dispatches it, and ends in
            // AudioTrack.Write, which blocks until the speaker takes the audio. Doing that first meant
            // playing inbound audio physically stalled the other phone's outbound queue. Measured on a
            // live call: the central got 13 frames away and then every single write for the rest of the
            // call was refused, while its peer — which only notifies, and never waits for a response —
            // sent 500 with not one refusal.
            if (responseNeeded)
                o._gattServer?.SendResponse(device, requestId, GattStatus.Success, offset, value);

            if (device is not null && value is not null)
            {
                var from = device;
                var payload = value;
                _ = Task.Run(() => { try { o.OnServerWrite(from, payload); } catch { /* never kill the radio thread */ } });
            }
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
            // Also not proof of life, for the same reason as OnCharacteristicWrite.
            o.OnFrameSent();   // a notify was flushed — release the next fragment
        }

        public override void OnMtuChanged(BluetoothDevice? device, int mtu)
        {
            o._mtu = mtu;
        }

        public override void OnConnectionStateChange(BluetoothDevice? device, ProfileState status, ProfileState newState)
        {
            if (device is not null && newState == ProfileState.Connected) o._peripheralPeer = device;
            else if (newState == ProfileState.Disconnected) o.OnLinkLost("peer disconnected from our GATT server");
        }
    }
}
#endif

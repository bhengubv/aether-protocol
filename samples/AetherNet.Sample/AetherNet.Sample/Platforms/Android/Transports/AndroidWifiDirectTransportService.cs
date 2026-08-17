// SPDX-License-Identifier: MIT
#if ANDROID
using Android.Content;
using Android.Net.Wifi.P2p;
using Android.Net.Wifi.P2p.Nsd;
using Android.OS;
using AetherNet.Transport.Abstractions;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace AetherNet.Sample.Platforms.Android.Transports;

/// <summary>
/// Real Wi-Fi Direct (Wi-Fi P2P) transport for AetherNet on Android — a native C#
/// <see cref="ITransportService"/>, not a bridge to any other app. One APK: this radio
/// ships inside the AetherNet app itself.
///
/// Flow: discover peers → the higher-addressed device deterministically calls connect()
/// (so both sides don't race) → the framework forms a group and elects a Group Owner →
/// GO opens a TCP server on 8888, the client dials it → a one-line UHID handshake tells
/// each side who the peer is → thereafter every frame is [4-byte LE length][payload] and
/// surfaces on <see cref="DataReceived"/> as (peerUhid, bytes).
/// </summary>
public sealed class AndroidWifiDirectTransportService : ITransportService, IRadio, IDisposable
{
    private const int TcpPort = 8888;
    private const string ServiceInstance = "aethernet";
    private const string ServiceType = "_aethernet._tcp";

    private readonly Context _context;
    private readonly string _localUhid;
    private readonly byte[] _routingKey;
    private readonly ILogger _logger;

    private readonly WifiP2pManager? _manager;
    private WifiP2pManager.Channel? _channel;
    private Receiver? _receiver;

    /// <summary>
    /// How long a connect() attempt may sit before we assume it will never complete and try again.
    /// Android reports connect() as "succeeded" the moment the invitation is sent, so a peer that
    /// never answers leaves the attempt outstanding with no callback of any kind.
    /// </summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How often the search is re-issued — <b>often</b>, and a different interval on every phone.
    ///
    /// <para>
    /// A find is short-lived. It runs a bounded scan/listen cycle and stops, after which the phone is
    /// neither looking nor findable. So the search has to be restarted briskly to stay discoverable at
    /// all: measured on these two phones, restarting every few seconds found the other handset within
    /// seconds, and backing off to twenty left them both idle — two minutes side by side, both radios
    /// healthy, each repeatedly finding the same printer and never each other.
    /// </para>
    ///
    /// <para>
    /// (A printer is an autonomous group owner and beacons continuously, so it turns up in any brief
    /// scan. That is exactly why it kept appearing while the phones did not, and why its presence in
    /// the log is not evidence that discovery is working.)
    /// </para>
    ///
    /// <para>
    /// The per-device skew stays, so two phones do not scan and listen in lockstep — but it is small,
    /// because the restart itself is what keeps each phone visible.
    /// </para>
    /// </summary>
    private TimeSpan FindLifetime => TimeSpan.FromSeconds(3) + _findSkew;

    private readonly TimeSpan _findSkew;

    /// <summary>When the framework last told us anything about peers — proof the search is alive.</summary>
    private DateTime _lastPeerNewsUtc = DateTime.MinValue;

    /// <summary>Set once, so a second <see cref="Link"/> cannot start a second rediscovery loop.</summary>
    private int _linkStarted;

    private string _thisDeviceAddress = string.Empty;
    private volatile bool _connecting;
    private DateTime _connectStartedUtc;
    private DateTime _nextConnectUtc = DateTime.MinValue;
    private volatile bool _groupFormed;
    private volatile bool _disposed;
    private Role _role = Role.None;

    private enum Role { None, Host, Join }

    // Live peer links, keyed by the peer UHID learned from the handshake.
    private readonly ConcurrentDictionary<string, PeerLink> _peers = new(StringComparer.Ordinal);
    private TcpListener? _server;

    public AndroidWifiDirectTransportService(Context context, string localUhid, ILogger logger, byte[]? routingKey = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _localUhid = localUhid ?? throw new ArgumentNullException(nameof(localUhid));
        // Must come from the identity secret. The tag is public, so an address derived from it could
        // be computed by anyone holding it — private in appearance only.
        _routingKey = routingKey ?? throw new ArgumentNullException(nameof(routingKey),
            "A rotating wire address needs a key derived from the identity secret, not the public tag.");
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _manager = context.GetSystemService(Context.WifiP2pService) as WifiP2pManager;
        _findSkew = SkewFor(_localUhid);
    }

    /// <summary>
    /// A delay this phone will always choose and another phone almost certainly won't — derived from
    /// its own id rather than from a clock or a random source, so it is stable across restarts and
    /// still differs between any two devices. Android masks the local MAC, so the id is what we have.
    /// </summary>
    private static TimeSpan SkewFor(string uhid)
    {
        var hash = 0;
        foreach (var c in uhid) hash = unchecked(hash * 31 + c);
        return TimeSpan.FromMilliseconds(Math.Abs(hash % 2500));
    }

    // ── ITransportService metadata ───────────────────────────────────────────
    public string Name => "Wi-Fi Direct";

    /// <summary>
    /// Available means <b>this radio can actually carry traffic right now</b> — not merely that the
    /// silicon is present.
    ///
    /// <para>
    /// Found on merlin, 2026-08-16: with the hardware present and Wi-Fi on, every
    /// <c>addLocalService</c> and <c>discoverServices</c> came back <c>reason=0</c> — the framework's
    /// bare "internal error", which is what it returns when the app lacks location permission. It
    /// never mentions permissions. So the chip read as available, the user picked it, and nothing
    /// happened, forever, with the only clue buried in logcat.
    /// </para>
    ///
    /// <para>
    /// A radio that reports itself available and then does nothing is worse than one that reports
    /// itself unavailable, because there is nothing for the person holding the phone to do about it.
    /// </para>
    /// </summary>
    public bool IsAvailable =>
        HasFeature && _manager is not null && !_disposed && Blocker is null;

    /// <inheritdoc />
    public string? UnavailableReason =>
        !HasFeature ? "this phone has no Wi-Fi Direct"
        : _manager is null ? "Wi-Fi Direct is unavailable"
        : Blocker;

    /// <inheritdoc />
    /// <remarks>The hardware being absent is final; a permission or a settings toggle is not.</remarks>
    public bool IsFixable => HasFeature && _manager is not null && Blocker is not null;

    /// <summary>
    /// What is stopping this radio from working, in the words of someone holding the phone — or null
    /// when nothing is.
    /// </summary>
    private static string? Blocker
    {
        get
        {
            if (!HasPermission)
                return "needs permission to find phones nearby";

            // Below API 33 the discovery stack returns nothing at all unless Location is switched on
            // — not an error, just silence, which is the hardest kind of failure to find.
            if (global::Android.OS.Build.VERSION.SdkInt < global::Android.OS.BuildVersionCodes.Tiramisu &&
                !LocationServicesOn)
                return "Android needs Location switched on to find phones over Wi-Fi";

            return null;
        }
    }

    /// <summary>
    /// The permission Wi-Fi Direct discovery needs. Android 13 introduced <c>NEARBY_WIFI_DEVICES</c>
    /// precisely so that finding a phone next to you stops meaning "may track where you are"; before
    /// that the only way to ask was fine location.
    /// </summary>
    private static bool HasPermission =>
        AndroidX.Core.Content.ContextCompat.CheckSelfPermission(
            global::Android.App.Application.Context,
            global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.Tiramisu
                ? global::Android.Manifest.Permission.NearbyWifiDevices
                : global::Android.Manifest.Permission.AccessFineLocation)
        == global::Android.Content.PM.Permission.Granted;

    private static bool LocationServicesOn =>
        global::Android.App.Application.Context.GetSystemService(Context.LocationService)
            is global::Android.Locations.LocationManager m &&
        (m.IsProviderEnabled(global::Android.Locations.LocationManager.GpsProvider) ||
         m.IsProviderEnabled(global::Android.Locations.LocationManager.NetworkProvider));

    private static bool HasFeature =>
        global::Android.App.Application.Context.PackageManager?
            .HasSystemFeature(global::Android.Content.PM.PackageManager.FeatureWifiDirect) == true;
    public long MaxBandwidthBps => 250_000_000;   // ~250 Mbps practical
    public int MaxRangeMeters => 200;
    public int PowerCostRelative => 6;             // higher than BLE, lower than cellular
    public int MaxConcurrentPeers => 8;

    public event Action<string, byte[]>? DataReceived;

    /// <summary>Raised with the peer UHID once a peer completes the handshake and is linked.</summary>
    public event Action<string>? PeerLinked;

    /// <summary>Raised with a human-readable status line for the UI's radio log.</summary>
    public event Action<string>? Status;

    /// <summary>
    /// This node''s address for the current epoch. A rotating id, not an identity: unlinkable across
    /// epochs to anyone without the routing key, so a scanner sitting in a room cannot build a list
    /// of who was there.
    /// </summary>
    private string CurrentAddress() =>
        AetherNet.Identity.EphemeralRoutingId.Derive(_routingKey, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void EnsureInitialized()
    {
        if (_channel is not null || _manager is null) return;
        _channel = _manager.Initialize(_context, Looper.MainLooper, null);
        _receiver = new Receiver(this);
        var filter = new IntentFilter();
        filter.AddAction(WifiP2pManager.WifiP2pStateChangedAction);
        filter.AddAction(WifiP2pManager.WifiP2pPeersChangedAction);
        filter.AddAction(WifiP2pManager.WifiP2pConnectionChangedAction);
        filter.AddAction(WifiP2pManager.WifiP2pThisDeviceChangedAction);
        _context.RegisterReceiver(_receiver, filter);
        L("radio initialised");
    }

    /// <summary>
    /// Bring the radio up, advertise the AetherNet DNS-SD service, and discover it. When a peer
    /// advertising our service is found (never a printer/router), connect(). Both phones do
    /// exactly this, so they mutually invite each other and the framework forms a group with no
    /// system dialog and no wrong-device pairing — and no fragile autonomous Group Owner.
    /// </summary>
    public void Link()
    {
        if (_manager is null) { L("Wi-Fi Direct unavailable on this device"); return; }

        // Say why, rather than calling into a framework that will refuse every request with a bare
        // "internal error" and leave the radio looking merely broken.
        if (Blocker is { } blocker) { L($"can't link — {blocker}"); return; }

        // Linking twice is not linking harder. Every call used to start another rediscovery loop, and
        // two loops restart the find twice as often — which is how the app ended up interrupting its
        // own search before it could finish. Picking the radio and then tapping Connect was enough to
        // do it.
        if (Interlocked.Exchange(ref _linkStarted, 1) == 1)
        {
            L("already linking");
            return;
        }

        EnsureInitialized();
        LeaveAnyStaleGroup();
        L("linking — advertising + discovering the AetherNet service…");
        Advertise();
        StartFinding();
        _ = Task.Run(RediscoverLoopAsync);
    }

    /// <summary>
    /// Announce this phone as an AetherNet node. Re-announced after a group goes away, because
    /// tearing a group down resets the P2P interface — the device address goes to
    /// <c>02:00:00:00:00:00</c> on the way out — and a registration made against the old one is gone
    /// with it.
    /// </summary>
    private void Advertise()
    {
        if (_manager is null || _channel is null) return;

        // Announced to everyone scanning, so it carries the rotating address — never the AetherTag.
        var record = new Dictionary<string, string> { ["id"] = CurrentAddress() };
        var info = WifiP2pDnsSdServiceInfo.NewInstance(ServiceInstance, ServiceType, record);
        _manager.AddLocalService(_channel, info, new ActionListener("addLocalService", _logger,
            onFailure: r => L($"addLocalService failed reason={r} — this phone will not be findable")));
        _manager.SetDnsSdResponseListeners(_channel, new ServiceResponseListener(this), new TxtRecordListener());
        _manager.AddServiceRequest(_channel, WifiP2pDnsSdServiceRequest.NewInstance(),
            new ActionListener("addServiceRequest", _logger));
    }

    /// <summary>
    /// Start looking — for peers <b>and</b> for our service.
    ///
    /// <para>
    /// Both matter, and only one was being asked for. <c>discoverServices()</c> was doing all the work
    /// on the P30, whose framework starts a peer scan underneath it; on merlin it never surfaced the
    /// other phone at all, so merlin could see a printer beaconing away and never its own peer. The
    /// two phones then sat in a one-sided invitation: the P30 called connect(), merlin went to
    /// <c>Invited</c>, and nothing on merlin knew there was anyone to answer.
    /// </para>
    ///
    /// <para>
    /// <c>discoverPeers()</c> is the call that makes a phone both look and <b>be findable</b>, so it is
    /// asked for explicitly rather than hoped for as a side effect.
    /// </para>
    /// </summary>
    private void StartFinding()
    {
        if (_manager is null || _channel is null) return;

        _manager.DiscoverPeers(_channel, new ActionListener("discoverPeers", _logger,
            onFailure: r => L($"discoverPeers failed reason={r}")));
        _manager.DiscoverServices(_channel, new ActionListener("discoverServices", _logger,
            onFailure: r => L($"discoverServices failed reason={r}")));
    }

    /// <summary>
    /// Walk out of any Wi-Fi Direct group this phone is still in before trying to form a new one.
    /// <para>
    /// A group survives the app that made it. Both test phones were sitting in leftover groups from
    /// earlier runs, which is why each saw the other as <c>Connected</c> rather than <c>Available</c>
    /// — and a peer that already belongs to a group cannot be invited into another. Discovery looked
    /// perfectly healthy the whole time; there was simply nobody free to invite.
    /// </para>
    /// <para>
    /// Any outstanding invitation is cancelled for the same reason: a half-finished connect leaves
    /// the framework unwilling to start a new one.
    /// </para>
    /// </summary>
    private void LeaveAnyStaleGroup()
    {
        if (_manager is null || _channel is null) return;

        _manager.CancelConnect(_channel, new ActionListener("cancelConnect", _logger, onFailure: _ => { }));
        _manager.RequestGroupInfo(_channel, new GroupInfoListener(group =>
        {
            if (group is null) return;
            L($"leaving a leftover group ({group.NetworkName}) so this phone can be invited again");
            _manager.RemoveGroup(_channel, new ActionListener("removeGroup", _logger, onFailure: _ => { }));
        }));
    }

    private sealed class GroupInfoListener(Action<WifiP2pGroup?> onGroup)
        : Java.Lang.Object, WifiP2pManager.IGroupInfoListener
    {
        public void OnGroupInfoAvailable(WifiP2pGroup? group) => onGroup(group);
    }

    /// <summary>
    /// Keep looking, but only when looking has stopped working.
    ///
    /// <para>
    /// Discovery is one-shot: it runs a scan/listen cycle and stops. So it has to be restarted — but
    /// restarting it is also how you <b>cancel</b> a cycle that was halfway through finding someone. A
    /// Wi-Fi Direct find takes appreciably longer than the few seconds this loop used to allow, and
    /// with two loops running it fired about every three seconds, which meant neither phone was ever
    /// discoverable for a whole cycle. Two phones a foot apart never saw each other.
    /// </para>
    ///
    /// <para>
    /// So: restart only after <see cref="FindLifetime"/> of genuine silence. Any peer broadcast at all
    /// counts as the search still being alive and resets the clock.
    /// </para>
    /// </summary>
    private async Task RediscoverLoopAsync()
    {
        // Restarted whenever a group goes away, so it has to be safe to call while already running —
        // two loops search twice as often and cancel each other's finds.
        if (Interlocked.Exchange(ref _findLoopRunning, 1) == 1) return;

        try
        {
            while (!_disposed && !_groupFormed)
            {
                await Task.Delay(1000).ConfigureAwait(false);
                if (_disposed || _groupFormed || _manager is null || _channel is null) break;

                // Never search while an invitation is in flight — restarting discovery is precisely
                // what makes Android reject the connect with BUSY.
                if (_connecting) continue;
                if (DateTime.UtcNow - _lastPeerNewsUtc < FindLifetime) continue;

                L($"nothing seen for {FindLifetime.TotalSeconds:0.0}s — starting a new search");
                _lastPeerNewsUtc = DateTime.UtcNow;
                try { StartFinding(); }
                catch (Exception ex) { _logger.LogDebug(ex, "re-discover error"); }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _findLoopRunning, 0);
        }
    }

    private int _findLoopRunning;

    /// <summary>
    /// Claim the right to start a connect attempt. Only one may be outstanding at a time, but an
    /// attempt that produced no outcome must not block every future one: Android calls connect()
    /// "successful" as soon as the invitation is sent, so if the peer never answers there is no
    /// callback at all and the flag would otherwise stay set for the life of the app — which is
    /// exactly why discovery kept reporting the peer while nothing ever tried to connect again.
    /// </summary>
    private bool TryBeginConnect()
    {
        lock (_peers)
        {
            if (DateTime.UtcNow < _nextConnectUtc) return false;      // backing off after a collision
            if (_connecting)
            {
                if (DateTime.UtcNow - _connectStartedUtc < ConnectTimeout) return false;
                L($"previous connect() went unanswered after {ConnectTimeout.TotalSeconds:0}s — retrying");
            }
            _connecting = true;
            _connectStartedUtc = DateTime.UtcNow;
            return true;
        }
    }

    /// <summary>
    /// Both phones invite at once and one of them gets BUSY, every time, because they are doing the
    /// same thing at the same moment. Back off by an amount derived from this device's own tag: the
    /// delay is fixed for a given phone but different between any two, so the symmetry breaks on its
    /// own without needing a MAC address — which Android masks anyway.
    /// </summary>
    /// <summary>
    /// Invite a peer — <b>without stopping the search first</b>.
    ///
    /// <para>
    /// This code used to call <c>stopPeerDiscovery()</c> before connecting, on the reasoning that
    /// Android refuses connect() while a find is running. It does not, and the stop was the thing
    /// breaking it. <c>WifiP2pService</c> validates the invitation against its own peer list:
    /// </para>
    /// <code>
    /// if (isConfigInvalid(config)) { loge("Dropping connect request " + config); … }
    /// // isConfigInvalid → true when mPeers.get(config.deviceAddress) == null
    /// </code>
    /// <para>
    /// and stopping the find <b>empties that list</b>. Our own log said so plainly and it took a while
    /// to read: <c>stopPeerDiscovery ok</c> was always followed by <c>0 peer(s) discovered</c>. So we
    /// were deleting the peer and then asking to connect to it, and being told <c>reason=0</c> —
    /// Android's anonymous "internal error", which here meant "who?".
    /// </para>
    /// <para>
    /// The framework stops the find itself as part of connecting. Ours was never needed, and racing it
    /// was the only reason a connection ever succeeded at all.
    /// </para>
    /// </summary>
    private void ConnectTo(string deviceAddress)
    {
        if (_manager is null || _channel is null || _groupFormed || _disposed) return;

        L($"inviting {deviceAddress}");
        var config = new WifiP2pConfig { DeviceAddress = deviceAddress };
        _manager.Connect(_channel, config, new ActionListener("connect", _logger,
            onFailure: r => BackOffAfterCollision($"failed reason={r}")));
    }

    private void BackOffAfterCollision(string reason)
    {
        var hash = 0;
        foreach (var c in _localUhid) hash = unchecked(hash * 31 + c);
        var wait = TimeSpan.FromMilliseconds(1500 + Math.Abs(hash % 4000));

        lock (_peers)
        {
            _connecting = false;
            _nextConnectUtc = DateTime.UtcNow + wait;
        }

        // Stopping the search to invite has emptied the peer list, so there is nothing left to retry
        // against. Treat the search as dead rather than waiting out a silence timer that is only
        // measuring our own teardown.
        _lastPeerNewsUtc = DateTime.MinValue;
        L($"connect() {reason} — standing off {wait.TotalMilliseconds:0}ms so we are not both calling at once");
    }

    /// <summary>
    /// Is this peer a phone rather than some other Wi-Fi Direct gadget?
    /// <para>
    /// The primary device type is a WPS triple like <c>10-0050F204-5</c>; category 10 is Telephone,
    /// 3 is Printer. Filtering on it is what stops us pairing with the office printer — the exact
    /// thing that happened before DNS-SD was introduced, and which we still have to avoid now that
    /// DNS-SD has turned out not to work on these phones.
    /// </para>
    /// </summary>
    private static bool IsPhone(WifiP2pDevice d)
    {
        var type = d.PrimaryDeviceType;
        if (string.IsNullOrEmpty(type)) return true;   // unknown — let the handshake decide
        return type.StartsWith("10-", StringComparison.Ordinal);
    }

    /// <summary>A joiner found a peer advertising our service — connect to that one only.</summary>
    private void OnServiceFound(string? instanceName, string? registrationType, WifiP2pDevice? src)
    {
        L($"service seen: {instanceName} / {registrationType} @ {src?.DeviceAddress}");
        if (_groupFormed || src is null || _manager is null || _channel is null) return;
        if (registrationType?.Contains("aethernet", StringComparison.OrdinalIgnoreCase) != true &&
            instanceName?.Contains("aethernet", StringComparison.OrdinalIgnoreCase) != true) return;
        if (!TryBeginConnect()) return;

        L($"connecting to AetherNet peer {src.DeviceAddress}");
        ConnectTo(src.DeviceAddress!);
    }

    public void Discover()
    {
        if (_manager is null || _channel is null) return;
        Status?.Invoke("Discovering Wi-Fi Direct peers…");
        _manager.DiscoverPeers(_channel, new ActionListener("discoverPeers", _logger));
    }

    // ── Broadcast handling ─────────────────────────────────────────────────────

    /// <summary>Trace to logcat (tag AetherWFD), the ILogger, and the UI status line at once.</summary>
    private void L(string m)
    {
        global::Android.Util.Log.Info("AetherWFD", m);
        _logger.LogInformation("{Msg}", m);
        Status?.Invoke(m);
    }

    private void OnPeersChanged()
    {
        if (_manager is null || _channel is null) return;

        // The framework is still talking to us, so the search is alive — leave it running.
        _lastPeerNewsUtc = DateTime.UtcNow;

        _manager.RequestPeers(_channel, new PeerListListener(list =>
        {
            L($"{list.DeviceList.Count} peer(s) discovered");
            foreach (var d in list.DeviceList)
                global::Android.Util.Log.Info("AetherWFD",
                    $"  peer {d.DeviceAddress} status={d.Status} type={d.PrimaryDeviceType}");

            // DNS-SD is the precise way to find our own app, but on these phones the service
            // responses never arrive — both sides discover each other as plain peers and no service
            // callback ever fires, so a group could never form. Fall back to the peer list, filtered
            // to phones, which keeps the original reason for using DNS-SD: never dialling the
            // office printer that also advertises itself over Wi-Fi Direct.
            MaybeConnect(list);
        }));
    }

    private void OnThisDeviceChanged(WifiP2pDevice? self)
    {
        if (self?.DeviceAddress is { } addr) { _thisDeviceAddress = addr; L($"this device address = {addr}"); }
    }

    private void OnConnectionChanged(WifiP2pInfo? info)
    {
        L($"connection changed: groupFormed={info?.GroupFormed} isGO={info?.IsGroupOwner}");
        if (info is null || !info.GroupFormed)
        {
            // The invitation came to nothing. Release the latch immediately rather than waiting out
            // the timeout, so the next service sighting can try again.
            _connecting = false;
            if (_groupFormed) OnGroupLost();
            return;
        }
        _groupFormed = true;
        _connecting = false;
        if (info.IsGroupOwner)
        {
            L("role: Group Owner — starting TCP server");
            _ = Task.Run(RunServerAsync);
        }
        else
        {
            var go = info.GroupOwnerAddress?.HostAddress ?? "192.168.49.1";
            L($"role: Client — dialing GO {go}");
            _ = Task.Run(() => RunClientAsync(go));
        }
    }

    /// <summary>
    /// The group we were in has gone. Put the radio back to how it was before it formed.
    ///
    /// <para>
    /// This was missing entirely, and it is why a single bad group formation ended the radio for the
    /// rest of the app's life. <see cref="_groupFormed"/> was set when the group came up and never
    /// cleared, and the search loop runs <c>while (!_groupFormed)</c> — so the moment a group formed
    /// and collapsed, this phone stopped looking for anyone, permanently and silently. Watched on
    /// device 2026-08-17: the group formed, the TCP server started, it was gone 160ms later, and
    /// neither phone ever searched again. The radio looked idle because it <b>was</b> idle.
    /// </para>
    ///
    /// <para>
    /// A group collapsing is ordinary — the other phone walks away, sleeps, or the negotiation loses a
    /// race. It has to be survivable, not terminal.
    /// </para>
    /// </summary>
    private void OnGroupLost()
    {
        _groupFormed = false;

        try { _server?.Stop(); } catch { }
        _server = null;

        foreach (var link in _peers.Values)
            try { link.Client.Dispose(); } catch { }
        _peers.Clear();

        // Nothing was heard because nothing was listening. Start looking immediately rather than
        // waiting out a silence timer that would only be measuring our own teardown.
        _lastPeerNewsUtc = DateTime.MinValue;
        L("the group went away — looking again");

        if (_disposed) return;
        Advertise();
        StartFinding();
        _ = Task.Run(RediscoverLoopAsync);
    }

    /// <summary>
    /// Both sides call connect() to the discovered peer; Android's framework negotiates a single
    /// group and elects the Group Owner. (The local device address is privacy-masked on modern
    /// Android, so a "who initiates" rule based on it can't work — both initiating is the robust path.)
    /// </summary>
    private void MaybeConnect(WifiP2pDeviceList list)
    {
        if (_groupFormed || _manager is null || _channel is null) return;

        // Available: free to be invited. Invited: an invitation is already in flight between us, and
        // calling connect() from this side is what completes it — Wi-Fi Direct forms the group when
        // both ends have asked. Treating Invited as "busy, leave it alone" is why the two phones sat
        // staring at each other: one had invited, the other never answered, and it never timed out.
        WifiP2pDevice? target = null;
        foreach (var d in list.DeviceList)
        {
            if (d.Status is not (WifiP2pDeviceState.Available or WifiP2pDeviceState.Invited)) continue;
            if (!IsPhone(d)) continue;                                // printers advertise here too
            target = d;
            break;
        }
        if (target is null) return;
        if (!TryBeginConnect()) return;

        L($"initiating connect() to {target.DeviceAddress}");
        ConnectTo(target.DeviceAddress!);
    }

    // ── Sockets ────────────────────────────────────────────────────────────────

    private async Task RunServerAsync()
    {
        try
        {
            _server = new TcpListener(IPAddress.Any, TcpPort);
            _server.Start();
            L($"GO: TCP server listening on {TcpPort}");
            while (!_disposed)
            {
                var client = await _server.AcceptTcpClientAsync().ConfigureAwait(false);
                _ = Task.Run(() => HandleSocketAsync(client));
            }
        }
        catch (Exception ex) when (!_disposed) { _logger.LogError(ex, "Wi-Fi Direct server error"); }
    }

    private async Task RunClientAsync(string goAddress)
    {
        try
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Parse(goAddress), TcpPort).ConfigureAwait(false);
            L("client: TCP connected to GO");
            await HandleSocketAsync(client).ConfigureAwait(false);
        }
        catch (Exception ex) when (!_disposed) { _logger.LogError(ex, "Wi-Fi Direct client connect failed"); }
    }

    private async Task HandleSocketAsync(TcpClient client)
    {
        var stream = client.GetStream();
        // Handshake: announce our rotating address. Who we are arrives later, inside the session.
        await WriteFrameAsync(stream, System.Text.Encoding.UTF8.GetBytes("ERID:" + CurrentAddress())).ConfigureAwait(false);
        string? peerUhid = null;
        try
        {
            while (!_disposed)
            {
                var frame = await ReadFrameAsync(stream).ConfigureAwait(false);
                if (frame is null) break;
                if (peerUhid is null && frame.Length > 5 &&
                    System.Text.Encoding.UTF8.GetString(frame, 0, 5) == "ERID:")
                {
                    peerUhid = System.Text.Encoding.UTF8.GetString(frame, 5, frame.Length - 5);
                    _peers[peerUhid] = new PeerLink(client, stream);
                    L($"linked with {peerUhid}");
                    PeerLinked?.Invoke(peerUhid);
                    continue;
                }
                if (peerUhid is not null) DataReceived?.Invoke(peerUhid, frame);
            }
        }
        catch (Exception ex) when (!_disposed) { _logger.LogDebug(ex, "Wi-Fi Direct socket closed"); }
        finally
        {
            if (peerUhid is not null) _peers.TryRemove(peerUhid, out _);
            client.Dispose();
        }
    }

    // ── ITransportService send/query ────────────────────────────────────────────

    public async Task<bool> SendAsync(string peerUhid, byte[] data, CancellationToken cancellationToken = default)
    {
        if (!_peers.TryGetValue(peerUhid, out var link)) return false;
        try { await WriteFrameAsync(link.Stream, data).ConfigureAwait(false); return true; }
        catch (Exception ex) { _logger.LogDebug(ex, "Wi-Fi Direct send failed to {Uhid}", peerUhid); return false; }
    }

    public async Task<bool> SendStreamAsync(string peerUhid, Stream stream, CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        return await SendAsync(peerUhid, ms.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    public bool IsConnected(string peerUhid) => _peers.ContainsKey(peerUhid);

    /// <summary>UHIDs of peers currently linked over this radio.</summary>
    public IReadOnlyCollection<string> ConnectedPeers => _peers.Keys.ToArray();

    // ── IRadio ──────────────────────────────────────────────────────────────────
    public bool IsLinked => !_peers.IsEmpty;
    public string? PeerTag => _peers.Keys.FirstOrDefault();
    public Task<bool> SendAsync(byte[] data)
        => PeerTag is { } p ? SendAsync(p, data) : Task.FromResult(false);
    public void Stop() => Dispose();

    // ── Framing: [4-byte little-endian length][payload] ─────────────────────────

    private static async Task WriteFrameAsync(NetworkStream s, byte[] payload)
    {
        var header = new byte[4];
        BitConverter.TryWriteBytes(header, payload.Length); // little-endian on all supported platforms
        await s.WriteAsync(header).ConfigureAwait(false);
        await s.WriteAsync(payload).ConfigureAwait(false);
        await s.FlushAsync().ConfigureAwait(false);
    }

    private static async Task<byte[]?> ReadFrameAsync(NetworkStream s)
    {
        var header = await ReadExactAsync(s, 4).ConfigureAwait(false);
        if (header is null) return null;
        var len = BitConverter.ToInt32(header, 0);
        if (len <= 0 || len > 64 * 1024 * 1024) return null;
        return await ReadExactAsync(s, len).ConfigureAwait(false);
    }

    private static async Task<byte[]?> ReadExactAsync(NetworkStream s, int count)
    {
        var buf = new byte[count];
        var off = 0;
        while (off < count)
        {
            var n = await s.ReadAsync(buf.AsMemory(off, count - off)).ConfigureAwait(false);
            if (n <= 0) return null;
            off += n;
        }
        return buf;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { if (_receiver is not null) _context.UnregisterReceiver(_receiver); } catch { }
        try { _server?.Stop(); } catch { }
        foreach (var link in _peers.Values) link.Client.Dispose();
        _peers.Clear();
        if (_manager is not null && _channel is not null)
            _manager.RemoveGroup(_channel, null);
    }

    private sealed record PeerLink(TcpClient Client, NetworkStream Stream);

    // ── Android listener/receiver adapters ──────────────────────────────────────

    private sealed class Receiver(AndroidWifiDirectTransportService owner) : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            switch (intent?.Action)
            {
                case WifiP2pManager.WifiP2pPeersChangedAction:
                    owner.OnPeersChanged();
                    break;
                case WifiP2pManager.WifiP2pConnectionChangedAction:
                    owner.OnConnectionChanged(
                        intent.GetParcelableExtra(WifiP2pManager.ExtraWifiP2pInfo) as WifiP2pInfo);
                    break;
                case WifiP2pManager.WifiP2pThisDeviceChangedAction:
                    owner.OnThisDeviceChanged(
                        intent.GetParcelableExtra(WifiP2pManager.ExtraWifiP2pDevice) as WifiP2pDevice);
                    break;
            }
        }
    }

    private sealed class ActionListener(
        string op, ILogger logger, Action<int>? onFailure = null, Action? onSuccess = null)
        : Java.Lang.Object, WifiP2pManager.IActionListener
    {
        public void OnSuccess()
        {
            global::Android.Util.Log.Info("AetherWFD", $"{op} ok");
            onSuccess?.Invoke();
        }

        public void OnFailure(WifiP2pFailureReason reason)
        {
            global::Android.Util.Log.Warn("AetherWFD", $"{op} failed ({reason})");
            onFailure?.Invoke((int)reason);
        }
    }

    private sealed class PeerListListener(Action<WifiP2pDeviceList> onPeers)
        : Java.Lang.Object, WifiP2pManager.IPeerListListener
    {
        public void OnPeersAvailable(WifiP2pDeviceList peers) => onPeers(peers);
    }

    private sealed class ServiceResponseListener(AndroidWifiDirectTransportService owner)
        : Java.Lang.Object, WifiP2pManager.IDnsSdServiceResponseListener
    {
        public void OnDnsSdServiceAvailable(string? instanceName, string? registrationType, WifiP2pDevice? srcDevice)
            => owner.OnServiceFound(instanceName, registrationType, srcDevice);
    }

    private sealed class TxtRecordListener : Java.Lang.Object, WifiP2pManager.IDnsSdTxtRecordListener
    {
        public void OnDnsSdTxtRecordAvailable(string? fullDomainName,
            IDictionary<string, string>? txtRecordMap, WifiP2pDevice? srcDevice) { }
    }
}
#endif

// SPDX-License-Identifier: MIT
#if ANDROID
using Android.Content;
using Android.Net.Wifi.P2p;
using Android.Net.Wifi.P2p.Nsd;
using Android.OS;
using AetherNet.Sample.Shared.Services;
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
public sealed class AndroidWifiDirectTransportService
    : ITransportService, IRadio, AetherNet.Sample.Shared.Services.IWifiDirectGroup, IDisposable
{
    /// <inheritdoc />
    bool AetherNet.Sample.Shared.Services.IWifiDirectGroup.IsSupported => IsAvailable;

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
    // A DNS-SD cycle takes far longer than a peer scan — the responses come back well after the first
    // peers do. Three seconds restarted the search before a single service response could land, which
    // is why the peer list was the only thing this radio ever saw.


    /// <summary>Who this phone already knows. Nobody else is ever dialled.</summary>
    private readonly AetherNet.Sample.Shared.Services.CircleDirectory? _circle;

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

    public AndroidWifiDirectTransportService(Context context, string localUhid, ILogger logger,
        byte[]? routingKey = null, AetherNet.Sample.Shared.Services.CircleDirectory? circle = null)
    {
        _circle = circle;
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _localUhid = localUhid ?? throw new ArgumentNullException(nameof(localUhid));
        // Must come from the identity secret. The tag is public, so an address derived from it could
        // be computed by anyone holding it — private in appearance only.
        _routingKey = routingKey ?? throw new ArgumentNullException(nameof(routingKey),
            "A rotating wire address needs a key derived from the identity secret, not the public tag.");
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _manager = context.GetSystemService(Context.WifiP2pService) as WifiP2pManager;
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

    /// <summary>The group is gone. Whoever brought it up decides whether to bring it back.</summary>
    public event Action? GroupLost;

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

        // Nothing is discovered and nothing is advertised. The group's name and passphrase are worked
        // out from the contact list by FastRadioService — both phones reach the same answer with
        // nothing passing between them — so all this has to do is make sure the radio is awake and not
        // still sitting in a group from a previous run.
        //
        // Four measured attempts went the other way first. Service discovery never completed between
        // these handsets: both answered queries and neither received the reply, because both ends
        // free-run p2p_find and every query landed while the other was mid-search. The supplicant said
        // so plainly — "Service Discovery Query TX callback: success=0". Sending the credentials over
        // the link instead only moved the problem, since they needed the radio they were for.
        EnsureInitialized();
        LeaveAnyStaleGroup();
        L("radio up — the group comes from the Circle, not from a search");
    }

    public async Task<WifiDirectCredentials?> HostAsync(CancellationToken cancellationToken = default) =>
        await HostAsync(null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Create the group, using the credentials the whole Circle can already work out.
    /// </summary>
    /// <param name="wanted">
    ///   The name and passphrase to host under, or null to let the framework choose.
    ///   <para>
    ///   Choosing them is what removes discovery from the problem entirely. When the framework picks,
    ///   the credentials exist only on this phone and have to reach the others somehow — which needs a
    ///   radio, which is the thing being brought up. Naming them ourselves means the other phones
    ///   derived the same answer before anybody switched a radio on.
    ///   </para>
    /// </param>
    public async Task<WifiDirectCredentials?> HostAsync(WifiDirectCredentials? wanted,
        CancellationToken cancellationToken = default)
    {
        if (_manager is null) return null;
        if (Blocker is { } blocker) { L($"can't host — {blocker}"); return null; }

        EnsureInitialized();
        if (_channel is null) return null;

        // A group already open here is the wrong one — it may be from a previous run, with credentials
        // nobody has. Start clean.
        await LeaveAsync().ConfigureAwait(false);

        // Searching while creating is a collision, and there is nothing left to search for.
        _manager.StopPeerDiscovery(_channel, new ActionListener("stopPeerDiscovery", _logger, onFailure: _ => { }));

        var created = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var listener = new ActionListener("createGroup", _logger,
            onFailure: r => { L($"createGroup failed reason={r}"); created.TrySetResult(false); },
            onSuccess: () => created.TrySetResult(true));

        if (WifiDirectCredentials.IsUsable(wanted) && CanNameTheGroup)
        {
            L($"creating {wanted!.NetworkName} — the name this Circle already knows");
            var config = new WifiP2pConfig.Builder()
                .SetNetworkName(wanted.NetworkName)
                .SetPassphrase(wanted.Passphrase)
                .Build();
            _manager.CreateGroup(_channel, config, listener);
        }
        else
        {
            L("creating a group to host");
            _manager.CreateGroup(_channel, listener);
        }

        if (!await created.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false))
            return null;

        // The group exists but its details are not immediately readable; ask until they are.
        for (var attempt = 0; attempt < 20 && !cancellationToken.IsCancellationRequested; attempt++)
        {
            if (await ReadGroupAsync(cancellationToken).ConfigureAwait(false) is { } credentials)
            {
                _groupFormed = true;
                L($"hosting {credentials.NetworkName}");
                return credentials;
            }
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        L("created a group but could not read its credentials");
        return null;
    }

    /// <summary>
    /// Whether this Android lets the group be named. <c>createGroup(config)</c> arrived at API 29, and
    /// below it the framework picks — which puts that phone back to needing the credentials delivered.
    /// </summary>
    /// <summary>How long to wait for a group to actually form after connect() has been accepted.</summary>
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan JoinPoll = TimeSpan.FromMilliseconds(500);

    /// <summary>Whether this phone is in a Wi-Fi Direct group right now.</summary>
    public bool IsInGroup => _groupFormed;

    private static bool CanNameTheGroup =>
        global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.Q;

    private Task<WifiDirectCredentials?> ReadGroupAsync(CancellationToken cancellationToken)
    {
        if (_manager is null || _channel is null) return Task.FromResult<WifiDirectCredentials?>(null);

        var read = new TaskCompletionSource<WifiDirectCredentials?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _manager.RequestGroupInfo(_channel, new GroupInfoListener(group =>
        {
            var credentials = group is { NetworkName: { } ssid, Passphrase: { } pass }
                ? new WifiDirectCredentials(ssid, pass)
                : null;
            read.TrySetResult(WifiDirectCredentials.IsUsable(credentials) ? credentials : null);
        }));

        return read.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken)
            .ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : null, TaskScheduler.Default);
    }

    /// <summary>
    /// Join a named group directly.
    ///
    /// <para>
    /// Naming the network and its passphrase is what makes this dialog-free: Android has nothing to ask
    /// the user about, because nothing is being negotiated. It also needs no discovery at all, which is
    /// the other half of what made the old path unreliable.
    /// </para>
    /// </summary>
    public async Task<bool> JoinAsync(WifiDirectCredentials credentials, CancellationToken cancellationToken = default)
    {
        if (_manager is null || !WifiDirectCredentials.IsUsable(credentials)) return false;
        if (Blocker is { } blocker) { L($"can't join — {blocker}"); return false; }

        EnsureInitialized();
        if (_channel is null) return false;

        await LeaveAsync().ConfigureAwait(false);

        // Searching and joining at the same time is the collision the old path kept losing to. Nothing
        // here needs discovery, so stop it.
        _manager.StopPeerDiscovery(_channel, new ActionListener("stopPeerDiscovery", _logger, onFailure: _ => { }));

        var config = new WifiP2pConfig.Builder()
            .SetNetworkName(credentials.NetworkName)
            .SetPassphrase(credentials.Passphrase)
            .Build();

        var accepted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        L($"joining {credentials.NetworkName}");
        _manager.Connect(_channel, config, new ActionListener("joinGroup", _logger,
            onFailure: r => { L($"join failed reason={r}"); accepted.TrySetResult(false); },
            onSuccess: () => accepted.TrySetResult(true)));

        if (!await accepted.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false))
            return false;

        // Accepted is not joined.
        //
        // connect() answers as soon as the framework has taken the request, which it will happily do
        // for a group that does not exist yet — and then report groupFormed=False a few seconds later
        // with no further comment. Returning true there told the caller it was done, so the retry loop
        // above stopped retrying, and two phones sat four seconds out of step forever. Measured: one
        // joined at :28, the other created the group at :32.
        //
        // So wait for the group to actually form. The connection broadcast is what says so.
        for (var waited = TimeSpan.Zero; waited < JoinTimeout; waited += JoinPoll)
        {
            if (_groupFormed) return true;
            await Task.Delay(JoinPoll, cancellationToken).ConfigureAwait(false);
        }

        L($"{credentials.NetworkName} did not form — it is probably not up yet");
        return false;
    }

    /// <summary>Leave whatever group this phone is in, and wait for it to actually be gone.</summary>
    public async Task LeaveAsync()
    {
        if (_manager is null || _channel is null) return;

        var left = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _manager.RemoveGroup(_channel, new ActionListener("removeGroup", _logger,
            onFailure: _ => left.TrySetResult(true),      // usually "no group" — which is the goal anyway
            onSuccess: () => left.TrySetResult(true)));

        try { await left.Task.WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false); }
        catch (TimeoutException) { }
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
    /// Which of two phones creates the group.
    ///
    /// <para>
    /// The same shape of rule the app uses everywhere else: one both sides can evaluate alone, with no
    /// round trip and no way to disagree. Each advertises its rotating address and sees the other's,
    /// so ordering the two strings gives opposite answers on the two handsets, every time.
    /// </para>
    /// </summary>


    // ── Broadcast handling ─────────────────────────────────────────────────────

    /// <summary>Trace to logcat (tag AetherWFD), the ILogger, and the UI status line at once.</summary>
    private void L(string m)
    {
        global::Android.Util.Log.Info("AetherWFD", m);
        _logger.LogInformation("{Msg}", m);
        Status?.Invoke(m);
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

        L("the group went away");

        // Nothing to restart. The group is derived, not found, so FastRadioService simply hosts or
        // joins it again — and it does not need this radio's help to work out which.
        GroupLost?.Invoke();
    }

    /// <summary>
    /// Accept clients, for as long as this phone owns the group.
    /// </summary>
    /// <remarks>
    /// Guarded, because the connection broadcast fires more than once for the same group and each
    /// firing used to start another server. The second bind fails — the port is already ours — and in
    /// failing it overwrote the field the first accept loop was reading from, so the working server
    /// exited and the group owner stopped listening inside a group it was still hosting. The client on
    /// the other side saw "Connection refused" every three seconds while both phones agreed they were
    /// in the same group.
    /// </remarks>
    private async Task RunServerAsync()
    {
        if (Interlocked.Exchange(ref _serverRunning, 1) == 1) return;

        try
        {
            _server = new TcpListener(IPAddress.Any, TcpPort);
            _server.Start();
            L($"GO: TCP server listening on {TcpPort}");
            while (!_disposed)
            {
                var client = await _server.AcceptTcpClientAsync().ConfigureAwait(false);
                _ = Task.Run(async () =>
                {
                    using (client) await HandleSocketAsync(client).ConfigureAwait(false);
                });
            }
        }
        catch (Exception ex) when (!_disposed) { L($"GO: server stopped ({ex.Message})"); }
        finally
        {
            Interlocked.Exchange(ref _serverRunning, 0);
        }
    }

    /// <summary>
    /// Dial the group owner, and keep dialling for as long as we are in its group.
    ///
    /// <para>
    /// A group and a connection are not the same thing, and this is where that bites. Android keeps
    /// the group up perfectly happily while the TCP socket across it dies — the owner restarts its
    /// server, the client's socket goes with it, and the group reports itself formed throughout. One
    /// dial attempt meant the client then sat inside a healthy group with nowhere to send: measured as
    /// "app→radio 149B on Wi-Fi Direct linked=False sent=False" while both phones agreed they were in
    /// DIRECT-RYZ4HH1Y9.
    /// </para>
    ///
    /// <para>
    /// So it redials. The owner's server may not be listening the instant the group forms — it is
    /// starting at the same moment we are connecting — and it may restart later; neither is a reason
    /// to give up on a group we are still in.
    /// </para>
    /// </summary>
    private async Task RunClientAsync(string goAddress)
    {
        if (Interlocked.Exchange(ref _clientRunning, 1) == 1) return;

        try
        {
            while (!_disposed && _groupFormed)
            {
                try
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(IPAddress.Parse(goAddress), TcpPort).ConfigureAwait(false);
                    L("client: TCP connected to GO");
                    await HandleSocketAsync(client).ConfigureAwait(false);
                    if (!_disposed && _groupFormed) L("client: connection to GO ended — redialling");
                }
                catch (Exception ex) when (!_disposed)
                {
                    L($"client: could not reach the GO ({ex.Message}) — retrying");
                }

                if (!_disposed && _groupFormed)
                    await Task.Delay(RedialAfter).ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _clientRunning, 0);
        }
    }

    /// <summary>How long to wait before dialling the group owner again.</summary>
    private static readonly TimeSpan RedialAfter = TimeSpan.FromSeconds(3);

    private int _clientRunning;
    private int _serverRunning;

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
                if (peerUhid is null) continue;

                // Logged because a packet that leaves one phone and never appears on the other is
                // otherwise indistinguishable from one that was never sent — and both ends reported
                // success while a message quietly went nowhere.
                L($"◀ {frame.Length}B from {peerUhid}");
                DataReceived?.Invoke(peerUhid, frame);
            }
        }
        catch (Exception ex) when (!_disposed) { L($"socket to {peerUhid ?? "a peer"} closed: {ex.Message}"); }
        finally
        {
            // The caller owns the socket. It disposed it here as well, which on the client path meant
            // the redial loop's `using` disposed something already gone.
            if (peerUhid is not null) _peers.TryRemove(peerUhid, out _);
        }
    }

    // ── ITransportService send/query ────────────────────────────────────────────

    public async Task<bool> SendAsync(string peerUhid, byte[] data, CancellationToken cancellationToken = default)
    {
        if (!_peers.TryGetValue(peerUhid, out var link))
        {
            L($"▶ nowhere to send {data.Length}B — {peerUhid} is not one of the {_peers.Count} linked");
            return false;
        }
        await link.Write.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await WriteFrameAsync(link.Stream, data).ConfigureAwait(false); return true; }
        catch (Exception ex) { L($"▶ send to {peerUhid} failed: {ex.Message}"); return false; }
        finally { link.Write.Release(); }
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

    /// <summary>
    /// One peer's socket, and the lock that keeps frames on it whole.
    ///
    /// <para>
    /// A frame is a length followed by a payload, written with three separate awaits. Two sends
    /// running at once interleave those writes, and the far side then reads a length out of the
    /// middle of somebody else's payload — so it closes the connection, and this side sees "Broken
    /// pipe" on a socket that was perfectly healthy a moment ago.
    /// </para>
    /// <para>
    /// It looked like a flaky radio, and it was not. Attachments send their chunks concurrently, so
    /// the moment anything larger than a text message crossed, the link tore itself down and rebuilt,
    /// over and over. Nothing about it was visible from either end except the reconnects.
    /// </para>
    /// </summary>
    private sealed record PeerLink(TcpClient Client, NetworkStream Stream)
    {
        public SemaphoreSlim Write { get; } = new(1, 1);
    }

    // ── Android listener/receiver adapters ──────────────────────────────────────

    private sealed class Receiver(AndroidWifiDirectTransportService owner) : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            switch (intent?.Action)
            {
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

}
#endif

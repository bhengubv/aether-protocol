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
    private TimeSpan FindLifetime => TimeSpan.FromSeconds(15) + _findSkew;

    private readonly TimeSpan _findSkew;
    private bool _listening;

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
        _manager.SetDnsSdResponseListeners(_channel, new ServiceResponseListener(this), new TxtRecordListener(this));

        // Clear before adding. Requests accumulate across re-announcements, and a channel holding the
        // same request several times answers each sighting several times over — which reads as a
        // storm of peers rather than one phone saying hello once.
        _manager.ClearServiceRequests(_channel, new ActionListener("clearServiceRequests", _logger,
            onSuccess: () => _manager.AddServiceRequest(_channel, WifiP2pDnsSdServiceRequest.NewInstance(),
                new ActionListener("addServiceRequest", _logger,
                    onSuccess: StartFinding))));
    }

    /// <summary>
    /// Start looking for our own service.
    ///
    /// <para>
    /// Only <c>discoverServices()</c> is asked for, and that is deliberate: it runs a peer scan of its
    /// own underneath, and a separate <c>discoverPeers()</c> call cancels the service cycle that is
    /// already running. Asking for both is what made DNS-SD look broken on this hardware — every
    /// service response was killed a second or two before it could arrive, and the peer list was the
    /// only thing that ever came back. The peer scan still happens; it is just no longer restarted out
    /// from under the thing we actually want.
    /// </para>
    /// </summary>
    private void StartFinding()
    {
        if (_manager is null || _channel is null) return;

        _manager.DiscoverServices(_channel, new ActionListener("discoverServices", _logger,
            // If the service cycle will not start at all, a plain peer scan is still better than
            // sitting blind — it cannot say who is an AetherNet node, but it proves the radio is alive.
            onFailure: r =>
            {
                L($"discoverServices failed reason={r} — falling back to a plain peer scan");
                _manager.DiscoverPeers(_channel, new ActionListener("discoverPeers", _logger));
            }));
    }

    // ── Brokered groups: created, not negotiated ────────────────────────────────

    /// <summary>
    /// Create a group outright and become its owner, then read back what a second phone needs to join.
    ///
    /// <para>
    /// <c>createGroup()</c> does not negotiate with anybody, so there is no race to lose and no
    /// "Invitation to connect" dialog for a peer to ignore. The framework picks the network name and
    /// passphrase; we simply read them and hand them over the link that already works.
    /// </para>
    /// </summary>
    public async Task<WifiDirectCredentials?> HostAsync(CancellationToken cancellationToken = default)
    {
        if (_manager is null) return null;
        if (Blocker is { } blocker) { L($"can't host — {blocker}"); return null; }

        EnsureInitialized();
        if (_channel is null) return null;

        // A group already open here is the wrong one — it may be from a previous run, with credentials
        // nobody has. Start clean.
        await LeaveAsync().ConfigureAwait(false);

        var created = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        L("creating a group to host");
        _manager.CreateGroup(_channel, new ActionListener("createGroup", _logger,
            onFailure: r => { L($"createGroup failed reason={r}"); created.TrySetResult(false); },
            onSuccess: () => created.TrySetResult(true)));

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

        var joined = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        L($"joining {credentials.NetworkName}");
        _manager.Connect(_channel, config, new ActionListener("joinGroup", _logger,
            onFailure: r => { L($"join failed reason={r}"); joined.TrySetResult(false); },
            onSuccess: () => joined.TrySetResult(true)));

        return await joined.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
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
    /// <summary>
    /// Keep looking, and — just as important — keep being findable.
    ///
    /// <para>
    /// Two phones both sitting in <c>p2p_find</c> is why service discovery never completed. Find
    /// alternates its own search and listen phases on a timer neither app controls, so with both ends
    /// free-running, one side's query keeps arriving while the other is mid-search and nobody is home
    /// to answer. The supplicant says so in as many words: <c>Service Discovery Query TX callback:
    /// success=0</c>, then <c>Do not start Service Discovery … due to it being the first no-ACK peer
    /// in this search iteration</c>. Both phones were answering queries perfectly well; neither was
    /// ever awake to hear the reply.
    /// </para>
    /// <para>
    /// So this deliberately spends part of every cycle listening instead of searching. From API 30 the
    /// framework exposes that directly as <c>startListening()</c>; below it, the listen phases inside
    /// find are all there is, and the skew below is what keeps the two ends out of phase. Either way
    /// the point is the same — stop both sides talking over each other.
    /// </para>
    /// </summary>
    private async Task RediscoverLoopAsync()
    {
        // Restarted whenever a group goes away, so it has to be safe to call while already running —
        // two loops search twice as often and cancel each other's finds.
        if (Interlocked.Exchange(ref _findLoopRunning, 1) == 1) return;

        // Derived from this phone's own id, so the two ends of a pair land on different phases and
        // stay there. A random skew would drift back into step as often as out of it.
        var listening = _findSkew.TotalMilliseconds % 2 < 1;

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
                _lastPeerNewsUtc = DateTime.UtcNow;

                listening = !listening;
                try
                {
                    if (listening && CanListen)
                    {
                        L($"listening for {FindLifetime.TotalSeconds:0}s — so a query has somebody to answer it");
                        StartListening();
                    }
                    else
                    {
                        L($"searching for {FindLifetime.TotalSeconds:0}s");
                        StopListening();
                        StartFinding();
                    }
                }
                catch (Exception ex) { _logger.LogDebug(ex, "re-discover error"); }
            }
        }
        finally
        {
            StopListening();
            Interlocked.Exchange(ref _findLoopRunning, 0);
        }
    }

    /// <summary>Whether this Android can be told to listen without also searching (API 30+).</summary>
    private static bool CanListen =>
        global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.R;

    private void StartListening()
    {
        if (!CanListen || _manager is null || _channel is null || _listening) return;
        _listening = true;
        _manager.StartListening(_channel, new ActionListener("startListening", _logger,
            onFailure: _ => _listening = false));
    }

    private void StopListening()
    {
        if (!CanListen || _manager is null || _channel is null || !_listening) return;
        _listening = false;
        _manager.StopListening(_channel, new ActionListener("stopListening", _logger));
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
    /// The id another phone advertised, kept against the address it came from.
    ///
    /// <para>
    /// The TXT record arrives just before the service record does, so this is filled in by the time
    /// <see cref="OnServiceFound"/> needs it. When it is not — a peer whose record was missed — that
    /// peer is simply left alone this round and found on the next sweep, which is far better than
    /// guessing at a role.
    /// </para>
    /// </summary>
    /// <summary>Which discovered addresses are phones, by WPS category. Nothing else is ever dialled.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _phones =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _peerIds =
        new(StringComparer.OrdinalIgnoreCase);

    private void RememberPeerId(string deviceAddress, string id) => _peerIds[deviceAddress] = id;

    /// <summary>
    /// Which of two phones creates the group.
    ///
    /// <para>
    /// The same shape of rule the app uses everywhere else: one both sides can evaluate alone, with no
    /// round trip and no way to disagree. Each advertises its rotating address and sees the other's,
    /// so ordering the two strings gives opposite answers on the two handsets, every time.
    /// </para>
    /// </summary>
    private static bool IHost(string mine, string theirs) => string.CompareOrdinal(mine, theirs) < 0;

    /// <summary>
    /// A peer was found by DNS-SD. Decide who hosts, then act — nobody negotiates.
    ///
    /// <para>
    /// This used to call <c>connect()</c> the moment it saw anything, on both phones at once. Two
    /// phones calling connect() at each other is a race, and losing it drops Android's "Invitation to
    /// connect" dialog in front of an app nobody is looking at. Deciding first removes the race
    /// entirely: the host creates the group and waits, and the joiner connects to a group that already
    /// exists, which needs no invitation and shows no dialog.
    /// </para>
    ///
    /// <para>
    /// This is also what makes the radio self-sufficient. It finds its own peers, agrees its own roles
    /// and forms its own group — no second radio, and no credentials over the air.
    /// </para>
    /// </summary>
    private void OnServiceFound(string? instanceName, string? registrationType, WifiP2pDevice? src)
    {
        L($"service seen: {instanceName} / {registrationType} @ {src?.DeviceAddress}");
        if (_groupFormed || src is null || _manager is null || _channel is null) return;
        if (registrationType?.Contains("aethernet", StringComparison.OrdinalIgnoreCase) != true &&
            instanceName?.Contains("aethernet", StringComparison.OrdinalIgnoreCase) != true) return;

        var address = src.DeviceAddress;
        if (string.IsNullOrEmpty(address)) return;

        if (_phones.TryGetValue(address, out var isPhone) && !isPhone)
        {
            L($"{address} advertises our service but is not a phone — left alone");
            return;
        }

        if (!_peerIds.TryGetValue(address, out var theirs))
        {
            L($"no id yet from {address} — leaving it for the next sweep");
            return;
        }

        var mine = CurrentAddress();
        if (string.Equals(mine, theirs, StringComparison.Ordinal)) return;   // ourselves, somehow

        if (IHost(mine, theirs))
        {
            // Create and wait. The joiner comes to us, so there is nothing to connect to and nothing
            // to race over.
            L($"{theirs} found — this phone hosts, creating the group");
            _ = HostAsync();
            return;
        }

        if (!TryBeginConnect()) return;

        L($"{theirs} found — they host, joining {address}");
        ConnectTo(address);
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
            NotePeers(list);
            foreach (var d in list.DeviceList)
                if (d.DeviceAddress is { Length: > 0 } a) _phones[a] = IsPhone(d);

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
        _ = Task.Run(RediscoverLoopAsync);
    }

    /// <summary>
    /// Who is nearby, for the log and for liveness — never for dialling.
    ///
    /// <para>
    /// This used to pick the first phone-shaped device in the list and call <c>connect()</c> on it,
    /// from both sides at once. That is three separate faults in one line. It dials devices that have
    /// never heard of AetherNet, because "is a phone" is all the peer list can tell you. It races the
    /// other end, which is what puts Android's "Invitation to connect" dialog in front of someone who
    /// asked for nothing. And it beats DNS-SD to the latch every time, because a peer scan answers in
    /// a second or two and a service cycle takes longer — so the careful path never got to run.
    /// </para>
    /// <para>
    /// A peer sighting now means only what it can actually prove: the radio is awake and someone is
    /// out there. Deciding whether that someone is one of ours, and which of us hosts, is
    /// <see cref="OnServiceFound"/>'s job — it is the only path with the ids in hand to answer either
    /// question.
    /// </para>
    /// </summary>
    private static void NotePeers(WifiP2pDeviceList list)
    {
        foreach (var d in list.DeviceList)
            global::Android.Util.Log.Info("AetherWFD",
                $"  peer {d.DeviceAddress} status={d.Status} type={d.PrimaryDeviceType} " +
                $"({Category(d)}){(IsPhone(d) ? "" : " — not a phone, never dialled")}");
    }

    /// <summary>
    /// What kind of gadget this is, from the WPS primary device type it puts in its own beacon.
    ///
    /// <para>
    /// The triple looks like <c>10-0050F204-5</c>: the leading number is the WPS category, and it
    /// arrives in the peer list itself — no query, no round trip, no cooperation from the other end
    /// beyond it being a Wi-Fi Direct device at all. So there is never an excuse for dialling the
    /// office printer: we were told it was a printer the moment we saw it.
    /// </para>
    /// </summary>
    private static string Category(WifiP2pDevice d) => d.PrimaryDeviceType?.Split('-')[0] switch
    {
        "1" => "computer", "2" => "input device", "3" => "printer", "4" => "camera",
        "5" => "storage", "6" => "network infrastructure", "7" => "display",
        "8" => "multimedia device", "9" => "gaming device", "10" => "phone",
        "11" => "audio device", _ => "unknown",
    };

    /// <summary>
    /// Is this peer a phone rather than some other Wi-Fi Direct gadget?
    /// <para>
    /// Category 10 is Telephone. An unknown type is allowed through — a phone that reports nothing is
    /// still more likely one of ours than not, and the service check behind this one is what actually
    /// decides. A printer, a display or a camera is never dialled, whatever else happens.
    /// </para>
    /// </summary>
    private static bool IsPhone(WifiP2pDevice d)
    {
        var type = d.PrimaryDeviceType;
        if (string.IsNullOrEmpty(type)) return true;
        return type.StartsWith("10-", StringComparison.Ordinal);
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

    /// <summary>
    /// The peer's advertised id, which is what lets the two of them agree who hosts.
    ///
    /// <para>
    /// This body was empty and the record was thrown away — so discovery knew a peer existed and
    /// nothing about it, and both phones did the only thing left: called <c>connect()</c> at each
    /// other. That is the race, and losing it is Android's "Invitation to connect" dialog on a screen
    /// nobody is looking at. The id costs nothing to keep and turns the race into a decision.
    /// </para>
    /// </summary>
    private sealed class TxtRecordListener(AndroidWifiDirectTransportService owner)
        : Java.Lang.Object, WifiP2pManager.IDnsSdTxtRecordListener
    {
        public void OnDnsSdTxtRecordAvailable(string? fullDomainName,
            IDictionary<string, string>? txtRecordMap, WifiP2pDevice? srcDevice)
        {
            if (srcDevice?.DeviceAddress is not { Length: > 0 } address) return;
            if (txtRecordMap is null || !txtRecordMap.TryGetValue("id", out var id)) return;
            if (string.IsNullOrEmpty(id)) return;

            owner.RememberPeerId(address, id);
        }
    }
}
#endif

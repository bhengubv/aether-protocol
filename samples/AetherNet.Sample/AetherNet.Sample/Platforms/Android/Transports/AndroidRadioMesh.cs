// SPDX-License-Identifier: MIT
#if ANDROID
using AetherNet.Identity;
using AetherNet.Protocol;
using AetherNet.Sample.Shared.Services;
using AetherNet.Security.Services;
using Microsoft.Extensions.Logging;
using System.Text;

namespace AetherNet.Sample.Platforms.Android.Transports;

/// <summary>
/// The real over-the-air mesh on Android, inside this one APK. Owns one Ed25519 identity and a
/// set of native radios (Wi-Fi Direct, BLE, … — all <see cref="IRadio"/>). The UI picks a radio;
/// this links over it and moves real <see cref="MeshPacket"/>s to the linked peer phone.
/// </summary>
public sealed class AndroidRadioMesh : IRadioMesh, IDisposable
{
    private readonly object _gate = new();
    private readonly List<string> _log = new();
    private readonly Dictionary<string, IRadio> _radios = new(StringComparer.Ordinal);
    private readonly List<IRadio> _order = new();
    private readonly string _localUhid;
    private readonly byte[] _routingKey;
    private IRadio _selected;

    public AndroidRadioMesh(IIdentityService me, ILogger<AndroidRadioMesh> logger)
    {
        // The radio announces the SAME AetherTag the rest of the app uses. Generating one here would
        // give the device a third identity — the peer you linked with would not be the peer you added,
        // and it would change on every restart.
        ArgumentNullException.ThrowIfNull(me);
        LocalTag = me.AetherTag;
        _localUhid = LocalTag;

        // The wire address rotates off a key derived from the device's identity, asked for by purpose.
        // The identity itself never reaches a radio — and deriving the address from the public tag
        // instead would let anyone holding that tag compute every address this phone will ever use.
        var routingKey = me.RoutingKey;
        _routingKey = routingKey;

        Register(new AndroidWifiDirectTransportService(global::Android.App.Application.Context!, _localUhid, logger, routingKey));
        Register(new AndroidBleTransportService("BLE",
            "61657468-6572-0001-0000-000000000001", "61657468-6572-0003-0000-000000000001",
            "61657468-6572-0002-0000-000000000001", _localUhid, logger, routingKey: routingKey));
        // NearLink (SparkLink) is Huawei silicon driven by HarmonyOS APIs. Android's Bluetooth stack
        // cannot speak it, so on an Android phone there is no way to detect it, let alone use it. The
        // GATT engine below is a stand-in for the day the hardware is there — it must never present
        // itself as a working radio, because running Bluetooth under the NearLink name would be a lie
        // about what the device just did.
        Register(new AndroidBleTransportService("NearLink",
            "6e65726c-696e-0001-0000-000000000001", "6e65726c-696e-0003-0000-000000000001",
            "6e65726c-696e-0002-0000-000000000001", _localUhid, logger,
            unavailableReason: "needs NearLink hardware and HarmonyOS", routingKey: routingKey));
        Register(new AndroidNfcTransportService(_localUhid, logger));
        Register(new AndroidLoRaTransportService(_localUhid, logger));
        // Default to BLE: it links via GATT (advertise/scan) with no dependency on the Wi-Fi P2P
        // service-discovery framework, so it comes up reliably phone-to-phone. Wi-Fi Direct stays
        // available in the picker for higher throughput once linking is confirmed.
        _selected = _radios.TryGetValue("BLE", out var ble) ? ble : _order[0];
    }

    private void Register(IRadio r)
    {
        _radios[r.Name] = r;
        _order.Add(r);
        r.Status += s => Emit($"[{r.Name}] {s}");
        r.PeerLinked += p => Emit($"[{r.Name}] ● linked with {p}");
        r.DataReceived += (from, bytes) =>
        {
            try
            {
                var pkt = PacketSerializer.Deserialize(bytes);
                Emit($"[{r.Name}] ◀ from {from}: \"{Encoding.UTF8.GetString(pkt.Payload)}\"");
            }
            catch { Emit($"[{r.Name}] ◀ {bytes.Length} bytes from {from}"); }

            // Hand the raw packet to any higher layer riding this radio (the mesh-web).
            PacketReceived?.Invoke(bytes);
        };
    }

    /// <summary>
    /// The Wi-Fi Direct radio's group-hosting side, so the broker can create and join groups on it.
    /// Exposed as the capability rather than the radio, because hosting a group is specific to this
    /// one radio and means nothing to the others.
    /// </summary>
    public AetherNet.Sample.Shared.Services.IWifiDirectGroup WifiDirect =>
        (AetherNet.Sample.Shared.Services.IWifiDirectGroup)_radios["Wi-Fi Direct"];

    public string LocalTag { get; }
    public IReadOnlyList<RadioInfo> Radios =>
        _order.Select(r => new RadioInfo(r.Name, r.IsAvailable, r.UnavailableReason, r.IsFixable)).ToArray();
    public string SelectedRadio => _selected.Name;
    public bool IsSupported => _selected.IsAvailable;

    /// <summary>
    /// The radio actually carrying traffic right now: your preferred one while it holds a link, and
    /// otherwise whichever one does. The preference is a preference, not a restriction — a phone that
    /// can still be reached over another radio is still reachable.
    /// </summary>
    private IRadio Active => _selected.IsLinked ? _selected : _order.FirstOrDefault(r => r.IsLinked) ?? _selected;

    public bool IsLinked => _order.Any(r => r.IsLinked);

    /// <summary>
    /// Who is actually there: their AetherTag once a message from them has opened under it, and the
    /// rotating wire address until then. The address is what the radio saw; the tag is who it turned
    /// out to be.
    /// </summary>
    public string? PeerTag
    {
        get
        {
            if (Active.PeerTag is not { } wire) return null;
            lock (_gate) return _known.TryGetValue(wire, out var tag) ? tag : wire;
        }
    }

    /// <summary>Wire address → the person it turned out to be, once that has been proven.</summary>
    private readonly Dictionary<string, string> _known = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public void IdentifyPeer(string aetherTag)
    {
        if (string.IsNullOrEmpty(aetherTag) || Active.PeerTag is not { } wire || wire == aetherTag)
            return;

        lock (_gate)
        {
            if (_known.TryGetValue(wire, out var already) && already == aetherTag) return;

            // Wire addresses rotate every epoch, so this would otherwise grow for as long as the app
            // runs. Nothing here is worth keeping: the live link re-identifies itself on the next
            // message that opens, which is seconds away on a conversation that is actually going.
            if (_known.Count > 32) _known.Clear();
            _known[wire] = aetherTag;
        }

        Emit($"[{Active.Name}] ● {wire} is {aetherTag}");
    }

    public event Action? Changed;
    public event Action<byte[]>? PacketReceived;
    public IReadOnlyList<string> Log { get { lock (_gate) { return _log.ToArray(); } } }

    /// <summary>
    /// Choose the radio to prefer. It is a preference, not a switch — the others keep listening, and
    /// if this one has no link the mesh keeps using whatever does.
    /// </summary>
    public void SelectRadio(string name)
    {
        if (!_radios.TryGetValue(name, out var r)) return;
        _selected = r;
        if (r.IsAvailable && !r.IsLinked) { Emit($"[{r.Name}] preferred — bringing it up"); r.Link(); }
        RaiseChanged();
    }

    /// <summary>
    /// Bring up the preferred radio, and put every other working radio into listening range too.
    /// <para>
    /// Radios fail in different ways — Bluetooth drops when the phone is busy, Wi-Fi Direct needs a
    /// group to form, NFC needs a tap — so relying on exactly one is a single point of failure for a
    /// network whose whole point is not having one. The others are only asked to listen, not to
    /// transmit, which keeps the battery cost near zero while leaving every door open.
    /// </para>
    /// </summary>
    public void Link()
    {
        // Take the foreground service before the radio, not after: Android only lets an app hold a
        // connection off-screen while that service is running, and the user may leave the app the
        // moment they have tapped Connect.
        AetherLinkService.Start();

        Emit($"[{_selected.Name}] linking…");
        _selected.Link();

        foreach (var r in _order)
        {
            if (ReferenceEquals(r, _selected) || !r.IsAvailable || r.IsLinked) continue;
            Emit($"[{r.Name}] also listening");
            try { r.Link(); } catch (Exception ex) { Emit($"[{r.Name}] could not listen: {ex.Message}"); }
        }
    }

    public async Task SendTestAsync(string text)
    {
        if (_selected.PeerTag is null) { Emit($"[{_selected.Name}] no peer linked yet"); return; }
        var pkt = new MeshPacket
        {
            Type = PacketType.Data,
            // The header is readable before anything is decrypted, so it carries where-to-send, not
            // who-we-are. The identity travels inside the session.
            SourceUhid = WireAddress.For(_routingKey),
            DestinationUhid = _selected.PeerTag,
            Payload = Encoding.UTF8.GetBytes(text),
            Ttl = 7,
        };
        var ok = await _selected.SendAsync(PacketSerializer.Serialize(pkt)).ConfigureAwait(false);
        Emit(ok ? $"[{_selected.Name}] ▶ sent: \"{text}\"" : $"[{_selected.Name}] ▶ send failed");
    }

    /// <summary>
    /// Push a raw packet to the peer, over whichever radio can carry it.
    /// <para>
    /// The preferred radio goes first; if it will not take the packet, every other linked radio is
    /// tried before giving up. A message is only reported as unsent once nothing at all could carry
    /// it. Arriving twice is harmless — the receiver keys messages by the sender's own id, so a
    /// duplicate updates the message already there instead of showing the words again.
    /// </para>
    /// </summary>
    public async Task<bool> SendPacketAsync(byte[] packetBytes)
    {
        foreach (var r in Candidates())
        {
            var ok = await r.SendAsync(packetBytes).ConfigureAwait(false);
            global::Android.Util.Log.Info("AetherBLE",
                $"app→radio {packetBytes.Length}B on {r.Name} linked={r.IsLinked} sent={ok}");
            if (ok) return true;
        }
        return false;
    }

    /// <summary>The radios worth trying, best first: the active one, then any other holding a link.</summary>
    private IEnumerable<IRadio> Candidates()
    {
        var first = Active;
        yield return first;
        foreach (var r in _order)
            if (!ReferenceEquals(r, first) && r.IsLinked) yield return r;
    }

    public void Stop()
    {
        foreach (var r in _order) r.Stop();
        Emit("stopped");
    }

    public void Dispose()
    {
        foreach (var r in _order)
            if (r is IDisposable d) d.Dispose();
    }

    private void Emit(string line)
    {
        lock (_gate)
        {
            _log.Add(line);
            if (_log.Count > 300) _log.RemoveAt(0);
        }
        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke();
}
#endif

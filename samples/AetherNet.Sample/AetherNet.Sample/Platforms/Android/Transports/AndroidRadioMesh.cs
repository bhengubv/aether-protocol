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
        // Wi-Fi Aware is registered even though no phone here has the hardware. It is an open Wi-Fi
        // Alliance standard with a standard Android API, so the code is portable in a way NearLink's
        // never can be, and registering it means the picker says "this phone does not have Wi-Fi
        // Aware" rather than the radio not existing at all. See PROTOCOL_SPEC §5.6.
        Register(new AndroidWifiAwareTransportService(() => WireAddress.For(routingKey), logger));
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

    /// <inheritdoc />
    public string LinkRadio
    {
        get
        {
            foreach (var r in Candidates())
                if (r.IsLinked) return r.Name;
            return _selected.Name;
        }
    }

    /// <inheritdoc />
    public long LinkBandwidthBps
    {
        get
        {
            // The radio that traffic actually leaves on, not the one in the picker — sizing media to
            // the wrong radio is how a call ends up asking a narrow link for a wide one's bitrate.
            foreach (var r in Candidates())
                if (r.IsLinked) return r.MaxBandwidthBps;
            return 0;
        }
    }
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
            // Ask the radios in the order traffic actually uses them, not the order the picker shows.
            // The widest linked radio is the one a packet leaves on, and it is very often not the
            // selected one: bring Wi-Fi Direct up alongside BLE and every byte moves to Wi-Fi Direct
            // while the picker still says BLE.
            foreach (var r in Candidates())
            {
                if (r.PeerTag is not { } wire) continue;
                lock (_gate)
                {
                    if (_known.TryGetValue(wire, out var tag)) return tag;
                }
            }

            // Nobody proven yet — report the wire address of the radio traffic would leave on, so what
            // is shown is what is being used.
            foreach (var r in Candidates())
                if (r.PeerTag is { } wire) return wire;

            return null;
        }
    }

    /// <summary>Wire address → the person it turned out to be, once that has been proven.</summary>
    private readonly Dictionary<string, string> _known = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public void IdentifyPeer(string aetherTag)
    {
        if (string.IsNullOrEmpty(aetherTag)) return;

        // One person, however many radios can see them — so record the tag against the wire address
        // that EVERY linked radio currently has for them.
        //
        // This used to record only the selected radio's address, while sending picks the widest linked
        // one. With two radios up those are different addresses, so the tag was learned for a link
        // nothing was being sent on, and the link everything WAS being sent on stayed anonymous. A call
        // placed over Wi-Fi Direct while BLE held the identity reached nobody, and neither phone had a
        // word to say about why.
        foreach (var r in _order)
        {
            if (!r.IsLinked || r.PeerTag is not { } wire || wire == aetherTag) continue;

            bool learned = false;
            lock (_gate)
            {
                if (!_known.TryGetValue(wire, out var already) || already != aetherTag)
                {
                    // Wire addresses rotate every epoch, so this would otherwise grow for as long as
                    // the app runs. Nothing here is worth keeping: the live link re-identifies itself
                    // on the next message that opens, which is seconds away on a live conversation.
                    if (_known.Count > 32) _known.Clear();
                    _known[wire] = aetherTag;
                    learned = true;
                }
            }

            if (learned) Emit($"[{r.Name}] ● {wire} is {aetherTag}");
        }
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

    /// <summary>
    /// The radios worth trying, best first: the chosen one, then anything else holding a link.
    ///
    /// <para>
    /// The person's choice wins. Picking a radio is an instruction, not a hint — and it used to be a
    /// hint: this returned the WIDEST linked radio first regardless, so choosing BLE while Wi-Fi Direct
    /// was up changed the label on the screen and not one byte of what actually happened.
    /// </para>
    ///
    /// <para>
    /// Everything else linked still follows, widest first, so a call in progress does not drop dead the
    /// moment the chosen radio does.
    /// </para>
    /// </summary>
    private IEnumerable<IRadio> Candidates()
    {
        if (_selected.IsLinked) yield return _selected;

        foreach (var r in _order
                     .Where(r => r.IsLinked && !ReferenceEquals(r, _selected))
                     .OrderByDescending(r => r.MaxBandwidthBps))
            yield return r;

        // Nothing linked at all — still hand it to the chosen radio, which reports the failure honestly.
        if (!_selected.IsLinked) yield return _selected;
    }

    /// <summary>
    /// The widest linked radio, or the ordinary choice if none is wider.
    ///
    /// <para>
    /// A preferred radio is a preference about <b>reaching people</b>, not an instruction to force a
    /// call down a link that cannot hold one. BLE measures about 5 kbps between these handsets and one
    /// voice call wants roughly a hundred times that; sending media over it does not merely sound bad,
    /// it saturates the link and starves the signalling sharing it. Watched on device 2026-08-18: the
    /// callee answered and streamed happily, the caller sat on "Calling..." forever, because the answer
    /// could not get past the audio it was answering.
    /// </para>
    /// </summary>
    private IRadio Widest()
    {
        var best = Active;
        foreach (var r in _order)
            if (r.IsLinked && r.MaxBandwidthBps > best.MaxBandwidthBps) best = r;
        return best;
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

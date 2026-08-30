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

    /// <summary>
    /// The same commentary, written where somebody debugging can read it.
    /// </summary>
    /// <remarks>
    /// The radio log used to live only inside the app: a list a screen could show, and nothing else.
    /// So a radio that never started and a radio that started and said so looked identical from
    /// outside — which cost an evening, twice, chasing a Wi-Fi transport that may have been running
    /// the whole time. A log that cannot be read when the app is misbehaving is not a log.
    /// </remarks>
    private readonly ILogger<AndroidRadioMesh> _out;
    private readonly Dictionary<string, IRadio> _radios = new(StringComparer.Ordinal);
    private readonly List<IRadio> _order = new();
    private readonly string _localUhid;
    private readonly byte[] _routingKey;
    private readonly AetherNet.Sample.Shared.Services.CircleDirectory? _circle;

    /// <summary>
    /// Carrying for the people this phone has added.
    /// </summary>
    /// <remarks>
    /// The thing that makes this a mesh rather than a set of pairs. Two people who have added each
    /// other are often out of range of each other; a third phone both of them added is not, and it
    /// passes the note without ever being able to read it.
    /// </remarks>
    private readonly MeshRelay _relay = new();

    private IRadio _selected;

    /// <summary>The Wi-Fi already on the phone, kept so a meeting can be handed to it.</summary>
    private AetherNet.Transport.Wifi.WifiTransportService? _wifi;

    public AndroidRadioMesh(IIdentityService me, ILogger<AndroidRadioMesh> logger,
        AetherNet.Sample.Shared.Services.CircleDirectory? circle = null,
        AetherNet.Sample.Shared.Services.ProxyDirectory? proxies = null)
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
        _circle = circle;
        _out = logger;

        Register(new AndroidWifiDirectTransportService(global::Android.App.Application.Context!, _localUhid, logger, routingKey, circle));
        // Bluetooth is gone, and so is the NearLink stand-in that was Bluetooth wearing a different
        // name. It measured 11 kbps in one direction — it cannot carry a call, a note or an APK — and
        // while it was registered it did real harm: the mesh picks whichever radio reports a link, so
        // BLE kept taking traffic that Wi-Fi Direct was sitting there able to carry properly.
        //
        // Two radios, and they are independent. Wi-Fi Direct is the one every phone has and the only
        // one measured to carry real traffic. Internet is what you fall back to when nobody is in
        // range, and it is a phone in your Circle relaying, not a service.
        // Bluetooth is back.
        //
        // It was taken out because it kept carrying traffic Wi-Fi Direct should have: the mesh sent
        // over whichever radio reported a link, so an 11 kbps radio took messages, receipts and voice
        // notes while the fast one sat idle, and a 91 KB note crawled for a minute. Deleting it fixed
        // that and cost the one thing it was good at — being the radio that works when there is no
        // Wi-Fi of any kind, which for a mesh is most of the time.
        //
        // The reason is gone. RadioChoice now sends over the widest LINKED radio measured, so BLE only
        // carries when it is genuinely the best there is — which is exactly when it should. And it now
        // advertises the meeting rather than one fixed id for the whole app, so it answers the person
        // whose tag you were handed and nobody else.
        Register(new AndroidBleTransportService("BLE",
            "61657468-6572-0001-0000-000000000001", "61657468-6572-0003-0000-000000000001",
            "61657468-6572-0002-0000-000000000001", _localUhid, logger, routingKey: routingKey));

        Register(new AndroidWifiAwareTransportService(() => WireAddress.For(routingKey), logger));
        // The second leg. Last in the ladder on purpose: it costs the person data and puts their
        // traffic through somebody else's phone, so it is what you use when the alternative is nothing
        // at all — which, for a network meant to hold up when you walk out of range, is most of the time.
        Register(new AndroidInternetTransportService(global::Android.App.Application.Context!, _localUhid, logger, proxies));
        // The Wi-Fi the phone is already on.
        //
        // Wi-Fi Direct builds a network out of nothing, which is the right answer in a field and a
        // slow, fragile one in a kitchen where both handsets are three metres from the same access
        // point. Two phones sat on one network for an afternoon unable to reach each other while a
        // perfectly good link went unused — refusing to use it is not principle, it is waste.
        //
        // Below Wi-Fi Direct in the ladder on purpose: the router sees that two devices on it are
        // talking, how much and when. It never sees what — that is sealed above every radio equally —
        // so the difference is metadata and a dependency on somebody else's box. Worth having as one
        // way out among several rather than as the only one.
        _wifi = new AetherNet.Transport.Wifi.WifiTransportService(_localUhid);

        // Its own voice, or it has none.
        //
        // TransportRadio wraps a transport and raises ITS status, never the transport's — so
        // everything this radio said about itself went nowhere, and a radio that had not run looked
        // exactly like a radio that had. Silence from a layer is the wiring, not the code.
        _wifi.Status += s => Emit($"[Wi-Fi] {s}");

        Register(new TransportRadio(_wifi, _localUhid));

        Register(new AndroidNfcTransportService(_localUhid, logger));
        Register(new AndroidLoRaTransportService(_localUhid, logger));
        // Wi-Fi Direct is the radio this mesh is built on, and the default says so.
        //
        // Every phone has it, and it is the only one measured to carry real traffic: 50 frames/sec
        // each way against BLE's 11 kbps in ONE direction (PROTOCOL_SPEC §5.5). BLE was the default
        // because it links reliably with no dependency on Wi-Fi P2P service discovery — which is true,
        // and is why it stays as the radio that FINDS people and brokers the group. It is not the one
        // that should carry what it finds.
        //
        // Defaulting to BLE quietly made it the answer to everything: messages, receipts and notes all
        // went over eleven kilobits while the fast radio sat idle, and a 91 KB voice note took over a
        // minute on a phone that can move it in under a second.
        //
        // This is a preference, not a restriction — Widest() still sends over whichever radio is
        // actually linked, so nothing breaks before the group forms, and everything moves across the
        // moment it does.
        _selected = _radios.TryGetValue("Wi-Fi Direct", out var wifiDirect) ? wifiDirect
            : _radios.TryGetValue("BLE", out var ble) ? ble
            : _order[0];
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

            // Ours, or somebody's we carry? A packet addressed to a contact who is not us goes back
            // out on whichever radio can reach them, one hop shorter, and is NOT delivered upstairs —
            // this node is a router for it, not a reader.
            if (!Carry(bytes)) PacketReceived?.Invoke(bytes);
        };
    }

    /// <summary>
    /// Pass a packet on if it belongs to two people this phone has added.
    /// </summary>
    /// <returns>True when it was carried, and therefore must not also be delivered here.</returns>
    private bool Carry(byte[] bytes)
    {
        MeshPacket packet;
        try { packet = PacketSerializer.Deserialize(bytes); }
        catch { return false; }               // not a packet we understand — let the layer above look

        // Only this class holds the routing key, and only the circle can put a name to a rotating
        // address, so the two lookups the relay cannot do for itself are answered here.
        var mine = WireAddress.IsMine(packet.DestinationUhid, _routingKey);
        var from = _circle?.Recognise(packet.SourceUhid);
        var to = _circle?.Recognise(packet.DestinationUhid);

        var decision = _relay.Look(packet, mine, from, to);
        if (!decision.ShouldCarry) return false;

        var onward = PacketSerializer.Serialize(MeshRelay.OneHopShorter(packet));
        _ = ForwardAsync(decision.To!, onward, PacketPriority.Lane(packet.Type), packet.Ttl - 1);
        return true;
    }

    private async Task ForwardAsync(string toTag, byte[] onward, SendLane lane, int ttlLeft)
    {
        var sent = await SendToPeerAsync(toTag, onward, lane).ConfigureAwait(false);

        // Said either way. A relay that silently fails looks exactly like a relay nobody is using,
        // and the difference matters a great deal when somebody's message did not arrive.
        Emit(sent
            ? $"↻ carried {onward.Length}B for {toTag} — {ttlLeft} hops left"
            : $"↻ could not reach {toTag} to carry {onward.Length}B");
    }

    /// <summary>
    /// Send to one particular person, over whichever radio currently has a link to them.
    /// </summary>
    /// <remarks>
    /// Addressed by AetherTag rather than by wire address on purpose: the address rotates every
    /// fifteen minutes and the person does not, so a route held by address goes stale on the hour.
    /// </remarks>
    public async Task<bool> SendToPeerAsync(string aetherTag, byte[] packetBytes, SendLane lane)
    {
        if (string.IsNullOrEmpty(aetherTag)) return false;

        foreach (var r in _order)
        {
            if (!r.IsLinked) continue;

            foreach (var address in r.Peers)
            {
                if (!IsPerson(address, aetherTag)) continue;
                if (await r.SendToAsync(address, packetBytes, lane).ConfigureAwait(false)) return true;
            }
        }

        return false;
    }

    /// <summary>Is this wire address that person, either proven in-session or derivable from their key?</summary>
    private bool IsPerson(string address, string aetherTag)
    {
        lock (_gate)
        {
            if (_known.TryGetValue(address, out var known) && known == aetherTag) return true;
        }

        return string.Equals(_circle?.Recognise(address), aetherTag, StringComparison.Ordinal);
    }

    /// <summary>How many packets this phone has carried for other people.</summary>
    public long Carried => _relay.Carried;

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
            // What the link has been MEASURED doing, and only then what it claims.
            //
            // Every advertised figure in this app has been wrong: BLE published 2 Mbps and delivered
            // 11 kbps one way; Wi-Fi Direct still reports a flat 250 Mbps that nothing has checked.
            // Sizing media to a number nobody verified is how 800 kbps of video went onto a link that
            // was time-slicing against the phone's own access point.
            //
            // The measured figure is a FLOOR — what has crossed, not what could — so it is only used
            // once enough has crossed to mean something. Before that the advertised number is all
            // there is, and it is at least honest about being a guess.
            foreach (var r in Candidates())
            {
                if (!r.IsLinked) continue;
                var measured = r.Quality.ThroughputBps();
                return measured > 0 ? measured : r.MaxBandwidthBps;
            }
            return 0;
        }
    }
    public bool IsSupported => _selected.IsAvailable;

    /// <summary>
    /// The radio actually carrying traffic right now: your preferred one while it holds a link, and
    /// otherwise whichever one does. The preference is a preference, not a restriction — a phone that
    /// can still be reached over another radio is still reachable.
    /// </summary>
    private IRadio Active => Candidates().FirstOrDefault() ?? _selected;

    /// <summary>
    /// How hard the carrying radio is working, 0 to 1. Media sizes itself from this.
    /// </summary>
    public double LinkStrain
    {
        get
        {
            foreach (var r in Candidates())
                if (r.IsLinked) return r.Quality.Strain();
            return 0;
        }
    }

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
        // A preference about which radio is brought up, and nothing about where traffic goes — the
        // widest linked radio carries either way. Choosing one used to move a call onto it, which is
        // how a voice call ended up on eleven kilobits because somebody tapped a chip.
        if (r.IsAvailable && !r.IsLinked) { Emit($"[{r.Name}] bringing it up"); r.Link(); }
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

        // Every radio brings ITSELF up. None of them needs another one's help, and none of them can
        // take another one down.
        //
        // Wi-Fi Direct was excluded here and left to a broker that handed it credentials over BLE.
        // That made the slowest radio in the app a prerequisite for the fastest: one BLE link that
        // claimed to be up while refusing writes took calls, notes and the group with it. It now finds
        // its own peers over DNS-SD and settles who hosts from the ids both sides advertise, so there
        // is nothing left to broker and no race to avoid.
        foreach (var r in _order)
        {
            if (!r.IsAvailable || r.IsLinked) continue;

            Emit(ReferenceEquals(r, _selected) ? $"[{r.Name}] linking…" : $"[{r.Name}] also listening");
            try { r.Link(); } catch (Exception ex) { Emit($"[{r.Name}] could not listen: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Bring every radio up to meet one particular person.
    /// </summary>
    /// <remarks>
    /// All of them at once, quietly, and none of them waiting on another. Which one ends up carrying
    /// the traffic is not decided here and is not decided by the person — see <see cref="Widest"/>.
    /// A radio that has not been taught about meetings still comes up; it simply comes up for
    /// everybody rather than for somebody.
    /// </remarks>
    public void Link(AetherNet.Sample.Shared.Services.Meeting meeting)
    {
        AetherLinkService.Start();

        Emit($"meeting {meeting.PeerTag} — {(meeting.IStart ? "we open" : "they open")}");

        // Wi-Fi needs the rendezvous itself rather than a hint: it puts it on a multicast group and a
        // port that only the two of them can compute.
        if (_wifi is not null)
            _ = Task.Run(async () =>
            {
                try { await _wifi.MeetAsync(meeting.Rendezvous, meeting.IStart); }
                catch (Exception ex) { Emit($"[Wi-Fi] could not meet: {ex.Message}"); }
            });
        else Emit("[Wi-Fi] no radio");

        foreach (var r in _order)
        {
            if (!r.IsAvailable) continue;

            // Linked is not a reason to skip it. A radio that came up before the meeting arrived is
            // holding a link to whoever answered first, which is exactly the link that should be
            // replaced — and the radios that are already meeting the right person recognise their own
            // meeting and do nothing.
            try { r.Link(meeting); }
            catch (Exception ex) { Emit($"[{r.Name}] could not listen: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Give the foreground service back when nothing is linked.
    /// </summary>
    /// <remarks>
    /// Android only lets an app hold a connection off-screen while a foreground service is running, so
    /// it is needed exactly as long as there IS a connection and not a moment longer. It used to be
    /// taken on the first Link() and never given back — a permanent notification the person cannot
    /// dismiss, for radios that were often carrying nothing.
    /// </remarks>
    public void ReleaseIfIdle()
    {
        if (_order.Any(r => r.IsLinked)) return;
        AetherLinkService.Stop();
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
    public Task<bool> SendPacketAsync(byte[] packetBytes) =>
        SendPacketAsync(packetBytes, LaneFor(packetBytes));

    /// <summary>
    /// Send in a named lane, so a phone call is never queued behind a file transfer.
    /// </summary>
    public async Task<bool> SendPacketAsync(byte[] packetBytes, SendLane lane)
    {
        foreach (var r in Candidates())
        {
            var ok = await r.SendAsync(packetBytes, lane).ConfigureAwait(false);
            global::Android.Util.Log.Info("AetherBLE",
                $"app→radio {packetBytes.Length}B {lane} on {r.Name} linked={r.IsLinked} sent={ok}");
            if (ok) return true;
        }
        return false;
    }

    /// <summary>
    /// Read the lane off the packet itself, for callers that do not name one.
    /// </summary>
    /// <remarks>
    /// Only possible now that packets carry their real type. While everything was
    /// <see cref="PacketType.Data"/> with a string marker hidden inside the ciphertext, nothing out
    /// here could tell speech from a file — which is precisely why they shared a queue.
    /// </remarks>
    private static SendLane LaneFor(byte[] packetBytes)
    {
        try { return PacketPriority.Lane(PacketSerializer.Deserialize(packetBytes).Type); }
        catch { return SendLane.Interactive; }
    }

    /// <summary>
    /// The radios worth trying, best first.
    ///
    /// <para>
    /// Nobody is asked. The person picked a contact, not a transport — every radio tries at once and
    /// whichever got through and is widest carries, silently, handing over when a better one appears.
    /// See <see cref="RadioChoice"/> for the rule and for why it is best-through rather than
    /// first-through.
    /// </para>
    ///
    /// <para>
    /// The preferred radio used to come first outright, which meant a person could put a voice call on
    /// eleven kilobits by tapping a chip on a screen. It is now a preference about which radio is
    /// brought up, and no part of where traffic goes.
    /// </para>
    ///
    /// <para>
    /// Everything else linked still follows, so a send that fails on the best radio drops to the next
    /// rather than failing outright.
    /// </para>
    /// </summary>
    private IEnumerable<IRadio> Candidates()
    {
        var speeds = _order.Select(r =>
            new RadioSpeed(r.Name, r.IsLinked, r.Quality.ThroughputBps(), r.MaxBandwidthBps));

        var order = RadioChoice.Order(speeds, _carrying);

        if (order.Count == 0)
        {
            // Nothing linked at all. Still hand it to a radio, which reports the failure honestly
            // rather than the mesh inventing one.
            yield return _selected;
            yield break;
        }

        // Remembered so the next decision knows what is already carrying, and does not move the
        // traffic off it for a rounding difference — see RadioChoice.Wider.
        _carrying = order[0].Name;

        foreach (var named in order)
            if (_radios.TryGetValue(named.Name, out var r))
                yield return r;
    }

    /// <summary>Which radio is carrying, so a near-tie does not bounce the traffic between two.</summary>
    private string? _carrying;

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
        _out.LogInformation("[mesh] {Line}", line);

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

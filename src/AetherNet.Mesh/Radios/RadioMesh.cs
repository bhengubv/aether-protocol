// SPDX-License-Identifier: MIT
using AetherNet.Protocol;
using AetherNet.Routing;                 // the carry-for-a-third-node MeshRelay is the library's now
using AetherNet.Transport.Services;
using Microsoft.Extensions.Logging;
using System.Text;

namespace AetherNet.Mesh;

/// <summary>
/// Which of the device's radios the person has switched on. Every radio is on unless they switched it off — the
/// product never decides that for them; it tells them what each one does, on and off.
/// </summary>
public interface IRadioSwitches
{
    /// <summary>Whether the person has this radio switched on. True for a radio they never touched.</summary>
    bool IsOn(string radio);
}

/// <summary>
/// The real over-the-air mesh, the same on every system. Owns the device's address on the wire and a set of radios
/// (<see cref="IRadio"/>) that each system adds — Wi-Fi Direct and Bluetooth on a phone, the network a computer is on,
/// the internet relay on both — and moves real <see cref="MeshPacket"/>s to whoever is linked, over whichever linked
/// radio is widest.
/// </summary>
/// <remarks>
/// This was the phone's radio mesh, word for word; what was Android's — the foreground service it takes while radios
/// are up, the system log it writes to — is now a hook the phone fills in (<see cref="BringingUp"/>,
/// <see cref="NothingLinked"/>, <see cref="Handed"/>).
/// </remarks>
public abstract class RadioMesh : IRadioMesh, IDisposable
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
    private readonly ILogger _out;
    private readonly Dictionary<string, IRadio> _radios = new(StringComparer.Ordinal);
    private readonly List<IRadio> _order = new();
    private readonly string _localUhid;
    private readonly byte[] _routingKey;
    private readonly CircleDirectory? _circle;
    private readonly IRadioSwitches? _switches;

    /// <summary>
    /// Carrying for the people this device has added.
    /// </summary>
    /// <remarks>
    /// The thing that makes this a mesh rather than a set of pairs. Two people who have added each
    /// other are often out of range of each other; a third phone both of them added is not, and it
    /// passes the note without ever being able to read it.
    /// </remarks>
    private readonly MeshRelay _relay = new();

    private IRadio? _selected;

    /// <summary>The Wi-Fi already on the device, kept so a meeting can be handed to it.</summary>
    private AetherNet.Transport.Wifi.WifiTransportService? _wifi;

    /// <param name="me">The device's identity — the radios announce the same AetherTag the rest of the device uses.</param>
    /// <param name="logger">Where the mesh says what its radios are doing.</param>
    /// <param name="circle">Who this device has added, to recognise them behind a rotating address.</param>
    /// <param name="switches">Which radios the person has switched off; null when every one is on.</param>
    protected RadioMesh(IIdentityService me, ILogger logger, CircleDirectory? circle = null, IRadioSwitches? switches = null)
    {
        // The radio announces the SAME AetherTag the rest of the device uses. Generating one here would
        // give the device a third identity — the peer you linked with would not be the peer you added,
        // and it would change on every restart.
        ArgumentNullException.ThrowIfNull(me);
        LocalTag = me.AetherTag;
        _localUhid = LocalTag;

        // The wire address rotates off a key derived from the device's identity, asked for by purpose.
        // The identity itself never reaches a radio — and deriving the address from the public tag
        // instead would let anyone holding that tag compute every address this device will ever use.
        _routingKey = me.RoutingKey;
        _circle = circle;
        _out = logger ?? throw new ArgumentNullException(nameof(logger));
        _switches = switches;
    }

    /// <summary>This device's tag, as a radio needs it to announce itself.</summary>
    protected string LocalUhid => _localUhid;

    /// <summary>The key the rotating wire address is derived from — only a radio that rotates its address needs it.</summary>
    protected byte[] RoutingKey => _routingKey;

    /// <summary>Who this device has added.</summary>
    protected CircleDirectory? Circle => _circle;

    /// <summary>Add one of this system's radios. The order they are added in is the order they are listed in.</summary>
    protected void Register(IRadio r)
    {
        _radios[r.Name] = r;
        _order.Add(r);
        _selected ??= r;
        r.Status += s => Emit($"[{r.Name}] {s}");
        r.PeerLinked += p =>
        {
            Emit($"[{r.Name}] ● linked with {p}");
            // First contact — say hello so the two devices learn each other's transports before the
            // conversation forces the question. Wired to InitiateAsync outside the mesh.
            PeerLinked?.Invoke(p);
        };
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
            if (!Carry(bytes)) PacketReceived?.Invoke(RevealIdentity(bytes));
        };
    }

    /// <summary>
    /// Add the Wi-Fi the device is already on — the same radio on a phone and a computer.
    /// </summary>
    /// <remarks>
    /// Wi-Fi Direct builds a network out of nothing, which is the right answer in a field and a
    /// slow, fragile one in a kitchen where both handsets are three metres from the same access
    /// point. Two phones sat on one network for an afternoon unable to reach each other while a
    /// perfectly good link went unused — refusing to use it is not principle, it is waste.
    /// </remarks>
    /// <param name="trace">Where else it says what it is doing — the system's own log.</param>
    protected void AddWifi(Action<string>? trace = null)
    {
        _wifi = new AetherNet.Transport.Wifi.WifiTransportService(_localUhid);

        // Its own voice, or it has none.
        //
        // TransportRadio wraps a transport and raises ITS status, never the transport's — so
        // everything this radio said about itself went nowhere, and a radio that had not run looked
        // exactly like a radio that had. Silence from a layer is the wiring, not the code.
        _wifi.Status += s => { trace?.Invoke(s); Emit($"[Wi-Fi] {s}"); };

        Register(new TransportRadio(_wifi, _localUhid));
    }

    /// <summary>The radio to bring up first: the first of these the device has. A preference, never a restriction.</summary>
    protected void Prefer(params string[] names)
    {
        foreach (var name in names)
        {
            if (_radios.TryGetValue(name, out var r))
            {
                _selected = r;
                return;
            }
        }
    }

    /// <summary>One of this device's radios, by name.</summary>
    protected IRadio Radio(string name) => _radios[name];

    /// <summary>The radios are about to be brought up. A phone takes its foreground service here, before any link.</summary>
    protected virtual void BringingUp()
    {
    }

    /// <summary>Nothing is linked any more. A phone gives its foreground service back here.</summary>
    protected virtual void NothingLinked()
    {
    }

    /// <summary>A packet was handed to a radio — which, how big, whether it went. For a system that logs every one.</summary>
    protected virtual void Handed(string line)
    {
    }

    /// <summary>Whether the person has this radio switched on.</summary>
    public bool IsOn(string radio) => _switches?.IsOn(radio) ?? true;

    private IRadio Selected => _selected ?? throw new InvalidOperationException("a radio mesh with no radios");

    /// <summary>
    /// Pass a packet on if it belongs to two people this device has added.
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

    /// <summary>How many packets this device has carried for other people.</summary>
    public long Carried => _relay.Carried;

    public string LocalTag { get; }
    public IReadOnlyList<RadioInfo> Radios =>
        _order.Select(r => new RadioInfo(r.Name, r.IsAvailable, r.UnavailableReason, r.IsFixable, r.NeedsPermission)).ToArray();
    public string SelectedRadio => Selected.Name;

    /// <inheritdoc />
    public string LinkRadio
    {
        get
        {
            foreach (var r in Candidates())
                if (r.IsLinked) return r.Name;
            return Selected.Name;
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
    public bool IsSupported => Selected.IsAvailable;

    /// <summary>
    /// The radio actually carrying traffic right now: your preferred one while it holds a link, and
    /// otherwise whichever one does. The preference is a preference, not a restriction — a device that
    /// can still be reached over another radio is still reachable.
    /// </summary>
    private IRadio Active => Candidates().FirstOrDefault() ?? Selected;

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

    /// <summary>
    /// AetherTag → the transports that person can also carry, as the capability handshake negotiated
    /// them. Already the intersection of the two devices' transports, so every tag is one both ends have.
    /// </summary>
    /// <remarks>
    /// Fed from outside via <see cref="NotePeerTransports"/> — the handshake that produces this depends
    /// on the sender that depends on this mesh, so it cannot be injected without a cycle. Read on the
    /// send path by <see cref="EffectivePeerTransports"/> to prefer a radio the peer can actually hear.
    /// </remarks>
    private readonly Dictionary<string, IReadOnlySet<string>> _peerTransports = new(StringComparer.Ordinal);

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
    public event Action<string>? PeerLinked;
    public IReadOnlyList<string> Log { get { lock (_gate) { return _log.ToArray(); } } }

    /// <inheritdoc />
    public void NotePeerTransports(string peer, IReadOnlySet<string> transports)
    {
        if (string.IsNullOrEmpty(peer) || transports is null) return;

        lock (_gate)
        {
            // Bounded like _known: wire identities rotate, the Circle is small, and nothing here is
            // worth keeping across a restart — the next handshake re-establishes it in seconds.
            if (_peerTransports.Count > 32) _peerTransports.Clear();
            _peerTransports[peer] = transports;
        }

        Emit(transports.Count > 0
            ? $"● {peer} also carries [{string.Join(", ", transports)}]"
            : $"● {peer} shares no transport we advertised");
    }

    /// <summary>
    /// The transports every currently-linked, identified peer can also carry — the intersection of what
    /// the handshake negotiated with each of them, so a tag survives only if all of them have it.
    /// </summary>
    /// <remarks>
    /// Null when nothing has been negotiated for anyone actually on the wire, which drops the choice
    /// straight back to the peer-agnostic ranking it always used — a message never waits on a handshake
    /// that has not finished.
    /// </remarks>
    private IReadOnlySet<string>? EffectivePeerTransports()
    {
        // Snapshot under the lock, then resolve identities without holding it — Recognise is somebody
        // else's code and must never be called inside our gate.
        Dictionary<string, IReadOnlySet<string>> negotiated;
        lock (_gate)
        {
            if (_peerTransports.Count == 0) return null;
            negotiated = new Dictionary<string, IReadOnlySet<string>>(_peerTransports, StringComparer.Ordinal);
        }

        HashSet<string>? shared = null;
        foreach (var r in _order)
        {
            if (!r.IsLinked) continue;
            foreach (var wire in r.Peers)
            {
                string? tag;
                lock (_gate) { _known.TryGetValue(wire, out tag); }
                tag ??= _circle?.Recognise(wire);
                if (tag is null || !negotiated.TryGetValue(tag, out var theirs)) continue;

                if (shared is null) shared = new HashSet<string>(theirs, StringComparer.Ordinal);
                else shared.IntersectWith(theirs);
            }
        }

        return shared is { Count: > 0 } ? shared : null;
    }

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
        if (!IsOn(r.Name)) { Emit($"[{r.Name}] switched off — not brought up"); RaiseChanged(); return; }
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
    /// <para>
    /// A radio the person switched off is the one exception: it stays down, and says so.
    /// </para>
    /// </summary>
    public void Link()
    {
        BringingUp();

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
            if (!IsOn(r.Name)) { Emit($"[{r.Name}] switched off — not brought up"); continue; }

            Emit(ReferenceEquals(r, _selected) ? $"[{r.Name}] linking…" : $"[{r.Name}] also listening");
            try { r.Link(); } catch (Exception ex) { Emit($"[{r.Name}] could not listen: {Why(ex)}"); }
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
    public void Link(AetherNet.Rendezvous.Meeting meeting)
    {
        BringingUp();

        Emit($"meeting {meeting.PeerTag} — {(meeting.IStart ? "we open" : "they open")}");

        // Wi-Fi needs the rendezvous itself rather than a hint: it puts it on a multicast group and a
        // port that only the two of them can compute.
        if (_wifi is not null && IsOn("Wi-Fi"))
            _ = Task.Run(async () =>
            {
                try { await _wifi.MeetAsync(meeting.Rendezvous, meeting.IStart); }
                catch (Exception ex) { Emit($"[Wi-Fi] could not meet: {ex.Message}"); }
            });
        else Emit(_wifi is null ? "[Wi-Fi] no radio" : "[Wi-Fi] switched off");

        foreach (var r in _order)
        {
            if (!r.IsAvailable || !IsOn(r.Name)) continue;

            // Linked is not a reason to skip it. A radio that came up before the meeting arrived is
            // holding a link to whoever answered first, which is exactly the link that should be
            // replaced — and the radios that are already meeting the right person recognise their own
            // meeting and do nothing.
            try { r.Link(meeting); }
            catch (Exception ex) { Emit($"[{r.Name}] could not listen: {Why(ex)}"); }
        }
    }

    /// <inheritdoc />
    public void MeetPeer(AetherNet.Rendezvous.Meeting meeting)
    {
        // Only the network leg meets peers pairwise. Wi-Fi Direct is the Circle's ONE shared group and
        // is brought up by Link(meeting)/FastRadioService for the elected host — driving it per-peer here
        // would have it thrash between groups. So this asks just the Wi-Fi/LAN transport to keep a
        // rendezvous for THIS peer, alongside any others it is already keeping: many at once, one per
        // contact, which is what lets two devices on the same network reach each other even when the tags
        // elected some third, absent peer to host.
        if (_wifi is null || !IsOn("Wi-Fi")) return;
        _ = Task.Run(async () =>
        {
            try { await _wifi.MeetAsync(meeting.Rendezvous, meeting.IStart); }
            catch (Exception ex) { Emit($"[Wi-Fi] could not meet {meeting.PeerTag}: {ex.Message}"); }
        });
    }

    /// <inheritdoc />
    public bool IsReachable(string aetherTag)
    {
        if (string.IsNullOrEmpty(aetherTag)) return false;

        // A device can hold several links at once now, so this asks per peer rather than reading the one
        // PeerTag. A Wi-Fi/LAN link names the peer by its tag directly; the rotating-address radios name
        // a wire we turn back into a tag the same way PeerTag does.
        foreach (var r in _order)
        {
            if (!r.IsLinked) continue;
            foreach (var wire in r.Peers)
            {
                if (string.Equals(wire, aetherTag, StringComparison.Ordinal)) return true;

                string? tag;
                lock (_gate) { _known.TryGetValue(wire, out tag); }
                tag ??= _circle?.Recognise(wire);
                if (string.Equals(tag, aetherTag, StringComparison.Ordinal)) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Let go of whatever holds the radios up when nothing is linked.
    /// </summary>
    /// <remarks>
    /// On a phone, Android only lets an app hold a connection off-screen while a foreground service is
    /// running, so it is needed exactly as long as there IS a connection and not a moment longer. It used
    /// to be taken on the first Link() and never given back — a permanent notification the person cannot
    /// dismiss, for radios that were often carrying nothing.
    /// </remarks>
    public void ReleaseIfIdle()
    {
        if (_order.Any(r => r.IsLinked)) return;
        NothingLinked();
    }

    public async Task SendTestAsync(string text)
    {
        var selected = Selected;
        if (selected.PeerTag is null) { Emit($"[{selected.Name}] no peer linked yet"); return; }
        var pkt = new MeshPacket
        {
            Type = PacketType.Data,
            // The header is readable before anything is decrypted, so it carries where-to-send, not
            // who-we-are. The identity travels inside the session.
            SourceUhid = WireAddress.For(_routingKey),
            DestinationUhid = selected.PeerTag,
            Payload = Encoding.UTF8.GetBytes(text),
            Ttl = 7,
        };
        var ok = await selected.SendAsync(PacketSerializer.Serialize(pkt)).ConfigureAwait(false);
        Emit(ok ? $"[{selected.Name}] ▶ sent: \"{text}\"" : $"[{selected.Name}] ▶ send failed");
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
        packetBytes = HideIdentity(packetBytes);
        foreach (var r in Candidates())
        {
            var ok = await r.SendAsync(packetBytes, lane).ConfigureAwait(false);
            Handed($"app→radio {packetBytes.Length}B {lane} on {r.Name} linked={r.IsLinked} sent={ok}");
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
    /// E2 — take the stable, trackable UHID off the wire. On any lane (voice, attachments, group calls,
    /// as well as chat) rewrite the source and destination to rotating ERIDs, once we hold the peer's
    /// routing key and the packet is not identity bootstrap. Chat already does this at its own,
    /// cross-platform seam; this catches every other lane that leaves this device. A packet that already
    /// carries ERIDs (chat's), a broadcast, a bootstrap type, or a peer whose key we do not hold passes
    /// through untouched.
    /// </summary>
    private byte[] HideIdentity(byte[] bytes)
    {
        if (_circle is null) return bytes;

        MeshPacket packet;
        try { packet = PacketSerializer.Deserialize(bytes); }
        catch { return bytes; }

        // These carry identity before any key is known and MUST keep the stable tag, or the routing-key
        // exchange (and thus recognition) could never bootstrap.
        if (packet.Type is PacketType.Hello or PacketType.HelloAck or PacketType.EridAnnounce
            or PacketType.PreKeyRequest or PacketType.PreKeyResponse)
            return bytes;

        if (string.IsNullOrEmpty(packet.DestinationUhid)) return bytes;      // broadcast — cannot ERID to one peer
        if (_circle.AddressFor(packet.DestinationUhid) is not { } peerErid) return bytes; // key unknown, or already an ERID

        packet.SourceUhid = _circle.MyAddress();
        packet.DestinationUhid = peerErid;
        return PacketSerializer.Serialize(packet);
    }

    /// <summary>
    /// The receive-side inverse of <see cref="HideIdentity"/>: resolve a delivered packet's source ERID
    /// back to the sender's stable tag so the ratchet keyed on that tag can decrypt it. Runs only on the
    /// deliver-here path (after the relay decision, which works on the raw wire address); a stable tag or
    /// an address we cannot resolve passes through unchanged.
    /// </summary>
    private byte[] RevealIdentity(byte[] bytes)
    {
        if (_circle is null) return bytes;

        MeshPacket packet;
        try { packet = PacketSerializer.Deserialize(bytes); }
        catch { return bytes; }

        var source = _circle.Recognise(packet.SourceUhid);
        if (source is null) return bytes;

        packet.SourceUhid = source;
        return PacketSerializer.Serialize(packet);
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
            new RadioSpeed(r.Name, r.IsLinked, r.Quality.ThroughputBps(), r.MaxBandwidthBps)
            {
                // Tag each radio with its transport so the choice can prefer one the peer also carries.
                Transport = TransportCapability.TagFor(r.Name),
            });

        var order = RadioChoice.Order(speeds, _carrying, EffectivePeerTransports());

        if (order.Count == 0)
        {
            // Nothing linked at all. Still hand it to a radio, which reports the failure honestly
            // rather than the mesh inventing one.
            yield return Selected;
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
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Why a radio failed, in a line that says something: the message, or — where the system gave none, as WinRT often
    /// does — what was thrown and its code, so a failure is never an empty line in the log.
    /// </summary>
    private static string Why(Exception ex) =>
        ex.Message is { Length: > 0 } message ? message : $"{ex.GetType().Name} 0x{ex.HResult:X8}";

    /// <summary>Say what a radio is doing — in the mesh's log, and the system's.</summary>
    protected void Emit(string line)
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

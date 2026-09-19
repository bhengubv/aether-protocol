// SPDX-License-Identifier: MIT

using System.Text;
using AetherNet.Messaging;
using AetherNet.Messaging.Models;
using AetherNet.PreKeys;
using AetherNet.Protocol;
using AetherNet.Sample.Shared.Data;
using AetherNet.Security.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// Real messaging over the mesh. Not a demo surface: a message you type is end-to-end encrypted with
/// the Signal double ratchet, carried to the other phone over the radio, decrypted there, and stored
/// on both devices. No server sees it, because there isn't one.
///
/// The session is established the Signal way, over the mesh: each device publishes a pre-key bundle,
/// asks its peer for theirs (<see cref="IPreKeyExchangeService"/>, packet types 25/26), and performs
/// X3DH locally. Until that completes a message is kept <b>pending</b> — never sent in the clear —
/// and flushed the moment the session exists.
/// </summary>
public sealed class ChatService
{
    /// <summary>Marks a chat payload inside a generic Data packet.</summary>
    private const string Marker = "AETHERMSG";

    /// <summary>Marks a delivery receipt. Same length as <see cref="Marker"/> on purpose.</summary>
    private const string AckMarker = "AETHERACK";

    /// <summary>Marks anything to do with a group — a message in one, or news of one.</summary>
    private const string GroupMarker = "AETHERGRP";

    /// <summary>
    /// Marks a session ping: an empty message whose only job is to exist.
    ///
    /// <para>
    /// When a session is rebuilt, the far side does not know. It still holds the old one and keeps
    /// sealing under a root key this phone has just thrown away, so every message between them fails
    /// its tag — forever, because neither has a reason to try again. What breaks the deadlock is one
    /// message under the new session: it carries the pre-key material, and processing it replaces the
    /// stale session on the other side.
    /// </para>
    ///
    /// <para>
    /// Chat has been getting this for free by flushing pending messages after a repair. A call has
    /// nothing pending, so it deadlocked — which is why voice could repair correctly and still never
    /// connect. This makes the nudge explicit rather than a side effect of having a backlog.
    /// </para>
    /// </summary>
    private const string PingMarker = "AETHERPNG";

    /// <summary>
    /// Carries this phone's routing key to one contact, sealed in their session.
    ///
    /// <para>
    /// It rides the encrypted path and nothing else, because a routing key read off the air by anybody
    /// else would let them recognise this phone behind every address it ever rotates through — the
    /// exact thing the rotation exists to prevent. The contact announce is not an option: it goes out
    /// in clear.
    /// </para>
    /// </summary>
    private const string CircleMarker = "AETHERCIR";

    /// <summary>
    /// Tells one contact that this phone is carrying traffic for the Circle, and where to reach it.
    ///
    /// <para>
    /// Sealed in their session like the routing key, and for the same reason: a proxy address on the
    /// air is an invitation to anyone listening to route their traffic through a stranger's phone, and
    /// to that phone's owner to carry it.
    /// </para>
    /// </summary>
    private const string ProxyMarker = "AETHERPXY";


    /// <summary>
    /// The kind of an app payload, carried as the first byte of the plaintext the messaging layer seals.
    /// The library moves opaque encrypted blobs and treats the kind as the caller's business, so the
    /// discriminator lives here, inside the ciphertext — an improvement on the old cleartext marker that
    /// sat on the wire in front of it.
    /// </summary>
    private const byte KindText = 0x01;
    private const byte KindGroup = 0x02;

    /// <summary>
    /// How long a message may sit unconfirmed before we call it failed. A radio hop is milliseconds;
    /// anything past this is not slow, it is lost.
    /// </summary>
    private static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Message ids we have handed to the messaging layer and are still waiting on a delivery receipt for.
    /// The receipt now comes back as a <see cref="MessagingService"/> <c>DeliveryConfirmed</c> event
    /// rather than our own ack packet, but the give-up timer that turns silence into a visible failure is
    /// the same.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _awaitingConfirm = new();

    private readonly AetherStore _store;
    private readonly IIdentityService _me;
    private readonly IRadioMesh? _radio;
    private readonly ISignalProtocolService _signal;
    private readonly IPreKeyExchangeService _preKeys;
    private readonly AttachmentService? _attachments;

    /// <summary>
    /// Brings the wide radio up. Optional — null on hosts that have none.
    ///
    /// <para>
    /// Wi-Fi Direct is the core radio, not a special case for calls. Every phone has it, and it is
    /// the only one measured to carry real traffic: fifty frames a second each way against BLE's
    /// eleven kilobits (PROTOCOL_SPEC §5.5). It was being raised by <c>CallService</c> alone, so
    /// everything else — messages, receipts, notes — crawled over BLE while the fast radio sat idle,
    /// and a ninety-kilobyte voice note took over a minute on a phone that could move it in under a
    /// second.
    /// </para>
    ///
    /// <para>
    /// Raising it any earlier than this would be unsafe: forming a group needs both sides to agree
    /// who hosts, and the broker settles that from the two tags — which are only known once there is
    /// a session. That is exactly the moment below.
    /// </para>
    /// </summary>
    private readonly CircleDirectory? _circle;
    private readonly ProxyDirectory? _proxies;
    private readonly IAppShareService? _appShare;
    private readonly IRelayHost? _gateway;
    private readonly FastRadioService? _fastRadio;

    private readonly ILogger _log;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly SessionRepair _repair;

    /// <summary>Contacts already handed our routing key this run — it is long-term, so once is enough.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _sharedCircleKey = new(StringComparer.Ordinal);
    private bool _bundlePublished;

    /// <summary>
    /// The reliable messaging core, shared with every other host of the protocol. Sealing, the outbox,
    /// retries, delivery receipts, and the queue-never-plaintext rule all live here now, so chat no longer
    /// carries its own copy of them. This service is the domain adapter on top: it maps a
    /// <see cref="ChatMessage"/> to a <see cref="MeshMessage"/> and back, and keeps the parts that are the
    /// app's own — groups, attachments, handoff, session repair, the rotating-address Circle.
    /// </summary>
    private readonly IMessagingService _messaging;

    /// <summary>
    /// The inbound pump. Data and Ack go to <see cref="_messaging"/>; the app's own kinds — a session
    /// ping, a routing-key share, a relay offer, a handoff — are registered here and handled below. This
    /// replaces the hand-rolled <c>OnPacket</c> switch, and the relay it used to run is now the library's.
    /// </summary>
    private readonly MeshInboundDispatcher _dispatcher;

    public ChatService(
        AetherStore store,
        IIdentityService me,
        ISignalProtocolService signal,
        IPreKeyExchangeService preKeys,
        IMessagingService messaging,
        MeshInboundDispatcher dispatcher,
        IRadioMesh? radio = null,
        AttachmentService? attachments = null,
        CircleDirectory? circle = null,
        ProxyDirectory? proxies = null,
        IAppShareService? appShare = null,
        IRelayHost? gateway = null,
        FastRadioService? fastRadio = null,
        ILoggerFactory? loggerFactory = null)
    {
        _circle = circle;
        _proxies = proxies;
        _appShare = appShare;
        _gateway = gateway;
        _fastRadio = fastRadio;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _me = me ?? throw new ArgumentNullException(nameof(me));
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
        _preKeys = preKeys ?? throw new ArgumentNullException(nameof(preKeys));
        _messaging = messaging ?? throw new ArgumentNullException(nameof(messaging));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _attachments = attachments;
        _radio = radio;
        _log = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<ChatService>();
        _repair = new SessionRepair();

        _preKeys.BundleReceived += OnBundleReceived;

        // The reliable core hands back four things: a decrypted message for us, a receipt for one of
        // ours, a nudge that a message could not go because there is no session yet, and — the one that
        // used to be ours to notice — a message that would not open. That last is how a diverged ratchet
        // announces itself; the core cannot repair it (it holds no pre-keys), so it tells us and we do.
        _messaging.MessageReceived += (_, m) => _ = OnMessageReceivedAsync(m);
        _messaging.DeliveryConfirmed += (_, receipt) => OnDeliveryConfirmed(receipt);
        _messaging.SessionRequired += (_, peer) => _ = EnsureSessionAsync(peer);
        _messaging.DecryptFailed += (_, peer) => _ = RepairSessionAsync(peer);

        // The app's own kinds ride their own packet types; register each so the one inbound pump routes
        // it here instead of every service re-writing the same deserialize-and-switch.
        RegisterInboundHandlers();

        // Attachments share this session and had no way to recover from a broken one. Chat is where
        // repair lives — voice already borrows it for the same reason — so notes borrow it too rather
        // than growing a second copy of the trickiest code in the app.
        if (_attachments is not null)
        {
            _attachments.SessionLooksBroken += OnAttachmentSessionBroken;

            // An app that arrives has to offer itself, or it is a forty-megabyte bubble nobody can open.
            _attachments.Arrived += OnAttachmentArrived;
        }
        if (_radio is not null)
        {
            // Inbound is the dispatcher's now (wired to the radio by the host); chat only still cares
            // about a link coming up, to finish whatever was waiting on it.
            _radio.Changed += OnRadioChanged;
        }
    }

    /// <summary>
    /// Point the one inbound pump at the app's own kinds. Chat text and group messages arrive as sealed
    /// Data through <see cref="_messaging"/>; everything here is a control message with bespoke delivery
    /// semantics the reliable outbox would get wrong — a ping must not be retried, a routing key must go
    /// once, a handoff draft must never reappear stale — so each keeps its own send and is only routed in
    /// here on the way back. The dispatcher has already resolved the source to a stable tag.
    /// </summary>
    private void RegisterInboundHandlers()
    {
        _dispatcher.Register(PacketType.Heartbeat, (p, _) => ReceivePingAsync(p.SourceUhid, p.Payload));
        _dispatcher.Register(PacketType.EridAnnounce, (p, _) => ReceiveCircleAsync(p.SourceUhid, p.Payload));
        _dispatcher.Register(PacketType.CircuitRelayControl, (p, _) => ReceiveProxyAsync(p.SourceUhid, p.Payload));
        _dispatcher.Register(PacketType.ChannelMessage, (p, _) => ReceiveHandoffAnyAsync(p.SourceUhid, p.Payload));
        _dispatcher.Register(PacketType.PreKeyRequest, (p, _) => HandlePreKeyAsync(p));
        _dispatcher.Register(PacketType.PreKeyResponse, (p, _) => HandlePreKeyAsync(p));
    }

    /// <summary>
    /// Handoff want and handoff give share one packet type (a session-scoped control message), so the
    /// marker in the payload is what tells them apart — the last place a marker still discriminates,
    /// because these two are one kind on the wire.
    /// </summary>
    private Task ReceiveHandoffAnyAsync(string? senderTag, byte[] payload)
    {
        if (StartsWith(payload, Handoff.WantMarker)) return ReceiveHandoffWantAsync(senderTag, payload);
        if (StartsWith(payload, Handoff.Marker)) return ReceiveHandoffAsync(senderTag, payload);
        return Task.CompletedTask;
    }

    private static bool StartsWith(byte[] payload, string marker)
    {
        var m = Encoding.UTF8.GetBytes(marker);
        if (payload.Length < m.Length) return false;
        for (var i = 0; i < m.Length; i++)
            if (payload[i] != m[i]) return false;
        return true;
    }

    /// <summary>
    /// A radio link coming up is the moment to finish whatever was waiting on it: start the secure
    /// session if there is not one, then push everything still undelivered.
    /// <para>
    /// Without this, a conversation that has lost its session — both phones restarted, say — sits on
    /// "setting up encryption…" forever even with a perfectly good link, because the handshake was
    /// only ever attempted once, when the page opened, and nothing asked again.
    /// </para>
    /// </summary>
    private void OnRadioChanged()
    {
        var peer = _radio is { IsLinked: true } ? _radio.PeerTag : null;

        // Changed fires for every line the radio logs, and resuming sends packets, which log — so
        // acting on the event itself feeds itself and the radio drowns in retries. Only a real
        // transition, into a link with a particular peer, is worth doing anything about.
        var previous = Interlocked.Exchange(ref _linkedPeer, peer);
        if (string.IsNullOrEmpty(peer) || peer == previous) return;

        foreach (var tag in PeersToResume(peer)) _ = ResumeAsync(tag);
    }

    /// <summary>
    /// A note could not be opened from this peer. Rebuild the session, then chase the transfer again —
    /// resume asks only for what is missing, so nothing already received is fetched twice.
    /// </summary>
    private void OnAttachmentSessionBroken(string peer)
    {
        if (string.IsNullOrEmpty(peer)) return;

        _ = Task.Run(async () =>
        {
            // Just ask for the rebuild. Chasing the transfer here would be too early — the session is
            // not back until the peer answers with a bundle, and FlushAsync is what runs then.
            T($"a note from {peer} would not open — rebuilding the session");
            await RepairSessionAsync(peer).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// Which conversations a new link is worth reviving.
    /// <para>
    /// The radio names its peer with the rotating address it saw in the handshake, because the
    /// long-term identity deliberately never travels in clear and only arrives inside the session.
    /// That address is not a person and nothing is filed under it, so taking it at face value would
    /// flush an empty conversation and leave the real backlog sitting there — which is exactly what
    /// two phones were caught doing, rebuilding their link every thirty seconds and re-sending
    /// nothing. When the radio can only offer an address, go by what is actually owed instead.
    /// </para>
    /// </summary>
    private IEnumerable<string> PeersToResume(string radioPeer)
    {
        if (AetherNet.Identity.AetherNetTag.TryParse(radioPeer, out _)) return [radioPeer];

        var owed = _store.GetPeersWithUnsentMessages();
        return owed.Count > 0 ? owed : [];
    }

    /// <summary>Who the radio was linked to last time it told us, so we can spot a real change.</summary>
    private string? _linkedPeer;

    /// <summary>
    /// Get a conversation moving again over a link that has just come up. The handshake is retried a
    /// few times because the other phone may still be starting up and unable to answer yet — one
    /// unanswered request must not strand the conversation.
    /// </summary>
    private async Task ResumeAsync(string peerTag)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                await EnsureSessionAsync(peerTag).ConfigureAwait(false);

                // Notes stall too, and nothing was chasing them: a transfer that stopped stayed
                // stopped forever. This is the same moment a conversation is revived, and the same
                // peer — worked out by PeersToResume, which already knows a wire address is not a
                // person.
                if (_signal.HasSession(peerTag) && _attachments is not null)
                    _ = _attachments.ResumeAllWithAsync(peerTag);

                if (_signal.HasSession(peerTag))
                {
                    await FlushAsync(peerTag).ConfigureAwait(false);
                    Changed?.Invoke();
                    return;
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not resume the conversation with {Peer}", peerTag);
            }

            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            if (_radio is not { IsLinked: true }) return;   // link went away; nothing to retry onto
        }
    }

    /// <summary>Raised when a conversation changes, so the UI can re-render.</summary>
    public event Action? Changed;

    /// <summary>
    /// Running commentary on the message path, for whoever can actually surface it — on a phone the
    /// host wires this to the system log. Without it a receipt that never arrives is indistinguishable
    /// from one that was never sent.
    /// </summary>
    public event Action<string>? Trace;

    private void T(string m) => Trace?.Invoke(m);

    /// <summary>
    /// Sign a contribution with this device's identity key.
    /// <para>
    /// Provenance has to be checkable, not merely claimed: a member's phone is told who wrote a
    /// message, and without a signature it has no way to confirm it — nor does any third phone the
    /// message reaches later. Cheap to add now; the docs are blunt that it is brutal to retrofit
    /// trust onto a corpus collected without it.
    /// </para>
    /// </summary>
    private byte[] SignContribution(byte[] body) =>
        _signal.SignDataAsync(body).GetAwaiter().GetResult();

    public IReadOnlyList<ChatMessage> Conversation(string peerTag) => _store.GetMessages(peerTag);

    public IReadOnlyList<ChatMessage> Latest() => _store.GetLatestPerPeer();

    /// <summary>The groups this phone is in — they belong in the chat list beside everyone else.</summary>
    public IReadOnlyList<GroupRecord> Groups() => _store.GetGroups();

    /// <summary>The group with this id, or null if the conversation is with a person.</summary>
    public GroupRecord? Group(string id) => _store.GetGroup(id);

    /// <summary>Who is in a group.</summary>
    public IReadOnlyList<string> GroupMembers(string id) => _store.GetGroupMembers(id);

    /// <summary>True once there is a secure session with this peer — messages flow immediately.</summary>
    public bool IsSecure(string peerTag) => !string.IsNullOrEmpty(peerTag) && _signal.HasSession(peerTag);

    /// <summary>
    /// Publish our pre-key bundle so peers can start a session with us, and ask a peer for theirs.
    /// Safe to call repeatedly — the bundle is published once per run.
    /// </summary>
    public async Task EnsureSessionAsync(string peerTag, CancellationToken cancellationToken = default)
    {
        // A session cannot be built over a radio that is down, so this needs it too — and this is the
        // path a call takes before it rings.
        _fastRadio?.Wake();

        if (string.IsNullOrEmpty(peerTag) || _radio is null) return;

        await EnsureLocalBundleAsync(cancellationToken).ConfigureAwait(false);

        if (_signal.HasSession(peerTag)) return;

        // Maybe their bundle already arrived unsolicited; otherwise ask over the radio.
        var known = _preKeys.GetReceivedBundle(peerTag);
        if (known is not null)
        {
            await AdoptBundleAsync(peerTag, known, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _preKeys.RequestBundleAsync(peerTag, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Publish this device's pre-key bundle. A node must have one ready <b>before</b> anyone asks —
    /// otherwise it cannot answer a pre-key request and no session can ever start.
    /// </summary>
    private async Task EnsureLocalBundleAsync(CancellationToken cancellationToken)
    {
        if (_bundlePublished) return;
        await RefreshLocalBundleAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Publish a brand-new bundle, spent one-time key and all.
    /// <para>
    /// Not something to do casually — every bundle costs a one-time key — but a handshake that is about
    /// to happen needs one nobody has used. Publishing once at startup is enough for exactly one
    /// session with one peer, which is fine right up until the first repair.
    /// </para>
    /// </summary>
    private async Task RefreshLocalBundleAsync(CancellationToken cancellationToken = default)
    {
        await _sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var bundle = await _signal.GeneratePreKeyBundleAsync(_me.AetherTag, cancellationToken).ConfigureAwait(false);
            _preKeys.SetLocalBundle(bundle);
            _bundlePublished = true;
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    /// <summary>
    /// Send a message. It is stored locally either way; if there is no secure session yet it stays
    /// <b>pending</b> rather than going out unprotected, and leaves as soon as the session is up.
    /// </summary>
    public async Task SendAsync(string peerTag, string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(peerTag) || string.IsNullOrWhiteSpace(text)) return;

        var message = new ChatMessage(
            Id: Guid.NewGuid().ToString("N"),
            PeerTag: peerTag,
            // Strip the header marker out of anything typed, so a caption can never be read as one.
            Body: AttachmentRef.Clean(text).Trim(),
            Mine: true,
            State: ChatMessage.Pending,
            SentMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        _store.SaveMessage(message);
        Changed?.Invoke();

        // Wake the radio here, where something first needs it — not only in FlushAsync, which runs
        // once a link already exists. Waking on the settle-up path alone meant the first message of a
        // conversation sat pending against "not connected" forever: the thing that would have brought
        // the radio up was itself waiting for the radio to be up.
        _fastRadio?.Wake();

        await EnsureSessionAsync(peerTag, cancellationToken).ConfigureAwait(false);
        await TryDeliverAsync(message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Settle up with a peer: push everything they do not have yet — anything still pending, and
    /// anything that went out but was never confirmed — and pay any receipts we owe them. Called when a
    /// session comes up and whenever we hear from them.
    /// <para>
    /// The receipts go first. They are the cheapest thing on the link and the thing someone is actively
    /// waiting on: until one arrives, a message that is already sitting on this phone is showing as a
    /// failure on theirs.
    /// </para>
    /// </summary>
    public async Task FlushAsync(string peerTag, CancellationToken cancellationToken = default)
    {
        // Receipts are the reliable core's job now — it acks every message it delivers — so there is no
        // owed-receipt ledger to settle here. What is still ours is the backlog: the messages that could
        // not go before, re-offered now that there is a session. The core drops the plaintext of anything
        // it had to queue, so re-sending from our own store is the only thing that can carry it.
        foreach (var unsent in _store.GetUnsentMessages(peerTag))
            await TryDeliverAsync(unsent, cancellationToken).ConfigureAwait(false);

        // Notes are owed too. This is the one moment that means "there is a working session with them
        // right now" — which is what a stalled transfer has been waiting for, and what repairing a
        // session cannot say for itself: RepairSessionAsync only ASKS for a fresh bundle, and the
        // session does not exist until the reply lands. Resuming straight after the ask found no
        // session and skipped, so a note sat at 26 of 29 chunks through a repair that had worked.
        // Ask once now, and keep asking if they cannot answer yet — repairing a session leaves a gap
        // in which a request is simply lost, and one well-timed ask cannot cover it.
        if (_attachments is not null)
        {
            await _attachments.ResumeAllWithAsync(peerTag, cancellationToken).ConfigureAwait(false);
            _attachments.Chase(peerTag);
        }

        // A session is the only place a routing key can safely travel, so this is the moment to hand
        // it over — the contact is real, the session is up, and until they have it their beacon is
        // indistinguishable from a stranger's.
        if (_circle is not null && _signal.HasSession(peerTag))
            _ = ShareRoutingKeyAsync(peerTag, cancellationToken);

        // Something to send is exactly when the fast radio is worth having up, and the only time it
        // is. Holding it the rest of the day cost this phone its own Wi-Fi and carried nothing.
        _fastRadio?.Wake();

        // The fast radio is not asked for here at all. FastRadioService brings it up from the contact
        // list, which it can do before a single message exists — asking from here made chat a
        // prerequisite for the radio, and the radio a prerequisite for chat. It finds its own peers over DNS-SD and
        // settles who hosts from the ids both sides advertise, so a message being sent is not news to
        // it — by the time there is a message, the group is either already up or coming up on its own.
        //
        // Asking from here made chat a trigger for group formation, which put the two phones into a
        // race: the radio's own discovery and this call both tried to form a group, and the one that
        // won locked the other out.
    }

    /// <summary>
    /// Send a recorded note — a voice note, a video note.
    ///
    /// <para>
    /// The message is saved and shown at once, naming the note by content hash, while the bytes go
    /// separately and take as long as they take. On the measured BLE link a ten-second voice note
    /// crosses in about seven seconds (PROTOCOL_SPEC §5.5) — far too slow to be a call, and perfectly
    /// fine for something nobody is waiting on in real time.
    /// </para>
    ///
    /// <para>
    /// Returns false when there is nothing to send or no way to move it. It does <b>not</b> return
    /// false merely because the session is not up yet: the message is stored pending, exactly as a
    /// typed one is, and leaves when the session does.
    /// </para>
    /// </summary>
    public async Task<bool> SendNoteAsync(
        string peerTag, byte[] bytes, string contentType, string name, string caption = "",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(peerTag) || bytes is null || bytes.Length == 0) return false;
        if (_attachments is null)
        {
            _log.LogWarning("Cannot send a note — this host has no attachment transport");
            return false;
        }

        // The bytes are stored and offered first, so the hash on the message is one this phone can
        // actually serve. A message naming content nobody holds is a permanently broken bubble.
        var descriptor = await _attachments
            .SendAsync(peerTag, bytes, contentType, name, cancellationToken)
            .ConfigureAwait(false);

        var message = new ChatMessage(
            Id: Guid.NewGuid().ToString("N"),
            PeerTag: peerTag,
            Body: AttachmentRef.Clean(caption).Trim(),
            Mine: true,
            State: ChatMessage.Pending,
            SentMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            AttachmentHash: descriptor.RootHash,
            AttachmentType: contentType,
            AttachmentBytes: descriptor.TotalBytes);

        _store.SaveMessage(message);
        Changed?.Invoke();

        await EnsureSessionAsync(peerTag, cancellationToken).ConfigureAwait(false);
        await TryDeliverAsync(message, cancellationToken).ConfigureAwait(false);
        return true;
    }

    // ── Send path ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The body as the other phone will read it: a header naming the note, then the caption.
    ///
    /// <para>
    /// A message with nothing attached produces exactly the bytes it always did, which is the point —
    /// text is untouched by this and cannot be broken by it.
    /// </para>
    /// </summary>
    private static string OnTheWire(ChatMessage message) => message.HasAttachment
        ? new AttachmentRef(message.AttachmentHash!, message.AttachmentType ?? "application/octet-stream", message.AttachmentBytes)
            .Encode(message.Body)
        : message.Body;

    private async Task TryDeliverAsync(ChatMessage message, CancellationToken cancellationToken)
    {
        // Already handed over with its receipt timer running. Sending again would put a second copy on a
        // link busy carrying the first, and every flush would multiply the backlog.
        if (_awaitingConfirm.ContainsKey(message.Id)) return;

        // The reliable core does the sealing, the outbox, the retry and the delivery receipt now. It
        // seals to the stable tag; the wire carries a rotating ERID because the transport swaps it on the
        // way out. The kind byte tells the far side this is chat text — a note is chat text with an
        // attachment header in front of the caption.
        var mesh = new MeshMessage
        {
            Id = ToGuid(message.Id),
            RecipientUhid = message.PeerTag,
            MessageType = "text",
        };
        var plaintext = WithKind(KindText, OnTheWire(message));

        try
        {
            // Start waiting before the send returns: a close peer can confirm while we are still inside
            // the call, and a receipt that arrives before we are listening would be lost.
            _awaitingConfirm[message.Id] = 0;

            if (await _messaging.SendAsync(mesh, plaintext, cancellationToken).ConfigureAwait(false))
            {
                // Handed to a transport. "Sent" is not "delivered" — the receipt upgrades it, and until
                // then the give-up timer turns silence into an honest failure rather than a tick that
                // lies. Never write over a "delivered": on a fast link the receipt can beat this line.
                _store.SetMessageStateUnlessDelivered(message.Id, ChatMessage.Sent);
                Changed?.Invoke();
                _ = FailIfUnconfirmedAsync(message.Id, message.PeerTag);
            }
            else
            {
                // Queued — no session yet, or no path right now. It stays pending and the next flush
                // re-offers it; the reliable core drops the plaintext when it queues, so re-sending is
                // ours to do, not something it can retry for us.
                _awaitingConfirm.TryRemove(message.Id, out _);
            }
        }
        catch (Exception ex)
        {
            _awaitingConfirm.TryRemove(message.Id, out _);
            _log.LogWarning(ex, "Could not deliver message {Id} to {Peer}", message.Id, message.PeerTag);
        }
    }

    /// <summary>
    /// The reliable core keys on a GUID. A chat message id is normally a 32-char hex GUID, so it maps
    /// straight across; anything else (a test id, a legacy id) is hashed to a stable GUID instead of
    /// throwing — the same string always yields the same GUID, so retries still dedupe and receipts still
    /// match, and a malformed id can never take the send path down.
    /// </summary>
    private static Guid ToGuid(string messageId) =>
        Guid.TryParseExact(messageId, "N", out var g)
            ? g
            : new Guid(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(messageId)));

    /// <summary>Prepend the one-byte app kind to a payload the messaging layer will seal.</summary>
    private static byte[] WithKind(byte kind, byte[] body)
    {
        var framed = new byte[body.Length + 1];
        framed[0] = kind;
        Buffer.BlockCopy(body, 0, framed, 1, body.Length);
        return framed;
    }

    private static byte[] WithKind(byte kind, string body) => WithKind(kind, Encoding.UTF8.GetBytes(body));

    /// <summary>Split a decrypted payload into its app kind and the body after it.</summary>
    private static (byte Kind, byte[] Body) SplitKind(byte[] plaintext)
    {
        if (plaintext.Length == 0) return (0, plaintext);
        var body = new byte[plaintext.Length - 1];
        Buffer.BlockCopy(plaintext, 1, body, 0, body.Length);
        return (plaintext[0], body);
    }

    /// <summary>Wrap an encrypted body in a marked Data packet addressed to one peer.</summary>
    /// <summary>
    /// The protocol type each of this app's messages really is.
    /// </summary>
    /// <remarks>
    /// The protocol had a slot for nearly all of these already — Ack, Heartbeat, ChannelMessage, even
    /// EridAnnounce for the routing key. The app was not using any of them. The markers stay as the
    /// payload discriminator, because several of these share one type, but the packet now says what it
    /// is on the outside where it can be acted on.
    /// </remarks>
    private static PacketType TypeFor(string marker) => marker switch
    {
        PingMarker => PacketType.Heartbeat,
        CircleMarker => PacketType.EridAnnounce,
        ProxyMarker => PacketType.CircuitRelayControl,
        // Chat text and group messages now ride Data through the reliable messaging core, so handoff —
        // the one remaining Data-typed control message — moves onto ChannelMessage (freed up by group)
        // to keep its own packet type and not collide with the messaging plane.
        Handoff.WantMarker => PacketType.ChannelMessage,
        Handoff.Marker => PacketType.ChannelMessage,
        _ => PacketType.Data,
    };

    private byte[] Wrap(string marker, AetherNet.Security.Models.EncryptedPayload sealedPayload, string peerTag)
    {
        var body = EncryptedPayloadCodec.Serialize(sealedPayload);
        var payload = new byte[marker.Length + body.Length];
        Encoding.UTF8.GetBytes(marker).CopyTo(payload, 0);
        body.CopyTo(payload, marker.Length);

        // E2 — the ERID header swap. Once we hold this contact's routing key we can address them by their
        // rotating ERID instead of their stable, trackable tag, and put our own ERID as the source. The
        // key only arrives inside their session (the CircleMarker share below), so an un-upgraded peer
        // never triggers this, and the share itself must keep the stable tag or it could never be
        // resolved. AddressFor is null until the key is known — then bootstrap traffic stays on tags.
        var source = _me.AetherTag;
        var dest = peerTag;
        if (_circle is not null && marker != CircleMarker && _circle.AddressFor(peerTag) is { } peerErid)
        {
            source = _circle.MyAddress();
            dest = peerErid;
        }

        return PacketSerializer.Serialize(new MeshPacket
        {
            // The type is what everything downstream sorts on — the send lane here, and a relay or
            // another implementation of this protocol anywhere else. Sending all of it as Data with a
            // marker buried in the ciphertext made every packet look identical from the outside, which
            // is why a phone call and a file transfer shared one queue.
            Type = TypeFor(marker),
            SourceUhid = source,
            DestinationUhid = dest,
            Ttl = 1,
            Payload = payload,
        });
    }

    /// <summary>
    /// If no receipt arrives in time, decide whether this message has actually failed.
    /// </summary>
    private async Task FailIfUnconfirmedAsync(string messageId, string peerTag)
    {
        await Task.Delay(AckTimeout).ConfigureAwait(false);
        await GiveUpIfUnconfirmedAsync(messageId, peerTag).ConfigureAwait(false);
    }

    /// <summary>
    /// Say a message failed — but only if we have genuinely stopped trying.
    ///
    /// <para>
    /// "Failed" is a promise that nothing more is happening, and it is read as one: the person retypes
    /// the message, or concludes the other side never heard. So it is wrong to show it while the phone
    /// is still working. A link that has dropped will come back and the message goes again; a session
    /// being rebuilt is about to carry it. Neither is failure, and in both cases the message stays on
    /// the list of things still owed and is re-sent by the next flush.
    /// </para>
    ///
    /// <para>
    /// The opening line of a conversation is where this showed: it goes out over a session that turns
    /// out to be broken, and the receipt timer runs out in the middle of the recovery that is about to
    /// deliver it. Every later message was confirmed both ways; only the first wore a red mark.
    /// </para>
    ///
    /// <para>
    /// A live link and a working session with still nothing back is the real thing, and is still called
    /// what it is.
    /// </para>
    /// </summary>
    public async Task GiveUpIfUnconfirmedAsync(string messageId, string peerTag)
    {
        ArgumentException.ThrowIfNullOrEmpty(messageId);
        if (!_awaitingConfirm.TryRemove(messageId, out _)) return;   // already confirmed

        if (_radio is not { IsLinked: true })
        {
            T($"no receipt for {messageId[..8]} — no link, still trying");
            return;
        }

        if (!_signal.HasSession(peerTag))
        {
            // Left owed, and deliberately not re-sent from here. Every unconfirmed message runs its own
            // timer, so flushing on each one means one flush per message, each re-sending the whole
            // backlog — forty waiting messages become sixteen hundred sends. The flush already happens
            // where it belongs: when a session comes up, and when we hear from them.
            T($"no receipt for {messageId[..8]} — session being rebuilt, still owed");
            return;
        }

        // Linked, but not to THIS recipient. The message was accepted by the delay-tolerant layer — a
        // carrier is holding it for whenever the recipient reappears, for as long as its TTL (hours).
        // Not reachable is not the same as not delivered, and a red mark here would tell the person to
        // retype something that is genuinely on its way. It stays "sent" — handed to the mesh to carry.
        if (_radio is not null && !_radio.IsReachable(peerTag))
        {
            T($"no receipt for {messageId[..8]} — recipient away, carried by store-and-forward, still owed");
            return;
        }

        _store.SetMessageStateUnlessDelivered(messageId, ChatMessage.Failed);
        T($"no receipt for {messageId[..8]} in {AckTimeout.TotalSeconds:0}s → failed");
        Changed?.Invoke();
    }

    // ── Groups ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Start a group. Everyone invited is told about it over their own private session, so the group
    /// exists on every member's phone with the same id and the same membership — there is no server
    /// holding the list, and nobody has to be online at the same moment for it to be created.
    /// </summary>
    public async Task<GroupRecord> CreateGroupAsync(string name, IEnumerable<string> members,
        CancellationToken cancellationToken = default)
    {
        var group = new GroupRecord(
            Id: "G" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant(),
            Name: string.IsNullOrWhiteSpace(name) ? "Group" : name.Trim(),
            AdminTag: _me.AetherTag,
            CreatedMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        _store.SaveGroup(group);
        _store.AddGroupMember(group.Id, _me.AetherTag);
        foreach (var m in members.Where(m => !string.IsNullOrWhiteSpace(m)).Distinct())
            _store.AddGroupMember(group.Id, m);

        Changed?.Invoke();
        await AnnounceGroupAsync(group, cancellationToken).ConfigureAwait(false);
        return group;
    }

    /// <summary>Tell every member (except us) that this group exists and who is in it.</summary>
    private async Task AnnounceGroupAsync(GroupRecord group, CancellationToken cancellationToken)
    {
        var members = _store.GetGroupMembers(group.Id);
        var payload = GroupEnvelope.News(group, members, SignContribution);

        foreach (var m in members.Where(m => m != _me.AetherTag))
        {
            await EnsureSessionAsync(m, cancellationToken).ConfigureAwait(false);
            await SendGroupToMemberAsync(m, payload, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Send to a group by sending to each member privately.
    /// <para>
    /// Every copy is sealed with that member's own ratchet, so a group is not a weaker kind of chat —
    /// it is several of the same chat. No group key exists to be stolen, and a member who leaves can
    /// simply stop being sent copies.
    /// </para>
    /// </summary>
    public async Task SendToGroupAsync(string groupId, string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(groupId) || string.IsNullOrWhiteSpace(text)) return;
        var group = _store.GetGroup(groupId);
        if (group is null) return;

        var message = new ChatMessage(
            Id: Guid.NewGuid().ToString("N"),
            PeerTag: groupId,
            // Strip the header marker out of anything typed, so a caption can never be read as one.
            Body: AttachmentRef.Clean(text).Trim(),
            Mine: true,
            State: ChatMessage.Pending,
            SentMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SenderTag: _me.AetherTag);

        _store.SaveMessage(message);
        Changed?.Invoke();

        var payload = GroupEnvelope.Message(groupId, message.Id, _me.AetherTag, message.Body, SignContribution);
        var reached = false;

        foreach (var m in _store.GetGroupMembers(groupId).Where(m => m != _me.AetherTag))
        {
            await EnsureSessionAsync(m, cancellationToken).ConfigureAwait(false);
            if (await SendGroupToMemberAsync(m, payload, cancellationToken).ConfigureAwait(false)) reached = true;
        }

        // One member reached is enough to call it sent; a group message that reached nobody stays
        // pending and goes out with the rest when someone becomes reachable.
        if (reached)
        {
            _store.SetMessageState(message.Id, ChatMessage.Sent);
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Send a note — a photo, a video, a file, a voice note — to a group.
    /// <para>
    /// A group is several 1:1 chats, and a group note is the same idea: the bytes are stored once and
    /// offered to every member (each fetches the identical content by hash), and the message envelope
    /// carries the note's header in front of the caption exactly as a one-to-one note does. There is no
    /// group content store and no shared key — a member who leaves simply stops being offered the bytes.
    /// </para>
    /// <para>
    /// Returns false only when there is nothing to send or this host cannot move attachments at all; a
    /// note whose session is not up yet is stored pending and leaves when a member becomes reachable,
    /// exactly like a typed group message.
    /// </para>
    /// </summary>
    public async Task<bool> SendNoteToGroupAsync(
        string groupId, byte[] bytes, string contentType, string name, string caption = "",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(groupId) || bytes is null || bytes.Length == 0) return false;
        if (_attachments is null)
        {
            _log.LogWarning("Cannot send a group note — this host has no attachment transport");
            return false;
        }
        if (_store.GetGroup(groupId) is null) return false;

        var members = _store.GetGroupMembers(groupId).Where(m => m != _me.AetherTag).ToList();

        // Store once, offer to every member. The hash is the same for all of them, so the message we
        // save names content this phone can actually serve to whoever asks.
        var descriptor = await _attachments
            .SendToManyAsync(members, bytes, contentType, name, cancellationToken)
            .ConfigureAwait(false);

        var message = new ChatMessage(
            Id: Guid.NewGuid().ToString("N"),
            PeerTag: groupId,
            Body: AttachmentRef.Clean(caption).Trim(),
            Mine: true,
            State: ChatMessage.Pending,
            SentMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SenderTag: _me.AetherTag,
            AttachmentHash: descriptor.RootHash,
            AttachmentType: contentType,
            AttachmentBytes: descriptor.TotalBytes);

        _store.SaveMessage(message);
        Changed?.Invoke();

        // The envelope body is the one-to-one wire form — the note header, then the caption — so the far
        // side decodes it with the same reader and the bubble fills in as the bytes land.
        var wireBody = new AttachmentRef(descriptor.RootHash, contentType, descriptor.TotalBytes)
            .Encode(message.Body);
        var payload = GroupEnvelope.Message(groupId, message.Id, _me.AetherTag, wireBody, SignContribution);

        var reached = false;
        foreach (var m in members)
        {
            await EnsureSessionAsync(m, cancellationToken).ConfigureAwait(false);
            if (await SendGroupToMemberAsync(m, payload, cancellationToken).ConfigureAwait(false)) reached = true;
        }

        if (reached)
        {
            _store.SetMessageState(message.Id, ChatMessage.Sent);
            Changed?.Invoke();
        }
        return true;
    }

    private async Task<bool> SendGroupToMemberAsync(string memberTag, string json, CancellationToken cancellationToken)
    {
        // A group is several private 1:1 chats, so a group copy is one more sealed unicast through the
        // reliable core — group-kind so the far side routes it to the group handler rather than a normal
        // conversation. Each copy is sealed with that member's own ratchet; there is no group key.
        var mesh = new MeshMessage
        {
            Id = Guid.NewGuid(),
            RecipientUhid = memberTag,
            MessageType = "group",
        };

        try
        {
            var reached = await _messaging.SendAsync(mesh, WithKind(KindGroup, json), cancellationToken).ConfigureAwait(false);
            T($"group → {memberTag} sent={reached}");
            return reached;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not send group traffic to {Peer}", memberTag);
            return false;
        }
    }

    /// <summary>
    /// A group message or group news arrived from one of its members. The reliable core has already
    /// opened it and resolved the sender to a stable tag, so this is pure domain handling of the group
    /// envelope — no crypto, no ack (the core sent the receipt).
    /// </summary>
    private void HandleGroupEnvelope(string senderTag, string json)
    {
        try
        {
            var e = GroupEnvelope.Parse(json);
            if (e is null) return;

            if (e.Kind == "new")
            {
                _store.SaveGroup(new GroupRecord(e.GroupId, e.Name ?? "Group", senderTag,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                foreach (var m in e.Members ?? Array.Empty<string>()) _store.AddGroupMember(e.GroupId, m);
                _store.AddGroupMember(e.GroupId, _me.AetherTag);
                T($"group news: {e.Name} ({e.GroupId}) from {senderTag}");
                Changed?.Invoke();
                return;
            }

            if (e.Kind != "msg" || e.MessageId is null || e.Body is null) return;

            // A group note names itself in front of the caption, exactly as a one-to-one note does;
            // plain text decodes to itself with no attachment, so nothing about typed group messages
            // changes. The bytes arrive separately and the bubble fills in as they land.
            var (attachment, caption) = AttachmentRef.Decode(e.Body);

            // Keyed by the sender's message id, so the same message arriving twice — a retry, or a
            // relay from another member — updates the one we have instead of repeating their words.
            _store.SaveMessage(new ChatMessage(
                Id: e.MessageId,
                PeerTag: e.GroupId,
                Body: caption,
                Mine: false,
                State: ChatMessage.Received,
                SentMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                SenderTag: e.Sender ?? senderTag,
                AttachmentHash: attachment?.Hash,
                AttachmentType: attachment?.ContentType,
                AttachmentBytes: attachment?.Bytes ?? 0));

            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not open group traffic from {Peer}", senderTag);
            T($"group in UNREADABLE from {senderTag}: {ex.Message}");
        }
    }

    // ── Receive path ────────────────────────────────────────────────────────────

    /// <summary>
    /// A message the reliable core opened for us. It has already decrypted the payload and resolved the
    /// sender to a stable tag; the first byte says whether it is chat text or a group envelope.
    /// </summary>
    private async Task OnMessageReceivedAsync(MeshMessage m)
    {
        var senderTag = m.SenderUhid;
        if (string.IsNullOrEmpty(senderTag)) return;

        // That opened, so this really is them — the radio can stop calling them a wire address.
        _radio?.IdentifyPeer(senderTag);

        var (kind, body) = SplitKind(m.EncryptedContent);

        if (kind == KindGroup)
        {
            HandleGroupEnvelope(senderTag, Encoding.UTF8.GetString(body));
        }
        else
        {
            // The reliable core owns the message id now, so it no longer rides inside the body — the body
            // is just what to show. A note names itself in front of the caption; plain text is only the
            // caption. The bytes of a note arrive on their own and the bubble fills in as they land.
            var (attachment, caption) = AttachmentRef.Decode(Encoding.UTF8.GetString(body));

            // Keyed by the sender's own message id (the packet id the core preserved), so a retry updates
            // the one we have instead of showing the person's words twice.
            _store.SaveMessage(new ChatMessage(
                Id: m.Id.ToString("N"),
                PeerTag: senderTag,
                Body: caption,
                Mine: false,
                State: ChatMessage.Received,
                SentMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                AttachmentHash: attachment?.Hash,
                AttachmentType: attachment?.ContentType,
                AttachmentBytes: attachment?.Bytes ?? 0));
        }

        // Seeing their message means we can reach them — send anything we were holding.
        await FlushAsync(senderTag).ConfigureAwait(false);
        Changed?.Invoke();
    }

    /// <summary>
    /// A delivery receipt came back: that message really is on the other phone. The reliable core matched
    /// it to our outbound and raised this; we just move the bubble to "delivered" and stop the give-up
    /// timer.
    /// </summary>
    private void OnDeliveryConfirmed(DeliveryReceipt receipt)
    {
        // A receipt that opened is proof of who sent it — often earlier than anything they say of their
        // own, because the person you spoke to first usually acknowledges before replying. Let the radio
        // put that name to the link. The confirmer names itself in the receipt's RecipientUhid (its own
        // stable tag), so this is a proven identity, not a header claim.
        if (!string.IsNullOrEmpty(receipt.RecipientUhid)) _radio?.IdentifyPeer(receipt.RecipientUhid);

        var id = receipt.MessageId.ToString("N");
        _awaitingConfirm.TryRemove(id, out _);
        _store.SetMessageState(id, ChatMessage.Delivered);
        T($"ack in  {id[..Math.Min(8, id.Length)]} → delivered");
        Changed?.Invoke();
    }

    // ── Handing over what is on screen ──────────────────────────────────────

    /// <summary>
    /// Where this phone is standing right now, so it can be handed over.
    /// </summary>
    /// <remarks>
    /// Set by the shell as the person moves around. It is a route rather than an object because that
    /// is all the far side needs — it has the mesh too, and can fetch whatever the route names.
    /// </remarks>
    public string? WhereIAm { get; set; }

    /// <summary>
    /// What the screen currently open is holding — including the parts a route cannot express.
    /// </summary>
    /// <remarks>
    /// A route says which conversation. It cannot say that you are half way through typing a sentence,
    /// and the half-written sentence is the thing that makes a handoff feel alive rather than
    /// administrative. So the page that is open answers for itself, and falls back to the route when
    /// nothing has claimed it.
    /// </remarks>
    /// <remarks>
    /// Asynchronous because where you are scrolled lives in the browser, not in C#, and can only be
    /// asked for. It is asked once, at the moment of a tap, so the cost is a single call in a gesture
    /// that happens rarely — not a listener chattering across the bridge all day.
    /// </remarks>
    public Func<Task<Handoff.Note?>>? Holding { get; set; }

    /// <summary>
    /// The handoff that just arrived, for the page about to open to pick up.
    /// </summary>
    /// <remarks>
    /// A route cannot carry a half-written sentence, and the page is created after the navigation, so
    /// there is a gap between the note arriving and anything existing that could use it. This is that
    /// gap, and it is taken exactly once — a draft that reappeared every time you opened a chat would
    /// be somebody else's words haunting your keyboard.
    /// </remarks>
    private Handoff.Note? _arriving;

    /// <summary>Take whatever was handed over, once.</summary>
    public Handoff.Note? TakeArriving()
    {
        var note = _arriving;
        _arriving = null;
        return note;
    }

    /// <summary>A place arrived from somebody who was just touched. The shell navigates to it.</summary>
    public event Action<string>? HandoffArrived;

    /// <summary>
    /// "I just touched your phone — what have you got?"
    /// </summary>
    /// <remarks>
    /// The reader has to speak first. A tap is one-way — one phone is a tag and the other reads it —
    /// so only the phone that did the touching knows who it touched. The gesture still reads as
    /// giving; the asking simply runs the other way underneath.
    /// </remarks>
    public async Task AskForHandoffAsync(string peerTag, CancellationToken cancellationToken = default)
    {
        if (_radio is null || string.IsNullOrEmpty(peerTag) || !_signal.HasSession(peerTag)) return;

        try
        {
            var sealedPayload = await _signal
                .EncryptAsync(peerTag, Encoding.UTF8.GetBytes("?"), cancellationToken)
                .ConfigureAwait(false);

            await _radio.SendPacketAsync(Wrap(Handoff.WantMarker, sealedPayload, peerTag)).ConfigureAwait(false);
            T($"touched {peerTag} — asked what they are holding");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not ask {Peer} for a handoff", peerTag);
        }
    }

    /// <summary>They touched us. Send back where we are standing, if it is anywhere worth going.</summary>
    private async Task ReceiveHandoffWantAsync(string? senderTag, byte[] payload)
    {
        if (string.IsNullOrEmpty(senderTag) || _radio is null) return;

        try
        {
            // Opened before it is acted on. Anyone can put bytes on a radio; only somebody holding the
            // session can produce something that decrypts, and that is what makes this a friend.
            var asked = EncryptedPayloadCodec.Deserialize(payload.AsSpan(Handoff.WantMarker.Length).ToArray());
            await _signal.DecryptAsync(senderTag, asked).ConfigureAwait(false);
            _radio.IdentifyPeer(senderTag);

            T($"◀ {senderTag} touched us and is asking what we hold");

            var holding = Holding is { } ask
                ? await ask().ConfigureAwait(false)
                : Handoff.Describe(WhereIAm);

            if (holding is not { } note)
            {
                T($"{senderTag} touched us, but this screen is not a place worth handing over");
                return;
            }

            var sealedPayload = await _signal
                .EncryptAsync(senderTag, Handoff.Encode(note)).ConfigureAwait(false);

            await _radio.SendPacketAsync(Wrap(Handoff.Marker, sealedPayload, senderTag)).ConfigureAwait(false);
            T($"▶ handed {senderTag} the {note.Kind} {note.Target}" +
              (note.Draft is null ? "" : $" with {note.Draft.Length} unsent characters") +
              (note.At is null ? "" : $" at {note.At:P0}"));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not answer a handoff from {Peer}", senderTag);
            if (LooksLikeABrokenSession(ex)) await RepairSessionAsync(senderTag).ConfigureAwait(false);
        }
    }

    /// <summary>A place came back from the phone we touched. Go there.</summary>
    private async Task ReceiveHandoffAsync(string? senderTag, byte[] payload)
    {
        if (string.IsNullOrEmpty(senderTag)) return;

        try
        {
            var sealedPayload = EncryptedPayloadCodec.Deserialize(payload.AsSpan(Handoff.Marker.Length).ToArray());
            var body = await _signal.DecryptAsync(senderTag, sealedPayload).ConfigureAwait(false);
            _radio?.IdentifyPeer(senderTag);

            var arrived = Handoff.Decode(body);
            if (Handoff.RouteFor(arrived) is not { } route)
            {
                // A kind or a version this build does not know. Doing nothing is the right answer —
                // landing somebody on the wrong screen is worse than landing them nowhere.
                T($"{senderTag} handed over something this build does not understand");
                return;
            }

            // Put it down before the navigation, because the page that wants it does not exist yet.
            _arriving = arrived;

            T($"◀ {senderTag} handed us {route}" +
              (arrived?.Draft is null ? "" : $" with {arrived.Draft.Length} unsent characters") +
              (arrived?.At is null ? "" : $" at {arrived.At:P0}"));
            HandoffArrived?.Invoke(route);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not open a handoff from {Peer}", senderTag);
            if (LooksLikeABrokenSession(ex)) await RepairSessionAsync(senderTag).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Send one message under the current session purely so the other side adopts it.
    /// <para>
    /// Never shown and never stored — the value is entirely in the far side having decrypted it.
    /// </para>
    /// </summary>
    private async Task PingAsync(string peerTag, CancellationToken cancellationToken = default)
    {
        if (_radio is null || !_signal.HasSession(peerTag)) return;

        try
        {
            var sealedPayload = await _signal
                .EncryptAsync(peerTag, Encoding.UTF8.GetBytes("hello"), cancellationToken)
                .ConfigureAwait(false);

            await _radio.SendPacketAsync(Wrap(PingMarker, sealedPayload, peerTag)).ConfigureAwait(false);
            T($"pinged {peerTag} so they pick up the new session");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not ping {Peer} after rebuilding the session", peerTag);
        }
    }

    /// <summary>
    /// Let one contact recognise this phone behind its rotating address, by handing them the key the
    /// address is derived from.
    ///
    /// <para>
    /// Sent once per contact and then never again unless they re-key, because the key does not rotate
    /// — only the addresses derived from it do. Sending it on every settle-up would put a long-term
    /// secret on the radio dozens of times a day for no gain.
    /// </para>
    /// </summary>
    private async Task ShareRoutingKeyAsync(string peerTag, CancellationToken cancellationToken = default)
    {
        if (_radio is null || _circle is null || !_signal.HasSession(peerTag)) return;
        if (!_sharedCircleKey.TryAdd(peerTag, 0)) return;

        try
        {
            var sealedPayload = await _signal
                .EncryptAsync(peerTag, _me.RoutingKey, cancellationToken)
                .ConfigureAwait(false);

            if (await _radio.SendPacketAsync(Wrap(CircleMarker, sealedPayload, peerTag)).ConfigureAwait(false))
                T($"{peerTag} can recognise this phone now — they hold its routing key");
            else
                _sharedCircleKey.TryRemove(peerTag, out _);   // nothing went out; try again next flush
        }
        catch (Exception ex)
        {
            _sharedCircleKey.TryRemove(peerTag, out _);
            _log.LogWarning(ex, "Could not share the routing key with {Peer}", peerTag);
        }
    }

    /// <summary>
    /// A contact handed us their routing key. From here their beacon has a name on it, so the radio
    /// can tell them apart from every stranger broadcasting nearby.
    /// </summary>
    private async Task ReceiveCircleAsync(string? senderTag, byte[] payload)
    {
        if (string.IsNullOrEmpty(senderTag) || _circle is null) return;

        try
        {
            var sealedPayload = EncryptedPayloadCodec.Deserialize(payload.AsSpan(CircleMarker.Length).ToArray());
            var key = await _signal.DecryptAsync(senderTag, sealedPayload).ConfigureAwait(false);
            if (key.Length == 0) return;

            _circle.Learn(senderTag, key);
            _radio?.IdentifyPeer(senderTag);
            T($"{senderTag} can be recognised behind a rotating address now");

            // Recognition has to be mutual to be useful: they can find us, and we still cannot find
            // them. Answering costs one packet and closes the pair.
            await ShareRoutingKeyAsync(senderTag).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not open a routing key from {Peer}", senderTag);
            if (LooksLikeABrokenSession(ex)) await RepairSessionAsync(senderTag).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Something finished arriving. If it is the app itself, offer to install it.
    /// </summary>
    /// <remarks>
    /// Only offers — the system installer is what asks, and the person is what decides. Nothing
    /// installs because it finished downloading.
    /// </remarks>
    private void OnAttachmentArrived(string hash)
    {
        if (_appShare is not { IsSupported: true } || _attachments is null) return;
        if (!_store.GetMessagesWithAttachment(hash).Any(m => m.AttachmentType == AppPackageType)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var bytes = await _attachments.GetAsync(hash).ConfigureAwait(false);
                if (bytes is null || bytes.Length == 0) return;

                T($"the app arrived ({bytes.Length / (1024 * 1024)} MB) — offering to install it");
                await _appShare.OfferToInstallAsync(bytes).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not offer the shared app for install");
            }
        });
    }

    /// <summary>Whether this phone can hand the app to another one.</summary>
    public bool CanShareApp => _appShare is { IsSupported: true };

    /// <summary>Why not, in words someone holding the phone can act on.</summary>
    public string? CannotShareAppReason => _appShare?.UnavailableReason ?? "not supported on this phone";

    /// <summary>How big the share is, so a person is told before it starts rather than after.</summary>
    public long AppShareSizeBytes => _appShare?.SizeBytes ?? 0;

    /// <summary>
    /// Everyone this phone can hand the app to: contacts who have added it back.
    ///
    /// <para>
    /// Mutual only, deliberately. A forty-megabyte package is not something to accept from somebody
    /// who is not already in your Circle, and it is not something to spend a data bundle pushing at
    /// somebody who has not agreed to know you.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> MutualContacts =>
        _store.GetContacts().Where(c => c.IsMutual).Select(c => c.Tag).ToArray();

    /// <summary>
    /// Turn relaying for the Circle on or off, and tell everyone either way.
    /// </summary>
    /// <remarks>
    /// Switching off has to be announced. Going quiet instead leaves every contact pointing at a
    /// relay that no longer answers, which from their side is indistinguishable from the network
    /// being down — the worst possible way to withdraw a favour.
    /// </remarks>
    public async Task<bool> SetRelayingAsync(bool on, CancellationToken cancellationToken = default)
    {
        if (_proxies is null) return false;
        if (_gateway is null)
        {
            // No gateway on this head — the web build has no server to run. Say so rather than
            // flipping a switch that does nothing.
            T("this build cannot relay — there is no server side on this host");
            return false;
        }

        return on
            ? await _gateway.StartRelayingAsync(cancellationToken).ConfigureAwait(false)
            : await _gateway.StopRelayingAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The content type an Android package arrives under. Recognised on the way in, so a
    /// shared app offers to install itself rather than sitting in a chat as an unopenable blob.</summary>
    public const string AppPackageType = "application/vnd.android.package-archive";

    /// <summary>
    /// Give somebody the app itself.
    ///
    /// <para>
    /// It goes as an ordinary attachment, which is the whole trick: attachments are already
    /// content-addressed, chunked and resumable, so a forty-megabyte package survives a link dropping
    /// halfway and picks up where it stopped. A bespoke transfer path for this would be a second
    /// implementation of something already working, and a worse one.
    /// </para>
    ///
    /// <para>
    /// The receiving phone ends at Android's own installer prompt. That is not an obstacle to route
    /// around — it is the moment a person decides to trust what the phone beside them just handed
    /// over, and an app that arranged to skip it would be malware with good manners.
    /// </para>
    /// </summary>
    public async Task<bool> ShareAppAsync(string peerTag, CancellationToken cancellationToken = default)
    {
        if (_appShare is not { IsSupported: true })
        {
            T($"cannot share the app: {_appShare?.UnavailableReason ?? "not supported on this phone"}");
            return false;
        }

        var installer = await _appShare.ReadInstallerAsync(cancellationToken).ConfigureAwait(false);
        if (installer is null || installer.Length == 0) return false;

        T($"sharing the app with {peerTag} — {installer.Length / (1024 * 1024)} MB, no store involved");
        return await SendNoteAsync(peerTag, installer, AppPackageType, "Aether.apk",
            "Aether — install this to join", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Tell one contact where to reach this phone while it is acting as the Circle's relay — or that
    /// it has stopped.
    /// </summary>
    public async Task OfferProxyAsync(string peerTag, string? url, CancellationToken cancellationToken = default)
    {
        if (_radio is null || !_signal.HasSession(peerTag)) return;

        try
        {
            // An empty address is how a phone withdraws. Saying nothing would leave every contact
            // pointing at a relay that has stopped answering, which looks exactly like a dead network.
            var sealedPayload = await _signal
                .EncryptAsync(peerTag, Encoding.UTF8.GetBytes(url ?? string.Empty), cancellationToken)
                .ConfigureAwait(false);

            if (await _radio.SendPacketAsync(Wrap(ProxyMarker, sealedPayload, peerTag)).ConfigureAwait(false))
                T(url is null ? $"told {peerTag} this phone has stopped relaying"
                              : $"offered {peerTag} a relay at {url}");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not offer a relay to {Peer}", peerTag);
        }
    }

    /// <summary>Tell everyone we hold a session with. Used when the gateway is switched on or off.</summary>
    public async Task OfferProxyToCircleAsync(string? url, CancellationToken cancellationToken = default)
    {
        foreach (var contact in _store.GetContacts())
        {
            if (!contact.IsMutual) continue;
            await OfferProxyAsync(contact.Tag, url, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A contact is offering to carry this phone's traffic — or has stopped.</summary>
    private async Task ReceiveProxyAsync(string? senderTag, byte[] payload)
    {
        if (string.IsNullOrEmpty(senderTag) || _proxies is null) return;

        try
        {
            var sealedPayload = EncryptedPayloadCodec.Deserialize(payload.AsSpan(ProxyMarker.Length).ToArray());
            var url = Encoding.UTF8.GetString(await _signal.DecryptAsync(senderTag, sealedPayload).ConfigureAwait(false));

            if (string.IsNullOrWhiteSpace(url))
            {
                _proxies.Withdraw(senderTag);
                T($"{senderTag} has stopped relaying");
                return;
            }

            _proxies.Offer(senderTag, url);
            T($"{senderTag} is relaying for the Circle at {url}");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not open a relay offer from {Peer}", senderTag);
            if (LooksLikeABrokenSession(ex)) await RepairSessionAsync(senderTag).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A ping arrived. Opening it is the entire point — that is what adopts their session on this
    /// side — so there is deliberately nothing else to do with it.
    /// </summary>
    private async Task ReceivePingAsync(string? senderTag, byte[] payload)
    {
        if (string.IsNullOrEmpty(senderTag)) return;

        try
        {
            var sealedPayload = EncryptedPayloadCodec.Deserialize(payload.AsSpan(PingMarker.Length).ToArray());
            await _signal.DecryptAsync(senderTag, sealedPayload).ConfigureAwait(false);
            _radio?.IdentifyPeer(senderTag);
            T($"session ping from {senderTag} — their session is live here now");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not open a session ping from {Peer}", senderTag);
            if (LooksLikeABrokenSession(ex)) await RepairSessionAsync(senderTag).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Answer a pre-key request. Our own bundle has to exist first — a node that has never been asked
    /// before still has to be able to reply, or the very first conversation could never start.
    /// </summary>
    private async Task HandlePreKeyAsync(MeshPacket packet)
    {
        await EnsureLocalBundleAsync(CancellationToken.None).ConfigureAwait(false);
        await _preKeys.HandleAsync(packet).ConfigureAwait(false);
    }

    /// <summary>
    /// A payload from this peer would not open. Treat the session as finished and get a new one built.
    ///
    /// <para>
    /// The old session has to go first: while it is still there, a fresh pre-key bundle is ignored as
    /// "we already have a session with them", so the broken session prevents the only thing that would
    /// fix it. Then this phone asks for a new bundle itself rather than deferring to the peer — a
    /// diverged ratchet usually breaks in one direction only, and the side still sending happily has no
    /// idea anything is wrong. Waiting for it is waiting forever.
    /// </para>
    /// </summary>
    /// <summary>
    /// Throw away a session that cannot read, and get a fresh one built.
    ///
    /// <para>
    /// Public because voice needs exactly this and must not have its own copy. A call hits the same
    /// wall a message does — <c>AuthenticationTagMismatch</c>, meaning the two sides hold sessions that
    /// do not agree — and for a long time the call path simply gave up where chat quietly recovered.
    /// That is why sending a message first appeared to "fix" calling: chat's repair had already
    /// collapsed the two divergent sessions into one before the call was placed.
    /// </para>
    /// </summary>
    public Task RepairAsync(string peerTag) => RepairSessionAsync(peerTag);

    /// <summary>Is this the failure that means the session is finished rather than the payload bad?</summary>
    public static bool IsBrokenSession(Exception ex) => LooksLikeABrokenSession(ex);

    private async Task RepairSessionAsync(string peerTag)
    {
        if (string.IsNullOrEmpty(peerTag)) return;
        if (!_repair.ShouldRestart(peerTag, DateTime.UtcNow)) return;

        _signal.DropSession(peerTag);
        T($"no usable session with {peerTag} → dropping it and asking for a fresh bundle");

        // Publish a new bundle before asking for theirs. A bundle carries a ONE-TIME pre-key: the peer
        // consumed ours establishing the session that just died, and a second message naming the same
        // id is refused outright. Offering the spent one again means the repair completes on this side
        // and is thrown away on theirs — two phones did exactly that every forty seconds, indefinitely.
        await RefreshLocalBundleAsync().ConfigureAwait(false);

        try
        {
            await _preKeys.RequestBundleAsync(peerTag).ConfigureAwait(false);
            T($"asked {peerTag} for a fresh bundle");
        }
        catch (Exception ex)
        {
            // The link will come back and the next failure starts this again — nothing is lost.
            _log.LogWarning(ex, "Could not ask {Peer} for a fresh pre-key bundle", peerTag);
        }
    }

    /// <summary>
    /// Is this the failure of a session that no longer works, as opposed to a payload that was never
    /// meant for us? A mismatched authentication tag is the ratchet saying it has diverged.
    /// </summary>
    /// <summary>
    /// Is this the kind of failure a fresh session would fix?
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two failures, one remedy. A session that has gone bad throws
    /// <see cref="System.Security.Cryptography.CryptographicException"/> — an authentication tag that
    /// does not match, because the two ratchets have diverged. Having no session at all throws
    /// <see cref="InvalidOperationException"/> from the Signal service instead.
    /// </para>
    /// <para>
    /// Only the first was matched here, and the second is the one that matters most: it is what a
    /// phone sees the very first time somebody messages it after a reinstall. The message crossed the
    /// radio perfectly, decryption failed for the most ordinary reason there is, and it was dropped in
    /// silence with no attempt to build the session that would have opened it — forever, because
    /// every later message failed the same way. Measured on the P30: "No session established with
    /// peer ZXFA…d90b", once per message, while the sender's messages timed out one after another.
    /// </para>
    /// </remarks>
    private static bool LooksLikeABrokenSession(Exception ex) =>
        ex is System.Security.Cryptography.CryptographicException   // AuthenticationTagMismatch is one of these
        || (ex is InvalidOperationException && ex.Message.Contains("session", StringComparison.OrdinalIgnoreCase));

    private void OnBundleReceived(object? sender, PreKeyBundleReceivedEventArgs e)
    {
        if (e.Bundle is null) return;
        var peer = !string.IsNullOrEmpty(e.FromUhid) ? e.FromUhid : e.Bundle.Uhid;
        _ = AdoptBundleAsync(peer, e.Bundle, CancellationToken.None);
    }

    private async Task AdoptBundleAsync(string peerTag, AetherNet.Security.Models.PreKeyBundle bundle, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(peerTag) || _signal.HasSession(peerTag)) return;

        try
        {
            await _signal.ProcessPreKeyBundleAsync(bundle, cancellationToken).ConfigureAwait(false);

            // Tell them the session exists, before anything else. Flushing a backlog would do it, but
            // only if there is one — and the case that matters most is a call, which has nothing to
            // flush.
            await PingAsync(peerTag, cancellationToken).ConfigureAwait(false);
            await FlushAsync(peerTag, cancellationToken).ConfigureAwait(false);
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not start a secure session with {Peer}", peerTag);
        }
    }
}

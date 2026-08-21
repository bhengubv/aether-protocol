// SPDX-License-Identifier: MIT

using System.Text;
using AetherNet.Messaging;
using AetherNet.Protocol;
using AetherNet.Security.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// Brings a Wi-Fi Direct group up by <b>arranging it over Bluetooth first</b>.
///
/// <para>
/// Two phones calling <c>connect()</c> at each other is a race, and losing it is not quiet: Android
/// falls back to an <b>"Invitation to connect"</b> dialog on the other handset, which nobody is
/// looking at, and which takes window focus so the app appears wedged as well. Watched repeatedly on
/// merlin — a group that formed in seconds when the timing happened to work, and not at all in eight
/// minutes when it did not.
/// </para>
///
/// <para>
/// So nothing is negotiated. One phone creates the group outright, and tells the other its name and
/// passphrase over the BLE link that is already up and already carrying chat. The other joins by name.
/// There is no discovery, no invitation and no dialog, and the two radios never have to agree about
/// timing.
/// </para>
///
/// <para>
/// This is also the shape the app wanted anyway: <b>BLE is the control channel and Wi-Fi Direct is the
/// bulk pipe</b> — brought up on demand for the things BLE's measured 5 kbps cannot carry, which is
/// voice, pictures and video.
/// </para>
/// </summary>
public sealed class WifiDirectBroker : IDisposable
{
    /// <summary>Marks a credentials handoff inside an ordinary data packet, as chat and the mesh-web do.</summary>
    private const string Marker = "WFD1";

    private readonly IIdentityService _me;
    private readonly IRadioMesh? _radio;
    private readonly ISignalProtocolService _signal;
    private readonly IWifiDirectGroup _group;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public WifiDirectBroker(
        IIdentityService me,
        ISignalProtocolService signal,
        IWifiDirectGroup group,
        IRadioMesh? radio = null,
        ILoggerFactory? loggerFactory = null)
    {
        _me = me ?? throw new ArgumentNullException(nameof(me));
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
        _group = group ?? throw new ArgumentNullException(nameof(group));
        _radio = radio;
        _log = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<WifiDirectBroker>();

        if (_radio is not null) _radio.PacketReceived += OnPacket;
    }

    /// <summary>Running commentary, for the radio log.</summary>
    public event Action<string>? Trace;

    /// <summary>Raised when the group is up on this phone, either as owner or as joiner.</summary>
    public event Action? Changed;

    /// <summary>True once this phone is in a brokered group.</summary>
    public bool IsUp { get; private set; }

    /// <summary>
    /// The group this phone is hosting, kept so the key can be offered again.
    ///
    /// <para>
    /// Null when joining rather than hosting — a joiner has nothing to hand out. Held because the
    /// first offer goes out seconds after launch, often before any radio can carry it, and the only
    /// way to recover is to still have the credentials when a link finally arrives.
    /// </para>
    /// </summary>
    private WifiDirectCredentials? _hosting;

    /// <summary>
    /// Whether this phone could bring a wide link up at all — not whether one is up now.
    ///
    /// <para>
    /// A call placed over Bluetooth alone cannot work (PROTOCOL_SPEC §5.5), but a call placed while
    /// Bluetooth is the only link so far usually does, because bringing this group up is the first
    /// thing the call does. The two are told apart here: no Wi-Fi Direct hardware means no wide link
    /// is coming, and only then is refusing the call the honest answer.
    /// </para>
    /// </summary>
    public bool IsSupported => !_disposed && _group.IsSupported;

    /// <summary>
    /// Which of two phones creates the group, when both want it at the same moment.
    ///
    /// <para>
    /// Only needed to break a genuine tie. In practice one side asks for the pipe first — it is the
    /// one placing a call — and <b>that side hosts</b>, because it is the side that knows it needs it.
    /// An earlier version had both phones consult this rule instead, which deadlocked the moment the
    /// caller turned out to be the non-hosting one: it sat waiting for a key from a phone that had no
    /// reason to create a group at all.
    /// </para>
    ///
    /// <para>
    /// The rule itself is deliberately one each side can evaluate alone, with no round trip and no way
    /// to disagree. Android's device address is privacy-masked, so the tag is what there is.
    /// </para>
    /// </summary>
    public static bool HostsTheGroup(string myTag, string peerTag) =>
        string.CompareOrdinal(myTag, peerTag) < 0;

    /// <summary>
    /// Get a Wi-Fi Direct group up with this peer, whichever side of it we are on.
    ///
    /// <para>
    /// The host creates the group and sends the credentials; the joiner does nothing but wait, because
    /// the credentials are the only thing it could act on and they are on their way.
    /// </para>
    /// </summary>
    public async Task<bool> BringUpAsync(string peerTag, CancellationToken cancellationToken = default)
    {
        if (_disposed || !_group.IsSupported || string.IsNullOrEmpty(peerTag)) return false;

        // The credentials are a secret — anybody holding them can join — so they only travel inside an
        // established session. No session, no handoff.
        if (!_signal.HasSession(peerTag))
        {
            T($"cannot set up Wi-Fi Direct with {peerTag} — no secure session to send the key over");
            return false;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A group that is up is not the same as a group anybody is in.
            //
            // This used to return here, and that quietly made the key a one-shot: the group forms
            // within a second of launch — before BLE has linked — so the single offer went out over a
            // radio that could not carry it and was gone. Every later attempt then short-circuited on
            // a group nobody had ever joined, and the phones sat either side of an empty network
            // moving their traffic over eleven kilobits. Watched on merlin 12:30:01: the P30 reported
            // "sent the key" and merlin never logged it arriving at all.
            //
            // So while we are hosting, offer it again. Idempotent by construction — a peer that has
            // already joined ignores a second key, and the offer is one small packet. Every link-up
            // asks, so the first link that actually works delivers it.
            if (IsUp)
            {
                if (_hosting is null) return true;

                var again = await OfferTheKeyAsync(peerTag, _hosting, cancellationToken).ConfigureAwait(false);
                T(again
                    ? $"already hosting {_hosting.NetworkName} — offered the key to {peerTag} again"
                    : $"already hosting {_hosting.NetworkName}, but {peerTag} still cannot be reached");
                return again;
            }

            // Exactly one of us hosts, and the rule decides which — see HostsTheGroup.
            //
            // "Whoever asks hosts it" was right while only ONE side ever asked: the caller placing a
            // call. Wi-Fi Direct is now the core radio and both phones ask the moment they have a
            // session, so both hosted — merlin on DIRECT-2W-Redmi Note 9, the P30 on
            // DIRECT-Ap-NIATango0056, each dutifully sending the other its key, neither joining
            // anything. Two groups is the same as no group.
            //
            // The old deadlock this comment used to warn about cannot come back. That was one side
            // deferring to a host with no reason to create anything; now both sides ask, so whichever
            // one the rule names will create it. The other waits, and the credentials arrive over the
            // link that already works.
            if (!HostsTheGroup(_me.AetherTag, peerTag))
            {
                T($"{peerTag} hosts this one — waiting for their key");
                return false;
            }

            var credentials = await _group.HostAsync(cancellationToken).ConfigureAwait(false);
            if (credentials is null) { T("could not create a group to host"); return false; }

            IsUp = true;
            _hosting = credentials;
            Raise();

            // Keep offering the key. A group nobody can join is worse than no group at all: it is up,
            // it is empty, and every byte keeps crawling over BLE past a radio that would move it in a
            // fraction of the time. Watched on merlin — "hosting DIRECT-2W-Redmi Note 9, but could not
            // send the key" — after which a voice note carried on at eleven kilobits while the fast
            // radio sat idle beside it.
            //
            // One attempt was never enough, for exactly the reason one attachment request was not: the
            // radio may be mid-reconnect, or the session may be a few hundred milliseconds from
            // existing. Wi-Fi Direct is the radio this whole mesh is built on, so it is worth waiting
            // a few seconds to get somebody onto it.
            var sent = await OfferTheKeyAsync(peerTag, credentials, cancellationToken).ConfigureAwait(false);

            if (sent)
            {
                T($"hosting {credentials.NetworkName}; sent the key to {peerTag}");
                return true;
            }

            // Nobody is coming. Give the group back rather than leaving it up and empty — holding one
            // costs power and can disturb the phone's own Wi-Fi.
            T($"hosting {credentials.NetworkName}, but {peerTag} never got the key — taking it down");
            IsUp = false;
            _hosting = null;
            await _group.LeaveAsync().ConfigureAwait(false);
            Raise();
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Tear the group down — the other side will notice its socket go.</summary>
    public async Task TakeDownAsync()
    {
        if (!IsUp) return;
        IsUp = false;
        _hosting = null;
        await _group.LeaveAsync().ConfigureAwait(false);
        T("left the group");
        Raise();
    }

    // ── The handoff ───────────────────────────────────────────────────────────

    /// <summary>
    /// How many times to offer the key before concluding the peer is not coming.
    ///
    /// <para>
    /// Six tries over about twelve seconds. Long enough to ride out a radio mid-reconnect or a session
    /// that is moments from existing; short enough that a phone which has genuinely gone does not hold
    /// a group open on the strength of it.
    /// </para>
    /// </summary>
    private const int KeyOffers = 6;

    private async Task<bool> OfferTheKeyAsync(
        string peerTag, WifiDirectCredentials credentials, CancellationToken cancellationToken)
    {
        var wait = TimeSpan.FromMilliseconds(400);

        for (var attempt = 1; attempt <= KeyOffers; attempt++)
        {
            if (_disposed || cancellationToken.IsCancellationRequested) return false;
            if (await SendAsync(peerTag, credentials, cancellationToken).ConfigureAwait(false)) return true;

            if (attempt == KeyOffers) break;

            T($"key to {peerTag} did not go (try {attempt}/{KeyOffers}) — retrying");
            try { await Task.Delay(wait, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
            wait += wait;
        }

        return false;
    }

    private async Task<bool> SendAsync(string peerTag, WifiDirectCredentials credentials, CancellationToken cancellationToken)
    {
        if (_radio is null) return false;

        try
        {
            var body = Encoding.UTF8.GetBytes(credentials.ToJson());
            var sealedBody = await _signal.EncryptAsync(peerTag, body, cancellationToken).ConfigureAwait(false);
            var serialized = EncryptedPayloadCodec.Serialize(sealedBody);

            var payload = new byte[Marker.Length + serialized.Length];
            Encoding.UTF8.GetBytes(Marker).CopyTo(payload, 0);
            serialized.CopyTo(payload, Marker.Length);

            return await _radio.SendPacketAsync(PacketSerializer.Serialize(new MeshPacket
            {
                Type = PacketType.Data,
                SourceUhid = _me.AetherTag,
                DestinationUhid = peerTag,
                Ttl = 1,
                Payload = payload,
            })).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not send Wi-Fi Direct credentials to {Peer}", peerTag);
            return false;
        }
    }

    private void OnPacket(byte[] bytes)
    {
        MeshPacket packet;
        try { packet = PacketSerializer.Deserialize(bytes); }
        catch { return; }

        if (packet.Type != PacketType.Data) return;
        var payload = packet.Payload;
        if (payload is null || payload.Length <= Marker.Length) return;
        if (Encoding.UTF8.GetString(payload, 0, Marker.Length) != Marker) return;

        // Said the moment it lands, before anything can go wrong with it. Without this, a key that
        // arrived and could not be used looked exactly like a key that never arrived — merlin sat on
        // "waiting for their key" in silence while the P30's log showed it sent on the first try, and
        // there was no way to tell which end to look at.
        T($"a group key arrived from {packet.SourceUhid}");

        _ = ReceiveAsync(packet.SourceUhid, payload);
    }

    /// <summary>
    /// Someone has offered us a group to join.
    ///
    /// <para>
    /// Only acted on if it opens under their ratchet. That matters more here than almost anywhere
    /// else: these credentials tell this phone which network to attach itself to, and a forged handoff
    /// would be a way to steer it onto somebody else's.
    /// </para>
    /// </summary>
    private async Task ReceiveAsync(string? from, byte[] payload)
    {
        // No HasSession check, for the same reason the call path has none: the responder's session is
        // created by decrypting the first message, so refusing to decrypt until a session exists drops
        // the very message that would have made one.
        if (string.IsNullOrEmpty(from) || !_group.IsSupported) return;

        WifiDirectCredentials? credentials;
        try
        {
            var sealedBody = EncryptedPayloadCodec.Deserialize(payload.AsSpan(Marker.Length).ToArray());
            var json = Encoding.UTF8.GetString(await _signal.DecryptAsync(from, sealedBody).ConfigureAwait(false));
            credentials = WifiDirectCredentials.Parse(json);
        }
        catch (Exception ex)
        {
            // Say it out loud. This went only to ILogger, which on the phone goes nowhere anybody
            // looks — so a handoff that arrived and could not be opened was indistinguishable from one
            // that never arrived, and merlin sat on "waiting for their key" in silence while the P30
            // had already sent it. That is the third service tonight to hide a failure this way.
            T($"the group key from {from} would NOT open — {ex.GetType().Name}: {ex.Message}");
            _log.LogWarning(ex, "Could not open a Wi-Fi Direct handoff from {Peer}", from);
            return;
        }

        if (credentials is null) { T($"{from} sent a group key that makes no sense"); return; }

        // It opened, so this really is them.
        _radio?.IdentifyPeer(from);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsUp) return;
            T($"{from} is hosting {credentials.NetworkName} — joining");
            if (await _group.JoinAsync(credentials).ConfigureAwait(false))
            {
                IsUp = true;
                T($"joined {credentials.NetworkName}");
                Raise();
            }
            else
            {
                T($"could not join {credentials.NetworkName}");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void T(string message)
    {
        Trace?.Invoke(message);
        _log.LogInformation("{Message}", message);
    }

    private void Raise() => Changed?.Invoke();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_radio is not null) _radio.PacketReceived -= OnPacket;
        _gate.Dispose();
    }
}

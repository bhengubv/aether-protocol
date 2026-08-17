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
            if (IsUp) return true;

            // Whoever asks for the pipe hosts it. The caller is the side that knows it needs one, so
            // making it wait on a rule about tags is how this deadlocked before — both phones deferring
            // to a host that had no reason to create anything.
            var credentials = await _group.HostAsync(cancellationToken).ConfigureAwait(false);
            if (credentials is null) { T("could not create a group to host"); return false; }

            IsUp = true;
            Raise();

            var sent = await SendAsync(peerTag, credentials, cancellationToken).ConfigureAwait(false);
            T(sent
                ? $"hosting {credentials.NetworkName}; sent the key to {peerTag}"
                : $"hosting {credentials.NetworkName}, but could not send the key to {peerTag}");
            return sent;
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
        await _group.LeaveAsync().ConfigureAwait(false);
        T("left the group");
        Raise();
    }

    // ── The handoff ───────────────────────────────────────────────────────────

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

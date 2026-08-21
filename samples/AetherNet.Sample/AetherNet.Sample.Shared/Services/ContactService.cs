// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AetherNet.Identity;
using AetherNet.Protocol;
using AetherNet.Sample.Shared.Data;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// The people this device knows, and the BBM-style handshake that puts them there.
///
/// There is no directory and no search: <b>each device adds the other's AetherTag</b>. You get a tag
/// the way you'd get a phone number — scanned, typed, or handed over the radio by the phone in front
/// of you — and the pair only becomes real once both sides have added each other. Nothing about this
/// touches a server, because there isn't one.
/// </summary>
public sealed class ContactService
{
    /// <summary>Marks our add-request payload inside a generic Data packet.</summary>
    private const string Marker = "AETHERADD";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly AetherStore _store;
    private readonly IIdentityService _me;
    private readonly IRadioMesh? _radio;
    private readonly CircleDirectory? _circle;

    public ContactService(AetherStore store, IIdentityService me, IRadioMesh? radio = null,
        CircleDirectory? circle = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _me = me ?? throw new ArgumentNullException(nameof(me));
        _radio = radio;
        _circle = circle;

        if (_radio is not null)
        {
            _radio.PacketReceived += OnPacket;
            _radio.Changed += OnRadioChanged;
        }
    }

    /// <summary>Whether the radio had a link last time it told us, so we can spot a real transition.</summary>
    private int _wasLinked;

    /// <summary>
    /// A link appearing is the moment to tell anyone still waiting on us.
    ///
    /// <para>
    /// Adding someone is almost always done before any link exists — it happens during first-run setup,
    /// because a link is the very thing the person is trying to establish. That announcement cannot go
    /// anywhere, and without this it was never sent again: both phones sit on "waiting for them to add
    /// you back" forever with a working radio between them. A message survives this because it goes on
    /// a backlog and is flushed; the add needs the same.
    /// </para>
    /// </summary>
    private void OnRadioChanged()
    {
        var linked = _radio is { IsLinked: true };

        // Changed fires for every line the radio logs, and announcing sends packets, which log — so
        // acting on the event itself feeds itself. Only a transition into a link counts.
        if (Interlocked.Exchange(ref _wasLinked, linked ? 1 : 0) == 1 || !linked) return;

        _ = AnnounceOutstandingAsync();
    }

    /// <summary>How long to keep trying after a link appears, and how often.</summary>
    private static readonly TimeSpan AnnounceRetryEvery = TimeSpan.FromSeconds(2);
    private const int AnnounceAttempts = 5;

    /// <summary>
    /// Who we have successfully got an announcement out to.
    ///
    /// <para>
    /// Deliberately <b>not</b> the same question as "are we mutual". Them adding us says nothing about
    /// whether our announcement ever reached them — they may have added us from a QR code, or their
    /// packet may have crossed ours. Treating mutual as "told them" is what stranded a P30: its
    /// announcement lost a 4 ms race with the link flag, then merlin's add arrived and made the contact
    /// mutual, so the retry decided there was nothing left to say. merlin never heard from it.
    /// </para>
    ///
    /// <para>
    /// In memory rather than on disk: a restart costs one extra announcement on the next link, which is
    /// cheap and self-correcting, while a stale "already told them" on disk would be permanent.
    /// </para>
    /// </summary>
    private readonly HashSet<string> _announced = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Re-announce to everyone who has not added us back yet.
    ///
    /// <para>
    /// Tried several times rather than once, because the link event and the radio's own "I am linked"
    /// flag do not flip at the same instant — on a P30 Lite the announcement went out 3 ms before the
    /// flag turned true, the send was refused, and with nothing scheduled after it that phone never
    /// told its peer again. A few cheap retries close a gap measured in milliseconds.
    /// </para>
    ///
    /// <para>
    /// A settled pair is left alone: re-announcing to someone who already has us is pointless traffic
    /// on a radio with little to spare, and on a mesh it is a beacon nobody asked for. The loop stops
    /// as soon as nothing is outstanding.
    /// </para>
    /// </summary>
    private async Task AnnounceOutstandingAsync()
    {
        for (var attempt = 0; attempt < AnnounceAttempts; attempt++)
        {
            // Still owed unless BOTH are true: the announcement actually went out, and they have added
            // us back. Either alone is not enough — a send that succeeded may still have been lost on
            // the air, and them adding us says nothing about whether our own announcement arrived.
            ContactRecord[] outstanding;
            lock (_announced)
                outstanding = _store.GetContacts()
                    .Where(c => c.AddedByMe && !(_announced.Contains(c.Tag) && c.AddedByThem))
                    .ToArray();

            if (outstanding.Length == 0) return;

            var allSent = true;
            foreach (var contact in outstanding)
            {
                try
                {
                    if (await AnnounceAsync(contact.Tag).ConfigureAwait(false))
                        lock (_announced) _announced.Add(contact.Tag);
                    else
                        allSent = false;
                }
                catch (Exception) { allSent = false; }
            }

            // Everything went out. Whether they answer is their business.
            if (allSent) return;

            await Task.Delay(AnnounceRetryEvery).ConfigureAwait(false);
            if (_radio is not { IsLinked: true }) return;   // link went away; nothing to retry onto
        }
    }

    /// <summary>Raised when the contact list changes, so the UI can re-render.</summary>
    public event Action? Changed;

    public IReadOnlyList<ContactRecord> Contacts => _store.GetContacts();

    public IReadOnlyList<ContactRecord> Mutual => _store.GetContacts().Where(c => c.IsMutual).ToArray();

    public IReadOnlyList<ContactRecord> Incoming => _store.GetContacts().Where(c => c.IsIncoming).ToArray();

    /// <summary>What this device puts in its QR code — the tag, plus the key so the other side can verify it.</summary>
    public string MyInvite => BuildInvite(_me.AetherTag, _me.PublicKey);

    /// <summary>Build the <c>aether://</c> invite another phone scans to add you.</summary>
    public static string BuildInvite(string tag, byte[] publicKey) =>
        $"aether://{tag}/add?k={Convert.ToBase64String(publicKey)}";

    /// <summary>
    /// Read a scanned or pasted invite. Accepts a bare tag (<c>KXJB7-MN2P4</c>) or a full
    /// <c>aether://TAG/add?k=…</c>. Returns false when it isn't a valid tag, or when the key present
    /// doesn't actually derive that tag — a tag cannot be forged onto someone else's key.
    /// </summary>
    public static bool TryParseInvite(string? text, out string tag, out byte[]? publicKey)
    {
        tag = string.Empty;
        publicKey = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var value = text.Trim();
        if (value.StartsWith("aether://", StringComparison.OrdinalIgnoreCase))
            value = value["aether://".Length..];

        var query = string.Empty;
        var question = value.IndexOf('?');
        if (question >= 0)
        {
            query = value[(question + 1)..];
            value = value[..question];
        }

        var slash = value.IndexOf('/');
        if (slash >= 0) value = value[..slash];

        if (!AetherNetTag.TryParse(value, out var parsed)) return false;
        tag = parsed.Value;

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!pair.StartsWith("k=", StringComparison.Ordinal)) continue;
            try
            {
                var key = Convert.FromBase64String(Uri.UnescapeDataString(pair[2..]));
                // The key must actually derive the tag, or the invite is a lie.
                if (AetherNetTag.Verify(tag, key)) publicKey = key;
            }
            catch (FormatException) { /* malformed key — keep the tag, drop the key */ }
        }

        return true;
    }

    /// <summary>
    /// Add someone by tag. Records your half of the handshake and tells them over the radio, so they
    /// see an incoming request without either device consulting anything central.
    /// </summary>
    public async Task<bool> AddAsync(string tagOrInvite, string via, string? displayName = null)
    {
        if (!TryParseInvite(tagOrInvite, out var tag, out var key)) return false;
        if (string.Equals(tag, _me.AetherTag, StringComparison.OrdinalIgnoreCase)) return false; // that's you

        _store.UpsertContact(tag, key, byMe: true, byThem: false, via, displayName);
        Changed?.Invoke();

        // Adding almost always happens during first-run setup, before any link exists — so this very
        // often cannot go anywhere. What matters is that the failure is remembered: the contact record
        // holds the debt and the next link pays it.
        if (await AnnounceAsync(tag).ConfigureAwait(false))
            lock (_announced) _announced.Add(tag);

        return true;
    }

    public void Remove(string tag)
    {
        // The relationship is what granted the capability to recognise them, so ending it has to take
        // that capability with it. Leaving the key behind would keep answering their beacon for a
        // person this phone has just been told it does not know.
        _circle?.Forget(tag);
        if (_store.RemoveContact(tag)) Changed?.Invoke();
    }

    /// <summary>
    /// Tell a peer we've added them. Rides <see cref="PacketType.Data"/> with a small versioned
    /// envelope rather than minting a new packet type — a new type costs all eight language SDKs and
    /// their byte-parity fixtures, and buys nothing here.
    /// </summary>
    /// <returns>True when the radio actually carried it — not merely that we asked.</returns>
    public async Task<bool> AnnounceAsync(string? toTag = null)
    {
        if (_radio is null) return false;

        var body = JsonSerializer.SerializeToUtf8Bytes(new AddRequest
        {
            Version = 1,
            Tag = _me.AetherTag,
            PublicKey = Convert.ToBase64String(_me.PublicKey),
        }, Json);

        var payload = new byte[Marker.Length + body.Length];
        Encoding.UTF8.GetBytes(Marker).CopyTo(payload, 0);
        body.CopyTo(payload, Marker.Length);

        var packet = new MeshPacket
        {
            Type = PacketType.Data,
            SourceUhid = _me.AetherTag,
            DestinationUhid = toTag ?? string.Empty,
            Ttl = 1,
            Payload = payload,
        };

        return await _radio.SendPacketAsync(PacketSerializer.Serialize(packet)).ConfigureAwait(false);
    }

    /// <summary>
    /// Handle an inbound add-request. Returns true when the packet was ours to handle, so the caller
    /// can stop looking.
    /// </summary>
    public bool TryHandle(MeshPacket packet)
    {
        if (packet.Type != PacketType.Data) return false;
        var payload = packet.Payload;
        if (payload is null || payload.Length <= Marker.Length) return false;
        if (Encoding.UTF8.GetString(payload, 0, Marker.Length) != Marker) return false;

        AddRequest? request;
        try { request = JsonSerializer.Deserialize<AddRequest>(payload.AsSpan(Marker.Length), Json); }
        catch (JsonException) { return true; }   // ours, but malformed — swallow it

        if (request is null || string.IsNullOrWhiteSpace(request.Tag)) return true;
        if (string.Equals(request.Tag, _me.AetherTag, StringComparison.OrdinalIgnoreCase)) return true;

        byte[]? key = null;
        try
        {
            if (!string.IsNullOrEmpty(request.PublicKey))
            {
                var candidate = Convert.FromBase64String(request.PublicKey);
                // Only keep a key that genuinely derives the claimed tag.
                if (AetherNetTag.Verify(request.Tag, candidate)) key = candidate;
            }
        }
        catch (FormatException) { /* drop the key, keep the tag */ }

        if (!AetherNetTag.TryParse(request.Tag, out var parsed)) return true;

        var known = _store.GetContact(parsed.Value);
        _store.UpsertContact(parsed.Value, key, byMe: false, byThem: true, via: "radio");
        Changed?.Invoke();

        // If we'd already added them, this completes the pair — answer once so they see it too.
        if (known is { AddedByMe: true, AddedByThem: false })
            _ = AnnounceAsync(parsed.Value);

        return true;
    }

    private void OnPacket(byte[] bytes)
    {
        MeshPacket packet;
        try { packet = PacketSerializer.Deserialize(bytes); }
        catch { return; }
        TryHandle(packet);
    }

    private sealed class AddRequest
    {
        public int Version { get; set; }
        public string Tag { get; set; } = string.Empty;
        public string? PublicKey { get; set; }
    }
}

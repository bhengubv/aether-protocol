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

    public ContactService(AetherStore store, IIdentityService me, IRadioMesh? radio = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _me = me ?? throw new ArgumentNullException(nameof(me));
        _radio = radio;

        if (_radio is not null)
            _radio.PacketReceived += OnPacket;
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

        await AnnounceAsync(tag).ConfigureAwait(false);
        return true;
    }

    public void Remove(string tag)
    {
        if (_store.RemoveContact(tag)) Changed?.Invoke();
    }

    /// <summary>
    /// Tell a peer we've added them. Rides <see cref="PacketType.Data"/> with a small versioned
    /// envelope rather than minting a new packet type — a new type costs all eight language SDKs and
    /// their byte-parity fixtures, and buys nothing here.
    /// </summary>
    public async Task AnnounceAsync(string? toTag = null)
    {
        if (_radio is null) return;

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

        await _radio.SendPacketAsync(PacketSerializer.Serialize(packet)).ConfigureAwait(false);
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

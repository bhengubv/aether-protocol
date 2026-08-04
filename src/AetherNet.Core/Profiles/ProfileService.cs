// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text.Json;
using AetherNet.Constants;
using AetherNet.Privacy;
using AetherNet.Protocol;
using AetherNet.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Profiles;

/// <summary>
/// Default profile service. Shares this node's profile directly with a chosen peer and caches profiles
/// received from peers. Directed (not broadcast) to avoid leaking identity metadata to the whole mesh.
///
/// <para>The profile payload — display name, avatar, free-text status — is PII, so it is sealed to the
/// recipient inside the Signal session via <see cref="IControlPayloadCipher"/>. If there is no session the
/// publish is skipped rather than sent in clear (secure by default), and an inbound payload that cannot be
/// decrypted is dropped.</para>
/// </summary>
public sealed class ProfileService : IProfileService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IMeshSender _sender;
    private readonly IControlPayloadCipher _cipher;
    private readonly ILogger<ProfileService> _logger;

    private ProfileSyncPayload _local;
    private readonly ConcurrentDictionary<string, ProfileSyncPayload> _peerProfiles = new(StringComparer.Ordinal);

    public event EventHandler<ProfileSyncPayload>? ProfileUpdated;

    public ProfileService(IMeshSender sender, IControlPayloadCipher? cipher = null, ILogger<ProfileService>? logger = null)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _cipher = cipher ?? new NullControlPayloadCipher();
        _logger = logger ?? NullLogger<ProfileService>.Instance;
        _local = new ProfileSyncPayload { Uhid = sender.LocalUhid };
    }

    /// <inheritdoc />
    public void SetLocalProfile(string displayName, string avatarRef, string statusMessage)
    {
        _local = new ProfileSyncPayload
        {
            Uhid = _sender.LocalUhid,
            DisplayName = displayName ?? string.Empty,
            AvatarRef = avatarRef ?? string.Empty,
            StatusMessage = statusMessage ?? string.Empty,
            UpdatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
    }

    /// <inheritdoc />
    public ProfileSyncPayload GetLocalProfile() => _local;

    /// <inheritdoc />
    public async Task<bool> PublishProfileToAsync(string peerUhid, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(peerUhid);

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(_local, JsonOptions);
        var sealedPayload = await _cipher.EncryptAsync(peerUhid, plaintext, cancellationToken).ConfigureAwait(false);
        if (sealedPayload is null)
        {
            // No session — never share profile PII in clear. Skip; the host re-publishes once a session exists.
            _logger.LogDebug("Profile not shared to {Peer}: no Signal session (payload would be cleartext PII)", peerUhid);
            return false;
        }

        var packet = new MeshPacket
        {
            Type = PacketType.ProfileSync,
            SourceUhid = _sender.LocalUhid,
            DestinationUhid = peerUhid,
            Ttl = ProtocolConstants.DefaultTtl,
            Payload = sealedPayload,
        };

        var delivered = await _sender.SendAsync(packet, peerUhid, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Profile sent (encrypted) to {Peer} delivered={Delivered}", peerUhid, delivered);
        return delivered;
    }

    /// <inheritdoc />
    public async Task<bool> HandleAsync(MeshPacket packet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.Type != PacketType.ProfileSync)
            return false;

        var plaintext = await _cipher.DecryptAsync(packet.SourceUhid, packet.Payload, cancellationToken).ConfigureAwait(false);
        if (plaintext is null)
        {
            _logger.LogDebug("ProfileSync from {Source}: could not decrypt (no session / bad ciphertext) — dropped", packet.SourceUhid);
            return false;
        }

        ProfileSyncPayload? body;
        try
        {
            body = JsonSerializer.Deserialize<ProfileSyncPayload>(plaintext, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "ProfileSync from {Source}: malformed payload — dropped", packet.SourceUhid);
            return false;
        }
        if (body is null || string.IsNullOrEmpty(body.Uhid))
            return false;

        // Ignore our own profile echoed back.
        if (string.Equals(body.Uhid, _sender.LocalUhid, StringComparison.Ordinal))
            return false;

        _peerProfiles[body.Uhid] = body;
        ProfileUpdated?.Invoke(this, body);
        return true;
    }

    /// <inheritdoc />
    public ProfileSyncPayload? GetProfile(string uhid)
        => _peerProfiles.TryGetValue(uhid, out var p) ? p : null;

    /// <inheritdoc />
    public IReadOnlyList<ProfileSyncPayload> GetKnownProfiles() => _peerProfiles.Values.ToArray();
}

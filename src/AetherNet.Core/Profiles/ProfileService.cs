// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text.Json;
using AetherNet.Constants;
using AetherNet.Protocol;
using AetherNet.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Profiles;

/// <summary>
/// Default profile service. Shares this node's profile directly with a chosen peer and caches profiles
/// received from peers. Directed (not broadcast) to avoid leaking identity metadata to the whole mesh.
/// </summary>
public sealed class ProfileService : IProfileService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IMeshSender _sender;
    private readonly ILogger<ProfileService> _logger;

    private ProfileSyncPayload _local;
    private readonly ConcurrentDictionary<string, ProfileSyncPayload> _peerProfiles = new(StringComparer.Ordinal);

    public event EventHandler<ProfileSyncPayload>? ProfileUpdated;

    public ProfileService(IMeshSender sender, ILogger<ProfileService>? logger = null)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
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

        var packet = new MeshPacket
        {
            Type = PacketType.ProfileSync,
            SourceUhid = _sender.LocalUhid,
            DestinationUhid = peerUhid,
            Ttl = ProtocolConstants.DefaultTtl,
            Payload = JsonSerializer.SerializeToUtf8Bytes(_local, JsonOptions),
        };

        var delivered = await _sender.SendAsync(packet, peerUhid, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Profile sent to {Peer} delivered={Delivered}", peerUhid, delivered);
        return delivered;
    }

    /// <inheritdoc />
    public Task<bool> HandleAsync(MeshPacket packet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.Type != PacketType.ProfileSync)
            return Task.FromResult(false);

        ProfileSyncPayload? body;
        try
        {
            body = JsonSerializer.Deserialize<ProfileSyncPayload>(packet.Payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "ProfileSync from {Source}: malformed payload — dropped", packet.SourceUhid);
            return Task.FromResult(false);
        }
        if (body is null || string.IsNullOrEmpty(body.Uhid))
            return Task.FromResult(false);

        // Ignore our own profile echoed back.
        if (string.Equals(body.Uhid, _sender.LocalUhid, StringComparison.Ordinal))
            return Task.FromResult(false);

        _peerProfiles[body.Uhid] = body;
        ProfileUpdated?.Invoke(this, body);
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public ProfileSyncPayload? GetProfile(string uhid)
        => _peerProfiles.TryGetValue(uhid, out var p) ? p : null;

    /// <inheritdoc />
    public IReadOnlyList<ProfileSyncPayload> GetKnownProfiles() => _peerProfiles.Values.ToArray();
}

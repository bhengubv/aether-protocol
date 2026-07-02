// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;
using AetherNet.Constants;
using AetherNet.Protocol;
using AetherNet.Routing;
using AetherNet.Vault.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Vault;

/// <summary>
/// Wire payload for <see cref="PacketType.VaultShardRequest"/> (42) — a node asks the mesh for an
/// erasure-coded shard it needs to recover a file. Field order: shard_hash, requester_uhid. snake_case
/// keys pinned by <see cref="JsonPropertyNameAttribute"/>. Byte-identity gate: fixtures/vaultshard/vectors.json.
/// </summary>
public sealed class VaultShardRequestPayload
{
    [JsonPropertyName("shard_hash")] public string ShardHash { get; set; } = string.Empty;
    [JsonPropertyName("requester_uhid")] public string RequesterUhid { get; set; } = string.Empty;

    public VaultShardRequest ToRequest() => new() { ShardHash = ShardHash, RequesterUhid = RequesterUhid };
}

/// <summary>
/// Binds <see cref="PacketType.VaultShardRequest"/> (42) to the mesh: ask peers for a shard, and surface
/// inbound shard requests via <see cref="ShardRequested"/> (the host answers from <c>IVaultService</c>
/// if it holds the shard). Transport for the aether-vault erasure-coded-storage extension.
/// </summary>
public interface IVaultShardRequestService
{
    /// <summary>Raised when a peer requests a shard.</summary>
    event EventHandler<VaultShardRequest>? ShardRequested;

    /// <summary>Broadcast a request for <paramref name="shardHash"/>. Returns the number of peers reached.</summary>
    Task<int> RequestShardAsync(string shardHash, CancellationToken cancellationToken = default);

    /// <summary>Process an inbound <see cref="PacketType.VaultShardRequest"/>. Returns false on wrong type or malformed payload.</summary>
    Task<bool> HandleAsync(MeshPacket packet, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class VaultShardRequestService : IVaultShardRequestService
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly IMeshSender _sender;
    private readonly ILogger<VaultShardRequestService> _logger;

    public event EventHandler<VaultShardRequest>? ShardRequested;

    public VaultShardRequestService(IMeshSender sender, ILogger<VaultShardRequestService>? logger = null)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _logger = logger ?? NullLogger<VaultShardRequestService>.Instance;
    }

    /// <inheritdoc />
    public async Task<int> RequestShardAsync(string shardHash, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(shardHash);
        var payload = new VaultShardRequestPayload { ShardHash = shardHash, RequesterUhid = _sender.LocalUhid };
        var packet = new MeshPacket
        {
            Type = PacketType.VaultShardRequest,
            SourceUhid = _sender.LocalUhid,
            DestinationUhid = "*",
            Ttl = ProtocolConstants.DefaultTtl,
            Payload = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions),
        };
        var delivered = await _sender.BroadcastAsync(packet, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("VaultShardRequest {Shard} broadcast to {N} peers", shardHash, delivered);
        return delivered;
    }

    /// <inheritdoc />
    public Task<bool> HandleAsync(MeshPacket packet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.Type != PacketType.VaultShardRequest)
            return Task.FromResult(false);

        VaultShardRequestPayload? body;
        try
        {
            body = JsonSerializer.Deserialize<VaultShardRequestPayload>(packet.Payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "VaultShardRequest from {Source}: malformed payload — dropped", packet.SourceUhid);
            return Task.FromResult(false);
        }
        if (body is null || string.IsNullOrEmpty(body.ShardHash))
            return Task.FromResult(false);

        ShardRequested?.Invoke(this, body.ToRequest());
        return Task.FromResult(true);
    }
}

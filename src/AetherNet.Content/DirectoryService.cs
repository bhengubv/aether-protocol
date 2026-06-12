// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text.Json;
using AetherNet.Constants;
using AetherNet.Content.Models;
using AetherNet.Protocol;
using AetherNet.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Content;

/// <summary>
/// Default <see cref="IDirectoryService"/> implementation — in-process catalogue with
/// broadcast publish, query/response by correlation id, and cancellation-aware wait
/// loops. Persistence is the host's responsibility (rehydrate via <see cref="PublishAsync"/>
/// on startup if you want a non-volatile catalogue).
///
/// <para>Added in v1.2.0 — closes Issue #60.</para>
/// </summary>
public sealed class DirectoryService : IDirectoryService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>Default timeout for <see cref="ResolveAsync"/> when no value is supplied.</summary>
    public static readonly TimeSpan DefaultQueryTimeout = TimeSpan.FromSeconds(5);

    // PRIVACY: domain-separation prefix for the name hash that travels on the wire. The plaintext
    // name never leaves a node; peers match on this salted hash (private DNS-style resolution).
    private const string NameHashSalt = "aether-dir-name-v1:";

    private readonly IMeshSender _sender;
    private readonly ILogger<DirectoryService> _logger;

    // Local catalogue: PLAINTEXT name → descriptor. Stays local to this node (never serialized);
    // backs ListNamesAsync. StringComparer.Ordinal — names are opaque, case-sensitive identifiers.
    private readonly ConcurrentDictionary<string, ContentDescriptor> _catalogue =
        new(StringComparer.Ordinal);

    // Wire index: salted name-hash → descriptor. The wire only ever carries the hash, so a holder
    // can answer a hashed query without any plaintext name leaving (or entering) a node.
    private readonly ConcurrentDictionary<string, ContentDescriptor> _catalogueByHash =
        new(StringComparer.Ordinal);

    // Outstanding queries keyed by QueryId. Completed when a matching NamePublish arrives,
    // or set to null on timeout / cancellation.
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<ContentDescriptor?>> _pendingQueries =
        new();

    public event EventHandler<DirectoryEntryAnnouncedEventArgs>? EntryAnnounced;

    public DirectoryService(IMeshSender sender, ILogger<DirectoryService>? logger = null)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _logger = logger ?? NullLogger<DirectoryService>.Instance;
    }

    public async Task PublishAsync(string name, ContentDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(descriptor);

        _catalogue[name] = descriptor;
        var nameHash = HashName(name);
        _catalogueByHash[nameHash] = descriptor;

        var payload = JsonSerializer.SerializeToUtf8Bytes(new NamePublishPayload
        {
            NameHash = nameHash,
            Descriptor = descriptor,
            InResponseToQueryId = null,
        }, JsonOptions);

        var packet = new MeshPacket
        {
            Type = PacketType.NamePublish,
            SourceUhid = _sender.LocalUhid,
            Ttl = ProtocolConstants.DefaultTtl,
            Payload = payload,
        };

        var delivered = await _sender.BroadcastAsync(packet, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Directory: published name {Name} to {Count} peers (root={Root})",
            name, delivered, descriptor.RootHash);
    }

    public async Task<ContentDescriptor?> ResolveAsync(string name, TimeSpan? queryTimeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (_catalogue.TryGetValue(name, out var cached))
        {
            return cached;
        }

        // A name learned from an inbound (hashed) announce lives in the hash index only — check it
        // too before going to the network. We know the plaintext here, so we can hash it and match.
        var nameHash = HashName(name);
        if (_catalogueByHash.TryGetValue(nameHash, out var cachedByHash))
        {
            return cachedByHash;
        }

        var query = new NameQueryPayload
        {
            NameHash = nameHash,
            QueryId = Guid.NewGuid(),
        };
        var tcs = new TaskCompletionSource<ContentDescriptor?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingQueries[query.QueryId] = tcs;

        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(query, JsonOptions);
            var packet = new MeshPacket
            {
                Type = PacketType.NameQuery,
                SourceUhid = _sender.LocalUhid,
                Ttl = ProtocolConstants.DefaultTtl,
                Payload = payload,
            };
            await _sender.BroadcastAsync(packet, cancellationToken).ConfigureAwait(false);

            var timeout = queryTimeout ?? DefaultQueryTimeout;
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            await using var registration = linkedCts.Token.Register(() => tcs.TrySetResult(null)).ConfigureAwait(false);

            var resolved = await tcs.Task.ConfigureAwait(false);
            if (resolved is not null)
            {
                // We know this plaintext name (we asked for it) → cache locally by both keys.
                _catalogue[name] = resolved;
                _catalogueByHash[query.NameHash] = resolved;
            }
            return resolved;
        }
        finally
        {
            _pendingQueries.TryRemove(query.QueryId, out _);
        }
    }

    public Task<IReadOnlyList<string>> ListNamesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> snapshot = _catalogue.Keys.ToArray();
        return Task.FromResult(snapshot);
    }

    public async Task HandleAsync(MeshPacket packet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packet);

        switch (packet.Type)
        {
            case PacketType.NamePublish:
                HandlePublish(packet);
                break;
            case PacketType.NameQuery:
                await HandleQueryAsync(packet, cancellationToken).ConfigureAwait(false);
                break;
            default:
                _logger.LogDebug("Directory HandleAsync ignoring non-directory packet type {Type}", packet.Type);
                break;
        }
    }

    private void HandlePublish(MeshPacket packet)
    {
        NamePublishPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<NamePublishPayload>(packet.Payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Directory: failed to deserialize NamePublish payload from packet {Id}", packet.Id);
            return;
        }
        if (payload is null || string.IsNullOrEmpty(payload.NameHash) || payload.Descriptor is null)
        {
            return;
        }

        // Cache by hash — for an unsolicited announce we only ever learn the hash, never the
        // plaintext name. (ResolveAsync caches the plaintext locally for names this node asked for.)
        _catalogueByHash[payload.NameHash] = payload.Descriptor;

        // Query-response correlation.
        if (payload.InResponseToQueryId is { } queryId
            && _pendingQueries.TryRemove(queryId, out var tcs))
        {
            tcs.TrySetResult(payload.Descriptor);
        }

        EntryAnnounced?.Invoke(this, new DirectoryEntryAnnouncedEventArgs
        {
            NameHash = payload.NameHash,
            Descriptor = payload.Descriptor,
            SourceUhid = packet.SourceUhid,
            AnnouncedAtUtc = DateTime.UtcNow,
        });
    }

    private async Task HandleQueryAsync(MeshPacket packet, CancellationToken cancellationToken)
    {
        NameQueryPayload? query;
        try
        {
            query = JsonSerializer.Deserialize<NameQueryPayload>(packet.Payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Directory: failed to deserialize NameQuery payload from packet {Id}", packet.Id);
            return;
        }
        if (query is null || string.IsNullOrEmpty(query.NameHash))
        {
            return;
        }

        if (!_catalogueByHash.TryGetValue(query.NameHash, out var descriptor))
        {
            // We don't hold this name — silently ignore. Other peers may answer.
            return;
        }

        var responsePayload = JsonSerializer.SerializeToUtf8Bytes(new NamePublishPayload
        {
            NameHash = query.NameHash,
            Descriptor = descriptor,
            InResponseToQueryId = query.QueryId,
        }, JsonOptions);

        var response = new MeshPacket
        {
            Type = PacketType.NamePublish,
            SourceUhid = _sender.LocalUhid,
            DestinationUhid = packet.SourceUhid,
            Ttl = ProtocolConstants.DefaultTtl,
            Payload = responsePayload,
        };

        await _sender.SendAsync(response, packet.SourceUhid, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Directory: answered query for {NameHash} from {Asker}", query.NameHash, packet.SourceUhid);
    }

    // Salted hash of an application name for the wire. Names are case-sensitive opaque identifiers
    // (StringComparer.Ordinal), so the exact UTF-8 bytes are hashed — no normalisation.
    private static string HashName(string name)
        => Convert.ToHexString(
               System.Security.Cryptography.SHA256.HashData(
                   System.Text.Encoding.UTF8.GetBytes(NameHashSalt + name)))
           .ToLowerInvariant();
}

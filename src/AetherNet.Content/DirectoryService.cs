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
/// Default <see cref="IDirectoryService"/> implementation — in-process catalogue with broadcast
/// publish, query/response by correlation id, and cancellation-aware wait loops. Persistence is the
/// host's responsibility (rehydrate via <see cref="PublishAsync"/> on startup for a non-volatile
/// catalogue).
///
/// <para>
/// Bindings are either UNSIGNED (legacy — anyone may publish a name → descriptor, last-writer-wins) or
/// AUTHENTICATED via <see cref="PublishSignedAsync"/>. An authenticated binding is filed under a slot
/// the directory derives from the signing key — <c>ScopedSlot(verifier.DeriveScope(author), Hash(name))</c>
/// (see <see cref="NameHashing.ScopedSlot"/>). Because the slot is scope-bound and the scope comes from
/// the key, <b>only the scope's owner can occupy its slots</b>: an impostor signing the same name lands
/// in the impostor's own scope, never the owner's. That closes name-slot squatting outright — there is
/// no shared slot to race for. A binding is admitted only if the signature verifies (via an injected
/// <see cref="INameBindingVerifier"/>) and the version strictly increases.
/// </para>
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

    private readonly IMeshSender _sender;
    private readonly INameBindingVerifier? _verifier;
    private readonly ILogger<DirectoryService> _logger;

    // Local catalogue: PLAINTEXT name → descriptor. Stays local to this node (never serialized);
    // backs ListNamesAsync. StringComparer.Ordinal — names are opaque, case-sensitive identifiers.
    private readonly ConcurrentDictionary<string, ContentDescriptor> _catalogue =
        new(StringComparer.Ordinal);

    // Unsigned wire index: salted name-hash → descriptor. The wire only ever carries the hash, so a
    // holder can answer a hashed query without any plaintext name leaving (or entering) a node.
    private readonly ConcurrentDictionary<string, ContentDescriptor> _catalogueByHash =
        new(StringComparer.Ordinal);

    // Authenticated bindings keyed by SCOPED SLOT (= ScopedSlot(DeriveScope(author), innerNameHash)).
    // The slot is derived from the signing key, so each entry is owned exclusively by one author —
    // squatting is impossible and a single entry per slot suffices. Backs ResolveBindingByScopeAsync
    // and the query responder.
    private readonly ConcurrentDictionary<string, BindingAuth> _authBySlot =
        new(StringComparer.Ordinal);

    // Outstanding queries keyed by QueryId. Completed when a matching NamePublish arrives,
    // or set to null on timeout / cancellation.
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<ContentDescriptor?>> _pendingQueries =
        new();

    public event EventHandler<DirectoryEntryAnnouncedEventArgs>? EntryAnnounced;

    public DirectoryService(IMeshSender sender, INameBindingVerifier? verifier = null, ILogger<DirectoryService>? logger = null)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _verifier = verifier;
        _logger = logger ?? NullLogger<DirectoryService>.Instance;
    }

    public async Task PublishAsync(string name, ContentDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(descriptor);

        _catalogue[name] = descriptor;
        var nameHash = NameHashing.Hash(name);
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

    public async Task PublishSignedAsync(string name, ContentDescriptor descriptor, byte[] authorPublicKey, long version, byte[] signature, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(authorPublicKey);
        ArgumentNullException.ThrowIfNull(signature);
        if (_verifier is null)
            throw new InvalidOperationException(
                "PublishSignedAsync requires an INameBindingVerifier (e.g. via AddCards) to derive the ownership scope for the slot.");

        var innerNameHash = NameHashing.Hash(name);
        var scope = _verifier.DeriveScope(authorPublicKey);
        var slot = NameHashing.ScopedSlot(scope, innerNameHash);

        // The publisher trusts its own binding — store it locally (plaintext for ListNames + the slot).
        _catalogue[name] = descriptor;
        _authBySlot[slot] = new BindingAuth(innerNameHash, version, authorPublicKey, signature, descriptor);

        // The wire carries the INNER name hash; every receiver re-derives the slot from the author key,
        // so no one can file a binding under another author's scope.
        var payload = JsonSerializer.SerializeToUtf8Bytes(new NamePublishPayload
        {
            NameHash = innerNameHash,
            Descriptor = descriptor,
            InResponseToQueryId = null,
            Version = version,
            AuthorPublicKey = Convert.ToBase64String(authorPublicKey),
            Signature = Convert.ToBase64String(signature),
        }, JsonOptions);

        var packet = new MeshPacket
        {
            Type = PacketType.NamePublish,
            SourceUhid = _sender.LocalUhid,
            Ttl = ProtocolConstants.DefaultTtl,
            Payload = payload,
        };

        var delivered = await _sender.BroadcastAsync(packet, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Directory: published SIGNED name {Name} v{Version} to {Count} peers (root={Root})",
            name, version, delivered, descriptor.RootHash);
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
        var nameHash = NameHashing.Hash(name);
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

    public async Task<NameBinding?> ResolveBindingByScopeAsync(string scope, string name, TimeSpan? queryTimeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(scope);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var slot = NameHashing.ScopedSlot(scope, NameHashing.Hash(name));

        if (_authBySlot.TryGetValue(slot, out var local))
        {
            return ToBinding(local);
        }

        // Not held locally — ask the mesh for this slot, then re-check.
        await BroadcastQueryAndWaitAsync(slot, queryTimeout ?? DefaultQueryTimeout, cancellationToken).ConfigureAwait(false);

        return _authBySlot.TryGetValue(slot, out var found) ? ToBinding(found) : null;
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

    // Broadcast a NameQuery for a wire key (an unsigned name-hash or a scoped slot) and wait for the
    // first matching response (which HandlePublish admits into the right store), or time out.
    private async Task BroadcastQueryAndWaitAsync(string wireKey, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var query = new NameQueryPayload { NameHash = wireKey, QueryId = Guid.NewGuid() };
        var tcs = new TaskCompletionSource<ContentDescriptor?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingQueries[query.QueryId] = tcs;
        try
        {
            var packet = new MeshPacket
            {
                Type = PacketType.NameQuery,
                SourceUhid = _sender.LocalUhid,
                Ttl = ProtocolConstants.DefaultTtl,
                Payload = JsonSerializer.SerializeToUtf8Bytes(query, JsonOptions),
            };
            await _sender.BroadcastAsync(packet, cancellationToken).ConfigureAwait(false);

            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            await using var registration = linkedCts.Token.Register(() => tcs.TrySetResult(null)).ConfigureAwait(false);
            await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pendingQueries.TryRemove(query.QueryId, out _);
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

        var isSigned = !string.IsNullOrEmpty(payload.AuthorPublicKey) && !string.IsNullOrEmpty(payload.Signature);
        if (isSigned)
        {
            if (!TryAdmitSignedBinding(payload))
            {
                return;
            }
        }
        else
        {
            _catalogueByHash[payload.NameHash] = payload.Descriptor;
        }

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
            AuthorPublicKey = isSigned ? payload.AuthorPublicKey : null,
            Version = isSigned ? payload.Version : 0,
        });
    }

    // Verify + admission-control an authenticated binding. Returns true and stores it (under a slot
    // derived from the signing key) when accepted; false (with a reason logged) if the base64 is
    // malformed, the descriptor fails self-verification, no verifier is configured, the signature is
    // invalid, or the version is not strictly newer than the one already held for that slot.
    private bool TryAdmitSignedBinding(NamePublishPayload payload)
    {
        byte[] authorKey;
        byte[] signature;
        try
        {
            authorKey = Convert.FromBase64String(payload.AuthorPublicKey!);
            signature = Convert.FromBase64String(payload.Signature!);
        }
        catch (FormatException)
        {
            _logger.LogWarning("Directory: malformed base64 author key / signature on {NameHash}", payload.NameHash);
            return false;
        }

        // Manifest self-integrity: the descriptor's chunk hashes must recompute to its root hash.
        if (!payload.Descriptor.VerifySelf())
        {
            _logger.LogWarning("Directory: descriptor failed self-verification for {NameHash}", payload.NameHash);
            return false;
        }

        if (_verifier is null)
        {
            // Fail safe: a node with no verifier cannot authenticate or scope a signed binding, so it
            // must not cache it. (An unsigned-only node simply never learns card names.)
            _logger.LogWarning("Directory: signed binding {NameHash} arrived but no INameBindingVerifier is configured — dropping", payload.NameHash);
            return false;
        }

        // The wire carries the inner name hash; the signature is over it (+ author, version, root hash).
        var innerNameHash = payload.NameHash;
        var body = NameBindingCodec.BuildSignableBody(innerNameHash, authorKey, payload.Version, payload.Descriptor.RootHash);
        if (!_verifier.Verify(authorKey, body, signature))
        {
            _logger.LogWarning("Directory: invalid signature on binding {NameHash} — rejecting", payload.NameHash);
            return false;
        }

        // Derive the slot from the AUTHOR'S key — never from anything the sender chose. An impostor's
        // binding therefore lands in the impostor's own scope slot, not the owner's: squatting is impossible.
        var scope = _verifier.DeriveScope(authorKey);
        var slot = NameHashing.ScopedSlot(scope, innerNameHash);

        if (_authBySlot.TryGetValue(slot, out var existing) && payload.Version <= existing.Version)
        {
            _logger.LogWarning("Directory: stale version {Version} (held {Held}) on slot {Slot} — rejecting rollback",
                payload.Version, existing.Version, slot);
            return false;
        }

        _authBySlot[slot] = new BindingAuth(innerNameHash, payload.Version, authorKey, signature, payload.Descriptor);
        return true;
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

        NamePublishPayload response;

        // The query key is either a scoped slot (authenticated) or an unsigned name hash. Answer from
        // whichever store holds it. A signed answer echoes the INNER name hash so the asker re-derives
        // the same slot from the author key — never a downgrade to unsigned.
        if (_authBySlot.TryGetValue(query.NameHash, out var signed))
        {
            response = new NamePublishPayload
            {
                NameHash = signed.InnerNameHash,
                Descriptor = signed.Descriptor,
                InResponseToQueryId = query.QueryId,
                Version = signed.Version,
                AuthorPublicKey = Convert.ToBase64String(signed.AuthorPublicKey),
                Signature = Convert.ToBase64String(signed.Signature),
            };
        }
        else if (_catalogueByHash.TryGetValue(query.NameHash, out var descriptor))
        {
            response = new NamePublishPayload
            {
                NameHash = query.NameHash,
                Descriptor = descriptor,
                InResponseToQueryId = query.QueryId,
            };
        }
        else
        {
            // We don't hold this key — silently ignore. Other peers may answer.
            return;
        }

        var responsePacket = new MeshPacket
        {
            Type = PacketType.NamePublish,
            SourceUhid = _sender.LocalUhid,
            DestinationUhid = packet.SourceUhid,
            Ttl = ProtocolConstants.DefaultTtl,
            Payload = JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions),
        };

        await _sender.SendAsync(responsePacket, packet.SourceUhid, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Directory: answered query for {NameHash} from {Asker}", query.NameHash, packet.SourceUhid);
    }

    private static NameBinding ToBinding(BindingAuth b)
        => new(b.Descriptor, b.AuthorPublicKey, b.Version, b.Signature, Authenticated: true);

    // Verified metadata for an authenticated binding. InnerNameHash is the wire name hash the signature
    // covers; the storage slot is ScopedSlot(DeriveScope(author), InnerNameHash).
    private sealed record BindingAuth(
        string InnerNameHash,
        long Version,
        byte[] AuthorPublicKey,
        byte[] Signature,
        ContentDescriptor Descriptor);
}

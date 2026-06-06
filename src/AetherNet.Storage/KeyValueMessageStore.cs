// SPDX-License-Identifier: MIT

using System.Text.Json;
using AetherNet.Messaging;
using AetherNet.Messaging.Models;

namespace AetherNet.Storage;

/// <summary>
/// <see cref="IMessageStore"/> implementation backed by an arbitrary <see cref="IKeyValueStore"/>.
/// Messages are JSON-encoded under <c>msg:&lt;guid&gt;</c>. List queries scan the keyspace —
/// fine for typical mobile-app message volumes; hosts with very large mailboxes ship custom indexes.
/// </summary>
public sealed class KeyValueMessageStore : IMessageStore
{
    private const string Prefix = "msg:";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IKeyValueStore _kv;

    public KeyValueMessageStore(IKeyValueStore kv)
    {
        _kv = kv ?? throw new ArgumentNullException(nameof(kv));
    }

    public Task SaveAsync(MeshMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        return _kv.PutAsync(Key(message.Id), bytes, cancellationToken);
    }

    public async Task<MeshMessage?> GetAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        var bytes = await _kv.GetAsync(Key(messageId), cancellationToken).ConfigureAwait(false);
        return bytes is null ? null : JsonSerializer.Deserialize<MeshMessage>(bytes, JsonOptions);
    }

    public async Task UpdateStatusAsync(Guid messageId, MessageStatus status, CancellationToken cancellationToken = default)
    {
        var message = await GetAsync(messageId, cancellationToken).ConfigureAwait(false);
        if (message is null) return;
        message.Status = status;
        await SaveAsync(message, cancellationToken).ConfigureAwait(false);
    }

    public async Task IncrementRetryAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        var message = await GetAsync(messageId, cancellationToken).ConfigureAwait(false);
        if (message is null) return;
        message.RetryCount++;
        await SaveAsync(message, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MeshMessage>> GetPendingOutboxAsync(string senderUhid, int maxRetries, CancellationToken cancellationToken = default)
    {
        var matches = new List<MeshMessage>();
        await foreach (var message in EnumerateAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(message.SenderUhid, senderUhid, StringComparison.Ordinal)
                && message.RetryCount < maxRetries
                && (message.Status is MessageStatus.Pending or MessageStatus.Sending))
            {
                matches.Add(message);
            }
        }
        matches.Sort((a, b) => a.CreatedAt.CompareTo(b.CreatedAt));
        return matches;
    }

    public async Task<IReadOnlyList<MeshMessage>> GetInboxAsync(string recipientUhid, int limit, CancellationToken cancellationToken = default)
    {
        var matches = new List<MeshMessage>();
        await foreach (var message in EnumerateAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(message.RecipientUhid, recipientUhid, StringComparison.Ordinal))
                matches.Add(message);
        }
        matches.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
        return matches.Take(Math.Max(0, limit)).ToArray();
    }

    public async Task<IReadOnlyList<MeshMessage>> GetOutboxAsync(string senderUhid, int limit, CancellationToken cancellationToken = default)
    {
        var matches = new List<MeshMessage>();
        await foreach (var message in EnumerateAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(message.SenderUhid, senderUhid, StringComparison.Ordinal))
                matches.Add(message);
        }
        matches.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
        return matches.Take(Math.Max(0, limit)).ToArray();
    }

    private async IAsyncEnumerable<MeshMessage> EnumerateAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var key in _kv.ListKeysAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!key.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            var bytes = await _kv.GetAsync(key, cancellationToken).ConfigureAwait(false);
            if (bytes is null) continue;
            var message = JsonSerializer.Deserialize<MeshMessage>(bytes, JsonOptions);
            if (message is not null) yield return message;
        }
    }

    private static string Key(Guid id) => Prefix + id.ToString("N");
}

// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using AetherMesh.Messaging.Models;

namespace AetherMesh.Messaging;

/// <summary>
/// Thread-safe, process-local message store. Suitable for tests, demos, and any
/// host that does not need messages to survive a restart.
/// </summary>
public sealed class InMemoryMessageStore : IMessageStore
{
    private readonly ConcurrentDictionary<Guid, MeshMessage> _messages = new();

    public Task SaveAsync(MeshMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        _messages[message.Id] = message;
        return Task.CompletedTask;
    }

    public Task<MeshMessage?> GetAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        _messages.TryGetValue(messageId, out var message);
        return Task.FromResult(message);
    }

    public Task UpdateStatusAsync(Guid messageId, MessageStatus status, CancellationToken cancellationToken = default)
    {
        if (_messages.TryGetValue(messageId, out var message))
            message.Status = status;
        return Task.CompletedTask;
    }

    public Task IncrementRetryAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        if (_messages.TryGetValue(messageId, out var message))
            message.RetryCount++;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MeshMessage>> GetPendingOutboxAsync(string senderUhid, int maxRetries, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<MeshMessage> pending = _messages.Values
            .Where(m =>
                string.Equals(m.SenderUhid, senderUhid, StringComparison.Ordinal)
                && m.RetryCount < maxRetries
                && (m.Status == MessageStatus.Pending || m.Status == MessageStatus.Sending))
            .OrderBy(m => m.CreatedAt)
            .ToArray();
        return Task.FromResult(pending);
    }

    public Task<IReadOnlyList<MeshMessage>> GetInboxAsync(string recipientUhid, int limit, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<MeshMessage> inbox = _messages.Values
            .Where(m => string.Equals(m.RecipientUhid, recipientUhid, StringComparison.Ordinal))
            .OrderByDescending(m => m.CreatedAt)
            .Take(Math.Max(0, limit))
            .ToArray();
        return Task.FromResult(inbox);
    }

    public Task<IReadOnlyList<MeshMessage>> GetOutboxAsync(string senderUhid, int limit, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<MeshMessage> outbox = _messages.Values
            .Where(m => string.Equals(m.SenderUhid, senderUhid, StringComparison.Ordinal))
            .OrderByDescending(m => m.CreatedAt)
            .Take(Math.Max(0, limit))
            .ToArray();
        return Task.FromResult(outbox);
    }
}

// SPDX-License-Identifier: MIT

using AetherNet.Messaging.Models;

namespace AetherNet.Messaging;

/// <summary>
/// Persistence boundary for the messaging service. Outbox state must survive a
/// process restart so retries don't drop on the floor; the default
/// <see cref="InMemoryMessageStore"/> is process-local and only suitable for tests.
/// </summary>
public interface IMessageStore
{
    /// <summary>Insert or replace a message.</summary>
    Task SaveAsync(MeshMessage message, CancellationToken cancellationToken = default);

    /// <summary>Look up a message by id. Returns null if absent.</summary>
    Task<MeshMessage?> GetAsync(Guid messageId, CancellationToken cancellationToken = default);

    /// <summary>Update a message's lifecycle state in-place.</summary>
    Task UpdateStatusAsync(Guid messageId, MessageStatus status, CancellationToken cancellationToken = default);

    /// <summary>Increment the retry counter for a message.</summary>
    Task IncrementRetryAsync(Guid messageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pending outbox messages for the local sender that have not yet exhausted retries
    /// (i.e. <c>RetryCount &lt; maxRetries</c> AND <see cref="MessageStatus.Pending"/> or
    /// <see cref="MessageStatus.Sending"/>).
    /// </summary>
    Task<IReadOnlyList<MeshMessage>> GetPendingOutboxAsync(string senderUhid, int maxRetries, CancellationToken cancellationToken = default);

    /// <summary>Most-recent inbox messages addressed to the local recipient, newest first.</summary>
    Task<IReadOnlyList<MeshMessage>> GetInboxAsync(string recipientUhid, int limit, CancellationToken cancellationToken = default);

    /// <summary>Most-recent outbox messages from the local sender, newest first.</summary>
    Task<IReadOnlyList<MeshMessage>> GetOutboxAsync(string senderUhid, int limit, CancellationToken cancellationToken = default);
}

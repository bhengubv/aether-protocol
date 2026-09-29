// SPDX-License-Identifier: MIT

using AetherNet.Identity;

namespace AetherNetNodeService.Host;

/// <summary>
/// The reference in-process implementation of <see cref="IAetherNodeClient"/>. It adapts the device's real
/// <see cref="INodeIdentity"/>, a messaging seam (<see cref="INodeMessaging"/>) and a presence seam
/// (<see cref="INodeLinkSource"/>) to the bind contract.
///
/// <para>
/// The private key never passes through here — identity operations are <c>GetTag</c>, <c>GetPublicKey</c>
/// and <c>Sign</c>, exactly the closed surface of <see cref="INodeIdentity"/>. A locked node is reported as
/// <see cref="AetherNodeErrorCode.NodeUnavailable"/>, never as an absent identity, so a consumer never
/// mistakes "locked" for "mint a new one". A cross-process host (an Android bound <c>Service</c>) wraps this
/// same object and adds only the transport and the per-app grant check.
/// </para>
/// </summary>
public sealed class AetherNodeService : IAetherNodeClient
{
    private readonly INodeIdentity _identity;
    private readonly INodeMessaging _messaging;
    private readonly INodeLinkSource _link;
    private readonly INodeMeeting? _meeting;
    private readonly INodeIdentityRecovery? _recovery;

    /// <param name="meeting">How the radios are told whom to reach; null on a host with no radios.</param>
    /// <param name="recovery">
    /// Where the recovery phrase comes from — the same store as <paramref name="identity"/>. Null on a host that
    /// does not hand it out, which then answers every request for it with a refusal.
    /// </param>
    public AetherNodeService(INodeIdentity identity, INodeMessaging messaging, INodeLinkSource link, INodeMeeting? meeting = null,
        INodeIdentityRecovery? recovery = null)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _messaging = messaging ?? throw new ArgumentNullException(nameof(messaging));
        _link = link ?? throw new ArgumentNullException(nameof(link));
        _meeting = meeting;
        _recovery = recovery;
    }

    /// <inheritdoc />
    public async Task<AetherNetTag> GetTagAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _identity.GetOrMintAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (NodeIdentityUnavailableException ex)
        {
            throw Locked(ex);
        }
    }

    /// <inheritdoc />
    public async Task<byte[]> GetPublicKeyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _identity.GetPublicKeyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (NodeIdentityUnavailableException ex)
        {
            throw Locked(ex);
        }
    }

    /// <inheritdoc />
    public async Task<byte[]> SignAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _identity.SignAsync(data.ToArray(), cancellationToken).ConfigureAwait(false);
        }
        catch (NodeIdentityUnavailableException ex)
        {
            throw Locked(ex);
        }
    }

    /// <inheritdoc />
    public Task<OutboundResult> SendAsync(AetherNetTag to, ReadOnlyMemory<byte> payload, Guid messageId, CancellationToken cancellationToken = default)
        => _messaging.SendAsync(to, payload, messageId, cancellationToken);

    /// <inheritdoc />
    public Task<OutboundResult> SendAsync(AetherNetTag to, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        => SendAsync(to, payload, Guid.NewGuid(), cancellationToken);

    /// <inheritdoc />
    public Task MeetAsync(IReadOnlyList<NodeContact> contacts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contacts);
        _meeting?.Meet(contacts);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<InboundMessage>> GetInboxAsync(int limit = 50, CancellationToken cancellationToken = default)
        => _messaging.GetInboxAsync(limit, cancellationToken);

    /// <inheritdoc />
    public Task<NodeLinkStatus> GetLinkAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_link.Current);

    /// <inheritdoc />
    public async Task<string> GetRecoveryPhraseAsync(CancellationToken cancellationToken = default)
    {
        if (_recovery is null)
            throw new AetherNodeException(AetherNodeErrorCode.Internal, "this service does not hand out the recovery phrase");

        try
        {
            return await _recovery.ExportRecoveryPhraseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (NodeIdentityUnavailableException ex)
        {
            throw Locked(ex);
        }
        catch (InvalidOperationException ex)
        {
            // Nothing minted yet — said as "absent", which is the one case it truly is.
            throw new AetherNodeException(AetherNodeErrorCode.IdentityAbsent, ex.Message, ex);
        }
    }

    /// <inheritdoc />
    public IDisposable Subscribe(IAetherNodeEvents listener)
    {
        if (listener is null)
        {
            throw new ArgumentNullException(nameof(listener));
        }
        return new Subscription(this, listener);
    }

    private static AetherNodeException Locked(Exception inner)
        => new(AetherNodeErrorCode.NodeUnavailable, "the node is present but locked", inner);

    private sealed class Subscription : IDisposable
    {
        private readonly AetherNodeService _owner;
        private readonly IAetherNodeEvents _listener;

        public Subscription(AetherNodeService owner, IAetherNodeEvents listener)
        {
            _owner = owner;
            _listener = listener;
            _owner._messaging.Inbound += OnInbound;
            _owner._messaging.Delivered += OnDelivered;
            _owner._link.Changed += OnLinkChanged;
        }

        private void OnInbound(InboundMessage message) => _listener.OnInbound(message);

        private void OnDelivered(Guid messageId) => _listener.OnDelivered(messageId);

        private void OnLinkChanged() => _listener.OnLinkChanged(_owner._link.Current);

        public void Dispose()
        {
            _owner._messaging.Inbound -= OnInbound;
            _owner._messaging.Delivered -= OnDelivered;
            _owner._link.Changed -= OnLinkChanged;
        }
    }
}

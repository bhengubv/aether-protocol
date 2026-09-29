// SPDX-License-Identifier: MIT

using AetherNet.Identity;

namespace AetherNetNodeService.Client;

/// <summary>
/// Identity backup and restore, as a connected app sees them. The identity lives in AetherNetService, which has
/// no screen — so backup is the app asking the service for the 24 words and showing them, after the phone itself
/// has confirmed its owner (see <see cref="IOwnerCheck"/>). Nothing is asked of the service until then, and the
/// words are returned to be shown, never kept.
/// </summary>
/// <remarks>
/// Restore is not here yet. AetherNetService makes an identity the moment it first starts, and adopting a phrase
/// over a live identity is refused by design, so restoring on a new phone needs its own path. Until then adopt and
/// restore throw <see cref="NotSupportedException"/> — not the contract's <see cref="InvalidOperationException"/>,
/// which means "this device has no identity yet": not here is a different answer from not at all.
/// </remarks>
public sealed class NodeClientRecovery : INodeIdentityRecovery
{
    internal const string RestoreNotHere =
        "Restoring an identity from its recovery phrase is not available from an app yet — AetherNetService makes an identity when it first starts.";

    /// <summary>What the phone's own confirm screen says the check is for.</summary>
    internal const string Reason = "Show your 24 recovery words";

    private readonly IAetherNodeClient _service;
    private readonly IOwnerCheck _owner;

    public NodeClientRecovery(IAetherNodeClient service, IOwnerCheck owner)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    public ValueTask<AetherNetTag> AdoptSeedAsync(byte[] seed, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(RestoreNotHere);

    /// <exception cref="OwnerNotConfirmedException">The phone did not confirm its owner; nothing was asked or shown.</exception>
    /// <exception cref="NodeIdentityUnavailableException">The identity is there but cannot be opened right now.</exception>
    /// <exception cref="InvalidOperationException">This device has no identity yet.</exception>
    /// <exception cref="AetherNodeException">AetherNetService could not be reached.</exception>
    public async ValueTask<string> ExportRecoveryPhraseAsync(CancellationToken cancellationToken = default)
    {
        // The phone first. Security is its lock, not anything in the service, so nothing is asked for until the
        // person holding the phone has proved they own it.
        var said = await _owner.ConfirmAsync(Reason, cancellationToken).ConfigureAwait(false);
        if (said != OwnerCheck.Confirmed)
            throw new OwnerNotConfirmedException(said);

        try
        {
            return await _service.GetRecoveryPhraseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AetherNodeException ex) when (ex.Code == AetherNodeErrorCode.NodeUnavailable)
        {
            throw new NodeIdentityUnavailableException(ex.Message, ex);
        }
        catch (AetherNodeException ex) when (ex.Code == AetherNodeErrorCode.IdentityAbsent)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    public ValueTask<AetherNetTag> RestoreFromPhraseAsync(string recoveryPhrase, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(RestoreNotHere);
}

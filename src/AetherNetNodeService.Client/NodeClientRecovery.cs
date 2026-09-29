// SPDX-License-Identifier: MIT

using AetherNet.Identity;

namespace AetherNetNodeService.Client;

/// <summary>
/// Identity backup and restore, as a connected app sees them: not available here. The recovery phrase and the
/// seed never cross the node boundary (see <see cref="IAetherNodeClient"/>) — an app that could read them would
/// hold the identity, which is exactly what connecting to a node exists to prevent. Every call says so plainly
/// rather than pretending.
/// </summary>
/// <remarks>
/// <see cref="NotSupportedException"/>, not the contract's <see cref="InvalidOperationException"/>: that one
/// means "this device has no identity yet", and a backup screen showed exactly that — on a phone whose identity
/// was right there in AetherNetService. Not here is a different answer from not at all.
/// </remarks>
public sealed class NodeClientRecovery : INodeIdentityRecovery
{
    internal const string NotHere =
        "Identity backup lives with the node, not in this app — the recovery phrase never leaves AetherNetService.";

    public ValueTask<AetherNetTag> AdoptSeedAsync(byte[] seed, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(NotHere);

    public ValueTask<string> ExportRecoveryPhraseAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException(NotHere);

    public ValueTask<AetherNetTag> RestoreFromPhraseAsync(string recoveryPhrase, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(NotHere);
}

// SPDX-License-Identifier: MIT

namespace AetherNet.Node.Client;

/// <summary>
/// Tries several package sources in order and returns the first that yields a verified package — e.g.
/// distribution first, then a Wi-Fi Direct peer, then the mesh through a gateway. Each candidate still
/// verifies its own bytes; this only decides which to ask, and in what order.
/// </summary>
public sealed class FirstAvailableNodePackageSource : INodePackageSource
{
    private readonly IReadOnlyList<INodePackageSource> _sources;

    public FirstAvailableNodePackageSource(params INodePackageSource[] sources)
        : this((IReadOnlyList<INodePackageSource>)sources)
    {
    }

    public FirstAvailableNodePackageSource(IReadOnlyList<INodePackageSource> sources)
    {
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        if (_sources.Count == 0)
        {
            throw new ArgumentException("at least one package source is required", nameof(sources));
        }
    }

    /// <inheritdoc />
    public string Name => "first-available";

    /// <inheritdoc />
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        foreach (var source in _sources)
        {
            if (await source.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }
        return false;
    }

    /// <inheritdoc />
    public async Task<byte[]> FetchAsync(string expectedFingerprint, CancellationToken cancellationToken = default)
    {
        NodePackageException? last = null;
        foreach (var source in _sources)
        {
            if (!await source.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
            {
                continue;
            }
            try
            {
                return await source.FetchAsync(expectedFingerprint, cancellationToken).ConfigureAwait(false);
            }
            catch (NodePackageException ex)
            {
                last = ex;
            }
        }
        throw new NodePackageException("no package source could supply a verified node package", last);
    }
}

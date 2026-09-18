// SPDX-License-Identifier: MIT

namespace AetherNet.Node.Client;

/// <summary>
/// One way to obtain the node APK, verified. "Give me the bytes for this fingerprint." Every source checks
/// the bytes against the advertised SHA-256 (<see cref="NodePackageFingerprint"/>) <b>before</b> returning
/// them, so no caller ever installs an unverified package regardless of where it came from — a distribution
/// endpoint, a peer over Wi-Fi Direct, or the mesh through a gateway.
/// </summary>
public interface INodePackageSource
{
    /// <summary>A short label for diagnostics and ordering, e.g. "distribution", "peer", "mesh".</summary>
    string Name { get; }

    /// <summary>Whether this source can be tried right now (the internet is reachable, a peer is linked, …).</summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>Fetch the node APK and return it only if it matches <paramref name="expectedFingerprint"/>.</summary>
    /// <exception cref="NodePackageException">The source was unavailable, the fetch failed, or the bytes did not match.</exception>
    Task<byte[]> FetchAsync(string expectedFingerprint, CancellationToken cancellationToken = default);
}

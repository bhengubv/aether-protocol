// SPDX-License-Identifier: MIT

namespace AetherNetNodeService.Client;

/// <summary>What a store has of AetherNetService: its newest release, and where to download it.</summary>
/// <param name="VersionName">As people see it, e.g. "1.2".</param>
/// <param name="VersionCode">The number Android compares.</param>
/// <param name="SizeBytes">How big the download is, as the store says — 0 when it does not say.</param>
/// <param name="Download">Where the package comes from.</param>
public sealed record NodePackageOffer(string VersionName, int VersionCode, long SizeBytes, Uri Download);

/// <summary>
/// A store AetherNetService can be downloaded from — SleptOn, The Geek Network's own.
/// </summary>
/// <remarks>
/// Unlike an <see cref="INodePackageSource"/>, which serves bytes whose SHA-256 is known ahead, a store's newest
/// release is not known ahead. What proves a download is the publisher's key, so nothing from a store is installed
/// until an <see cref="INodePackageVerifier"/> has said it is AetherNetService, signed like the app asking.
/// </remarks>
public interface INodePackageStore
{
    /// <summary>The store's name, as the person knows it.</summary>
    string Name { get; }

    /// <summary>The newest release the store has, or null when it has none.</summary>
    /// <exception cref="NodePackageException">The store could not be reached, or answered nonsense.</exception>
    Task<NodePackageOffer?> FindAsync(CancellationToken cancellationToken = default);

    /// <summary>Download the package, reporting the bytes received so far.</summary>
    /// <exception cref="NodePackageException">The download failed, came back empty, or was far too large.</exception>
    Task<byte[]> DownloadAsync(NodePackageOffer offer, IProgress<long>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>Whether a downloaded package is the real AetherNetService.</summary>
public interface INodePackageVerifier
{
    /// <summary>
    /// True only when the bytes are AetherNetService's package, signed by the same key as the app asking — the one
    /// thing a store cannot fake, whatever it serves.
    /// </summary>
    NodePackageVerdict Verify(byte[] package);
}

/// <summary>A verifier's answer, and why when it is no.</summary>
public sealed record NodePackageVerdict(bool Genuine, string? Why = null)
{
    /// <summary>It is the real one.</summary>
    public static NodePackageVerdict Yes { get; } = new(true);

    /// <summary>It is not, and why — in words for the person.</summary>
    public static NodePackageVerdict No(string why) => new(false, why);
}

// SPDX-License-Identifier: MIT

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// Giving somebody the app, with no app store in the middle.
///
/// <para>
/// A mesh that needs a store to spread is not a mesh — it has a single point of failure sitting in
/// front of the very first step, and that point is somebody else's business. The places this network
/// is most worth having are the places with no data to reach a store with, and the moments it is most
/// worth having are the ones where the store is unreachable for everybody at once.
/// </para>
///
/// <para>
/// So the app carries itself. Every installed copy holds its own APK — that is simply where Android
/// keeps it — and can hand it to a phone beside it over the same radio it would use for anything else.
/// One package: what arrives is the same app that sent it, not a downloader that then needs a network
/// to fetch the real thing.
/// </para>
/// </summary>
public interface IAppShareService
{
    /// <summary>Whether this platform can hand its own installer to another phone.</summary>
    bool IsSupported { get; }

    /// <summary>Why not, in words someone holding the phone can act on — or null when it can.</summary>
    string? UnavailableReason => null;

    /// <summary>How big the share is, so a person can be told before it starts rather than after.</summary>
    long SizeBytes { get; }

    /// <summary>
    /// The app's own installer, ready to send.
    /// </summary>
    /// <remarks>
    /// Read fresh rather than cached: it is tens of megabytes, and holding it in memory for a thing
    /// that happens rarely is a poor trade on a phone with 3 GB in it.
    /// </remarks>
    Task<byte[]?> ReadInstallerAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Hand the installer to a phone that has just received it, so the person can install it.
    /// </summary>
    /// <remarks>
    /// The receiving side ends at the system installer, which asks the person. That prompt is not an
    /// obstacle to route around — it is the one moment somebody chooses to trust what a phone nearby
    /// just handed them, and an app that arranged to skip it would be malware with good manners.
    /// </remarks>
    Task<bool> OfferToInstallAsync(byte[] installer, CancellationToken cancellationToken = default);
}

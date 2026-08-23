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
    /// The same installer, as a stream, for handing to something that takes it a piece at a time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Serving the app to a phone that has nothing means pushing tens of megabytes down a socket that
    /// accepts a few kilobytes at a time. Reading the whole package into an array first is the kind of
    /// allocation that gets an app killed on a 3 GB handset, and it would be killed precisely during
    /// the one moment the feature had to work.
    /// </para>
    /// <para>
    /// The default reads it whole, so nothing has to implement this to be correct — but a platform
    /// that knows where the file is should open it instead.
    /// </para>
    /// </remarks>
    async Task<Stream?> OpenInstallerAsync(CancellationToken cancellationToken = default)
    {
        var bytes = await ReadInstallerAsync(cancellationToken).ConfigureAwait(false);
        return bytes is null ? null : new MemoryStream(bytes, writable: false);
    }

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

/// <summary>
/// For heads that are not an installable app — the web head, and any desktop.
/// </summary>
/// <remarks>
/// A server has no APK to hand anybody and no phone to hand it to. It says so rather than offering a
/// button that fails, which is the same rule the radios follow.
/// </remarks>
public sealed class NoAppShare : IAppShareService
{
    public bool IsSupported => false;
    public string? UnavailableReason => "the app can only be handed over from a phone";
    public long SizeBytes => 0;
    public Task<byte[]?> ReadInstallerAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<byte[]?>(null);
    public Task<bool> OfferToInstallAsync(byte[] installer, CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}

// SPDX-License-Identifier: MIT
#if ANDROID
using AetherNet.Sample.Shared.Services;
using Android.Content;
using Microsoft.Extensions.Logging;

namespace AetherNet.Sample.Platforms.Android;

/// <summary>
/// Android's answer to <see cref="IAppShareService"/>: hand over the APK this app is running from.
///
/// <para>
/// Android keeps every installed app's package on disk and tells you where — <c>ApplicationInfo
/// .SourceDir</c>. So a phone already holds everything needed to give the app to the phone next to it,
/// and has done since the moment it was installed. Nothing is downloaded, nothing is rebuilt, and
/// what arrives is byte-for-byte the app that sent it.
/// </para>
/// </summary>
public sealed class AndroidAppShareService : IAppShareService
{
    /// <summary>Matches the provider declared in the manifest. Both must agree or the handover fails.</summary>
    private const string Authority = "com.bhengubv.aethernet.share";

    private readonly ILogger<AndroidAppShareService> _logger;

    public AndroidAppShareService(ILogger<AndroidAppShareService>? logger = null) =>
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AndroidAppShareService>.Instance;

    private static Context Ctx => global::Android.App.Application.Context;

    /// <summary>Where Android put this app's own package.</summary>
    private static string? InstallerPath => Ctx.ApplicationInfo?.SourceDir;

    public bool IsSupported => InstallerPath is { Length: > 0 } path && File.Exists(path);

    /// <inheritdoc />
    /// <remarks>Asked of the running package rather than written down, so a rename cannot go stale.</remarks>
    public string PackageName => Ctx.PackageName ?? "com.bhengubv.aethernet";

    /// <inheritdoc />
    public string? UnavailableReason => IsSupported ? null : "this phone will not say where the app lives";

    public long SizeBytes
    {
        get
        {
            try { return InstallerPath is { } path && File.Exists(path) ? new FileInfo(path).Length : 0; }
            catch (IOException) { return 0; }
        }
    }

    public async Task<byte[]?> ReadInstallerAsync(CancellationToken cancellationToken = default)
    {
        if (InstallerPath is not { Length: > 0 } path || !File.Exists(path)) return null;

        try
        {
            return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "[Share] Could not read this app's own installer");
            return null;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Android already keeps the package on disk, so this opens the file the app is running from
    /// rather than copying a hundred megabytes into the heap to hand it to a socket.
    /// </remarks>
    public Task<Stream?> OpenInstallerAsync(CancellationToken cancellationToken = default)
    {
        if (InstallerPath is not { Length: > 0 } path || !File.Exists(path))
            return Task.FromResult<Stream?>(null);

        try
        {
            return Task.FromResult<Stream?>(
                new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 64 * 1024, useAsync: true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "[Share] Could not open this app's own installer");
            return Task.FromResult<Stream?>(null);
        }
    }

    /// <summary>
    /// Write what arrived somewhere the system installer can read it, then ask the system to install.
    /// </summary>
    /// <remarks>
    /// It ends at Android's own installer prompt, deliberately. Handing somebody an app is exactly the
    /// moment they should be asked whether they want it, and that prompt is the only thing standing
    /// between "a friend shared the app" and "something a phone nearby put on my device".
    /// </remarks>
    public async Task<bool> OfferToInstallAsync(byte[] installer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installer);
        if (installer.Length == 0) return false;

        try
        {
            // Cache, not documents: this is a copy of something the sender already has, and once the
            // installer has read it there is no reason for it to survive.
            var incoming = Path.Combine(Ctx.CacheDir!.AbsolutePath, "shared");
            Directory.CreateDirectory(incoming);
            var file = Path.Combine(incoming, "aether.apk");
            await File.WriteAllBytesAsync(file, installer, cancellationToken).ConfigureAwait(false);

            // A file:// URI has been refused since Android 7; it has to be handed over as content://
            // with read permission granted to whoever we are handing it to.
            var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(Ctx, Authority, new Java.IO.File(file));

            var intent = new Intent(Intent.ActionView);
            intent.SetDataAndType(uri, "application/vnd.android.package-archive");
            intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.GrantReadUriPermission);
            Ctx.StartActivity(intent);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Share] Could not offer the installer");
            return false;
        }
    }
}
#endif

// SPDX-License-Identifier: MIT
#if ANDROID
using System.Security.Cryptography;
using Android.Content;
using Android.Content.PM;
using AetherNetNodeService.Client;

namespace AetherNetNodeService.Android;

/// <summary>
/// The Android <see cref="INodePackageVerifier"/>: a download is AetherNetService only if it is AetherNetService's
/// package and is signed by the same key as the app asking.
/// </summary>
/// <remarks>
/// The key is the one thing a store cannot fake, whatever it serves; a release's SHA-256 is not known ahead, the key
/// is. Asking "signed like me?" pins nothing in the code: a Release Aether accepts only a Release AetherNetService
/// signed with The Geek Network's key, and a Debug build only one signed with the same debug key.
/// </remarks>
public sealed class AndroidNodePackageVerifier : INodePackageVerifier
{
    private readonly Context _context;
    private readonly string _servicePackage;

    /// <param name="context">An application context — the app whose key the download must share.</param>
    /// <param name="servicePackage">AetherNetService's package name.</param>
    public AndroidNodePackageVerifier(Context context, string servicePackage)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _servicePackage = string.IsNullOrWhiteSpace(servicePackage)
            ? throw new ArgumentException("AetherNetService's package name is needed.", nameof(servicePackage))
            : servicePackage;
    }

    /// <inheritdoc />
    public NodePackageVerdict Verify(byte[] package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (package.Length == 0) return NodePackageVerdict.No("it is empty");

        // The phone reads a package from a file, so it is written to this app's own cache for the look and removed.
        var dir = Path.Combine(_context.CacheDir!.AbsolutePath, "aethernetservice-check");
        var file = Path.Combine(dir, "candidate.apk");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(file, package);

            var pm = _context.PackageManager!;
            var candidate = pm.GetPackageArchiveInfo(file, 0);
            if (candidate is null) return NodePackageVerdict.No("it is not an Android app");
            if (!string.Equals(candidate.PackageName, _servicePackage, StringComparison.Ordinal))
                return NodePackageVerdict.No($"it is {candidate.PackageName}, not {_servicePackage}");

            var theirs = SignersOf(flags => pm.GetPackageArchiveInfo(file, flags));
            if (theirs.Count == 0) return NodePackageVerdict.No("it is not signed");

            var ours = SignersOf(flags => pm.GetPackageInfo(_context.PackageName!, flags));
            return theirs.SetEquals(ours) ? NodePackageVerdict.Yes : NodePackageVerdict.No("it is signed by someone else");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Java.Lang.Exception)
        {
            return NodePackageVerdict.No($"it could not be read ({ex.Message})");
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { /* the cache clears itself */ }
        }
    }

#pragma warning disable CA1422, CS0618 // the int-flag forms are the ones that exist on every Android this supports
    /// <summary>The SHA-256 of each certificate that signs a package, read with whichever flag this Android fills in.</summary>
    /// <remarks>
    /// Android 9 and 10 leave a package FILE's signing certificates unread when asked the newer way — the P30 called
    /// the real AetherNetService "not signed" (2026-10-03). The older flag still reads them, so it is the fallback.
    /// </remarks>
    private static HashSet<string> SignersOf(Func<PackageInfoFlags, PackageInfo?> read)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(28))
        {
            var newer = Hashes(read(PackageInfoFlags.SigningCertificates)?.SigningInfo?.GetApkContentsSigners());
            if (newer.Count > 0) return newer;
        }

        return Hashes(read(PackageInfoFlags.Signatures)?.Signatures);
    }

    private static HashSet<string> Hashes(IEnumerable<Signature>? signatures)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var signature in signatures ?? [])
        {
            if (signature.ToByteArray() is { Length: > 0 } certificate)
                set.Add(Convert.ToHexString(SHA256.HashData(certificate)));
        }
        return set;
    }
#pragma warning restore CA1422, CS0618
}
#endif

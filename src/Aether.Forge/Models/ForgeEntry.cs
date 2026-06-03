// SPDX-License-Identifier: MIT
namespace Aether.Forge.Models;

/// <summary>
/// Metadata record for one cached package artifact in the Forge mesh cache.
///
/// Package IDs use a namespaced format:
/// <list type="bullet">
///   <item><c>npm:react@18.2.0</c></item>
///   <item><c>git:github.com/org/repo@abc123</c></item>
///   <item><c>pip:requests@2.31.0</c></item>
///   <item><c>cargo:serde@1.0.195</c></item>
///   <item><c>go:golang.org/x/net@v0.21.0</c></item>
///   <item><c>nuget:Newtonsoft.Json@13.0.3</c></item>
/// </list>
/// </summary>
public sealed class ForgeEntry
{
    /// <summary>Aether content hash of the cached artifact (opaque bytes).</summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>Package identifier in <c>ecosystem:name@version</c> format.</summary>
    public string PackageId { get; set; } = string.Empty;

    /// <summary>UTC timestamp when the artifact was first fetched and cached.</summary>
    public DateTime FetchedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Size of the cached artifact in bytes.</summary>
    public long SizeBytes { get; set; }

    /// <summary>
    /// Number of times this artifact has been served from the mesh cache
    /// (incremented on each <c>IForgeService.FetchAsync</c> call that returns data).
    /// </summary>
    public int DownloadCount { get; set; }
}

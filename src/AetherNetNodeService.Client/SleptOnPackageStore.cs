// SPDX-License-Identifier: MIT

using System.Text.Json;

namespace AetherNetNodeService.Client;

/// <summary>
/// AetherNetService from SleptOn, The Geek Network's own store — found by its package name, so no store id has to be
/// known ahead, and downloaded the way SleptOn hands any app out.
/// </summary>
/// <remarks>
/// <para>
/// Two calls, both open to anyone: <c>api/updates/check/{package}?currentVersionCode=0</c> answers with the newest
/// approved release, and <c>api/appstore/download/{release}</c> redirects to a short-lived signed link to the package.
/// A release is only there once the app is published and the release has passed SleptOn's scan and review.
/// </para>
/// <para>
/// The download link is built here from the release id rather than taken as given: the lookup answers with
/// <c>/api/releases/{id}/download</c>, which SleptOn does not serve (read from its code, 2026-10-02). If that is
/// fixed, this still works.
/// </para>
/// </remarks>
public sealed class SleptOnPackageStore : INodePackageStore
{
    /// <summary>SleptOn's API.</summary>
    public static readonly Uri DefaultApi = new("https://api.slepton.co.za/");

    /// <summary>The most taken. AetherNetService is tens of megabytes; a download near this is not it.</summary>
    public const long MostBytes = 256L * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly string _package;
    private readonly Uri _api;

    /// <param name="http">The client to call SleptOn with.</param>
    /// <param name="packageName">The package wanted — AetherNetService's.</param>
    /// <param name="api">SleptOn's API; <see cref="DefaultApi"/> unless a test points it elsewhere.</param>
    public SleptOnPackageStore(HttpClient http, string packageName, Uri? api = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _package = string.IsNullOrWhiteSpace(packageName)
            ? throw new ArgumentException("The package wanted is needed.", nameof(packageName))
            : packageName;
        var root = api ?? DefaultApi;
        _api = root.AbsoluteUri.EndsWith('/') ? root : new Uri(root.AbsoluteUri + "/");
    }

    /// <inheritdoc />
    public string Name => "SleptOn";

    /// <inheritdoc />
    public async Task<NodePackageOffer?> FindAsync(CancellationToken cancellationToken = default)
    {
        var lookup = new Uri(_api, $"api/updates/check/{Uri.EscapeDataString(_package)}?currentVersionCode=0");
        try
        {
            using var response = await _http.GetAsync(lookup, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new NodePackageException($"SleptOn answered {(int)response.StatusCode} when asked for AetherNetService");

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var answer = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
            return Read(answer.RootElement);
        }
        catch (HttpRequestException ex)
        {
            throw new NodePackageException("SleptOn could not be reached", ex);
        }
        catch (JsonException ex)
        {
            throw new NodePackageException("SleptOn's answer could not be read", ex);
        }
    }

    /// <summary>The offer in SleptOn's answer, or null when it has no release of the package.</summary>
    private NodePackageOffer? Read(JsonElement answer)
    {
        if (!answer.TryGetProperty("updateAvailable", out var available) || available.ValueKind != JsonValueKind.True)
            return null;

        if (!answer.TryGetProperty("latestVersion", out var latest) || latest.ValueKind != JsonValueKind.Object)
            throw new NodePackageException("SleptOn named a release but not its version");

        var release = answer.TryGetProperty("downloadUrl", out var link) && link.ValueKind == JsonValueKind.String
            ? ReleaseIn(link.GetString())
            : null;
        if (release is null)
            throw new NodePackageException("SleptOn named a release but not where to download it");

        var name = latest.TryGetProperty("versionName", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "";
        var code = latest.TryGetProperty("versionCode", out var c) && c.TryGetInt32(out var number) ? number : 0;
        var size = latest.TryGetProperty("binarySize", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;
        return new NodePackageOffer(name, code, size, new Uri(_api, $"api/appstore/download/{release}"));
    }

    /// <summary>The release id inside SleptOn's download link, whatever path it is written with.</summary>
    private static Guid? ReleaseIn(string? link)
    {
        foreach (var part in (link ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Guid.TryParse(part, out var id)) return id;
        }
        return null;
    }

    /// <inheritdoc />
    public async Task<byte[]> DownloadAsync(NodePackageOffer offer, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(offer);
        try
        {
            using var response = await _http.GetAsync(offer.Download, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new NodePackageException($"SleptOn would not hand over AetherNetService (it answered {(int)response.StatusCode})");
            if (response.Content.Headers.ContentLength is > MostBytes)
                throw new NodePackageException("the download is far larger than AetherNetService");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var kept = new MemoryStream(offer.SizeBytes is > 0 and < MostBytes ? (int)offer.SizeBytes : 0);
            var chunk = new byte[81920];
            long total = 0;
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > MostBytes) throw new NodePackageException("the download is far larger than AetherNetService");
                kept.Write(chunk, 0, read);
                progress?.Report(total);
            }

            if (total == 0) throw new NodePackageException("SleptOn sent nothing");
            return kept.ToArray();
        }
        catch (HttpRequestException ex)
        {
            throw new NodePackageException("the download from SleptOn broke off", ex);
        }
    }
}

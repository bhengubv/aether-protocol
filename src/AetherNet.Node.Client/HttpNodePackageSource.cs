// SPDX-License-Identifier: MIT

namespace AetherNet.Node.Client;

/// <summary>
/// Fetches the node APK from a distribution endpoint over HTTP, then verifies the fingerprint. This is the
/// in-app downloader for the "reachable distribution" path — the endpoint is configuration (e.g. the
/// reference deployment's <c>nfc.circleos.co.za</c>), never hard-coded in the SDK.
/// </summary>
public sealed class HttpNodePackageSource : INodePackageSource
{
    private readonly HttpClient _http;
    private readonly Uri _packageUri;

    public HttpNodePackageSource(HttpClient http, Uri packageUri)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _packageUri = packageUri ?? throw new ArgumentNullException(nameof(packageUri));
    }

    /// <inheritdoc />
    public string Name => "distribution";

    /// <inheritdoc />
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, _packageUri);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<byte[]> FetchAsync(string expectedFingerprint, CancellationToken cancellationToken = default)
    {
        byte[] bytes;
        try
        {
            bytes = await _http.GetByteArrayAsync(_packageUri, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new NodePackageException($"could not fetch the node package from {_packageUri}", ex);
        }

        if (!NodePackageFingerprint.Verify(bytes, expectedFingerprint))
        {
            throw new NodePackageException(
                $"the package fetched from {_packageUri} did not match the expected fingerprint");
        }
        return bytes;
    }
}

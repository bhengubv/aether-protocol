// SPDX-License-Identifier: MIT

using System.Net;
using Xunit;

namespace AetherNet.Node.Client.Tests;

public class HttpNodePackageSourceTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly byte[] _body;
        private readonly HttpStatusCode _status;

        public StubHandler(byte[] body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(_status) { Content = new ByteArrayContent(_body) });
    }

    private static HttpNodePackageSource Source(byte[] body, HttpStatusCode status = HttpStatusCode.OK)
        => new(new HttpClient(new StubHandler(body, status)), new Uri("https://example.test/node.apk"));

    [Fact]
    public async Task Fetch_returns_bytes_that_match_the_fingerprint()
    {
        var apk = new byte[] { 10, 20, 30, 40 };
        var source = Source(apk);

        var fetched = await source.FetchAsync(NodePackageFingerprint.Compute(apk));

        Assert.Equal(apk, fetched);
    }

    [Fact]
    public async Task Fetch_refuses_bytes_that_do_not_match_the_fingerprint()
    {
        var source = Source(new byte[] { 1, 2, 3 });

        await Assert.ThrowsAsync<NodePackageException>(
            () => source.FetchAsync(NodePackageFingerprint.Compute(new byte[] { 4, 5, 6 })));
    }

    [Fact]
    public async Task Is_available_reflects_the_status_code()
    {
        Assert.True(await Source(new byte[] { 1 }).IsAvailableAsync());
        Assert.False(await Source(new byte[] { 1 }, HttpStatusCode.NotFound).IsAvailableAsync());
    }
}

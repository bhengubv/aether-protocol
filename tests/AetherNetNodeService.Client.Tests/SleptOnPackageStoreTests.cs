// SPDX-License-Identifier: MIT

using System.Net;
using System.Text;
using Xunit;

namespace AetherNetNodeService.Client.Tests;

/// <summary>
/// AetherNetService from SleptOn: found by package name, downloaded through the route SleptOn actually serves.
/// The answers below are the shape SleptOn's own code writes (UpdatesController.CheckForUpdate, 2026-10-02).
/// </summary>
public class SleptOnPackageStoreTests
{
    private const string Package = "com.bhengubv.aethernetservice";
    private const string Release = "3f2b6c1e-8d4a-4c7e-9b1f-2a6d5e4c3b21";
    private static readonly Uri Api = new("https://api.slepton.test/");

    private static string Found(long size = 34_000_000) =>
        "{\"updateAvailable\":true,\"currentVersion\":0," +
        "\"latestVersion\":{\"versionCode\":3,\"versionName\":\"1.2\",\"releaseNotes\":null,\"binarySize\":" + size + "}," +
        "\"urgencyLevel\":\"recommended\",\"gracePeriodHours\":null,\"message\":null," +
        "\"downloadUrl\":\"/api/releases/" + Release + "/download\"}";

    /// <summary>Answers by path; records what was asked.</summary>
    private sealed class FakeSleptOn(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<Uri> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked.Add(request.RequestUri!);
            return Task.FromResult(answer(request));
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (SleptOnPackageStore Store, FakeSleptOn Server) Store(Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        var server = new FakeSleptOn(answer);
        return (new SleptOnPackageStore(new HttpClient(server), Package, Api), server);
    }

    [Fact]
    public async Task It_finds_the_newest_release_by_package_name()
    {
        var (store, server) = Store(_ => Json(Found()));

        var offer = await store.FindAsync();

        Assert.NotNull(offer);
        Assert.Equal("1.2", offer.VersionName);
        Assert.Equal(3, offer.VersionCode);
        Assert.Equal(34_000_000, offer.SizeBytes);
        Assert.Equal($"https://api.slepton.test/api/updates/check/{Package}?currentVersionCode=0&platform=android", Assert.Single(server.Asked).AbsoluteUri);
    }

    /// <summary>SleptOn keeps one release per platform under one package name; a computer asks for its own build.</summary>
    [Fact]
    public async Task A_computer_asks_for_the_Windows_build()
    {
        var server = new FakeSleptOn(_ => Json(Found()));
        var store = new SleptOnPackageStore(new HttpClient(server), Package, Api, platform: "windows");

        await store.FindAsync();

        Assert.EndsWith("&platform=windows", Assert.Single(server.Asked).AbsoluteUri);
    }

    /// <summary>
    /// SleptOn's answer names <c>/api/releases/{id}/download</c>, which it does not serve; the download goes to the
    /// route it does.
    /// </summary>
    [Fact]
    public async Task It_downloads_through_the_route_SleptOn_actually_serves()
    {
        var (store, _) = Store(_ => Json(Found()));

        var offer = await store.FindAsync();

        Assert.Equal($"https://api.slepton.test/api/appstore/download/{Release}", offer!.Download.AbsoluteUri);
    }

    [Fact]
    public async Task Nothing_published_is_no_offer()
    {
        var (store, _) = Store(_ => Json("{\"updateAvailable\":false,\"message\":\"App not found or no approved releases\"}"));

        Assert.Null(await store.FindAsync());
    }

    [Fact]
    public async Task A_store_that_cannot_be_reached_says_so()
    {
        var (store, _) = Store(_ => throw new HttpRequestException("no route to host"));

        var problem = await Assert.ThrowsAsync<NodePackageException>(() => store.FindAsync());

        Assert.Equal("SleptOn could not be reached", problem.Message);
    }

    [Fact]
    public async Task A_refusal_or_nonsense_from_the_store_says_so()
    {
        var (refusing, _) = Store(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var (babbling, _) = Store(_ => Json("<html>maintenance</html>"));

        Assert.Contains("503", (await Assert.ThrowsAsync<NodePackageException>(() => refusing.FindAsync())).Message);
        Assert.Equal("SleptOn's answer could not be read", (await Assert.ThrowsAsync<NodePackageException>(() => babbling.FindAsync())).Message);
    }

    [Fact]
    public async Task The_download_arrives_whole_with_its_progress()
    {
        var package = new byte[200_000];
        Random.Shared.NextBytes(package);
        var (store, _) = Store(request => request.RequestUri!.AbsolutePath.StartsWith("/api/appstore/download/")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(package) }
            : Json(Found(package.Length)));
        var seen = new List<long>();

        var got = await store.DownloadAsync((await store.FindAsync())!, new Collect(seen));

        Assert.Equal(package, got);
        Assert.Equal(package.Length, seen[^1]);
    }

    [Fact]
    public async Task An_empty_download_is_refused()
    {
        var (store, _) = Store(request => request.RequestUri!.AbsolutePath.StartsWith("/api/appstore/download/")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) }
            : Json(Found()));

        var problem = await Assert.ThrowsAsync<NodePackageException>(async () => await store.DownloadAsync((await store.FindAsync())!));

        Assert.Equal("SleptOn sent nothing", problem.Message);
    }

    [Fact]
    public async Task A_download_SleptOn_will_not_give_says_so()
    {
        var (store, _) = Store(request => request.RequestUri!.AbsolutePath.StartsWith("/api/appstore/download/")
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : Json(Found()));

        var problem = await Assert.ThrowsAsync<NodePackageException>(async () => await store.DownloadAsync((await store.FindAsync())!));

        Assert.Contains("404", problem.Message);
    }

    private sealed class Collect(List<long> seen) : IProgress<long>
    {
        public void Report(long value) => seen.Add(value);
    }
}

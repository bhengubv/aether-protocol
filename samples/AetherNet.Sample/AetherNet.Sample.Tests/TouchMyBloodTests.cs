// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Services;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// Touch My Blood — handing the app to a phone that has nothing on it.
///
/// <para>
/// Every piece of this is fed by somebody who does not have our software. The tap is read by a stock
/// handset's NFC stack, the address is fetched by a stock browser, and the server answering is open
/// on a network anybody can be sitting on. So none of it can assume a well-behaved caller, and all of
/// it has to be exactly right the first time — the moment it runs is a person standing in front of a
/// friend, and there is no second attempt that does not feel like a broken promise.
/// </para>
/// </summary>
public class TouchMyBloodTests
{
    // ── The address that crosses ─────────────────────────────────────────────

    [Fact]
    public void An_invite_survives_the_round_trip()
    {
        var token = ShareInvite.NewToken();
        var url = ShareInvite.Compose("192.168.0.115", 41234, token);

        Assert.True(ShareInvite.TryParse(url, out var host, out var port, out var read));
        Assert.Equal("192.168.0.115", host);
        Assert.Equal(41234, port);
        Assert.Equal(token, read);
    }

    [Fact]
    public void An_invite_ends_in_something_android_will_offer_to_install()
    {
        // The thing fetching this is a browser, and a browser names a download after the last segment
        // of the path. A file with no .apk on the end is one Android will not open.
        Assert.EndsWith("/aether.apk", ShareInvite.Compose("10.0.0.2", 8080, ShareInvite.NewToken()),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Every_invite_gets_its_own_secret()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 200; i++) Assert.True(seen.Add(ShareInvite.NewToken()));
        Assert.All(seen, t => Assert.Equal(ShareInvite.TokenLength, t.Length));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://host:8080/tmb/abc/aether.apk")]
    [InlineData("https://host:8080/tmb/00112233445566778899aabbccddeeff/aether.apk")]  // wrong scheme
    [InlineData("http://host/tmb/00112233445566778899aabbccddeeff/aether.apk")]        // no port
    [InlineData("http://host:8080/other/00112233445566778899aabbccddeeff/aether.apk")] // wrong path
    [InlineData("http://host:8080/tmb/short/aether.apk")]                              // token too short
    [InlineData("http://host:8080/tmb/00112233445566778899aabbccddeegg/aether.apk")]   // not hex
    [InlineData("http://host:8080/tmb/00112233445566778899aabbccddeeff/other.apk")]    // wrong file
    [InlineData("http://host:8080/tmb/00112233445566778899aabbccddeeff")]              // no file
    public void Anything_that_is_not_an_invite_is_refused(string? url)
        => Assert.False(ShareInvite.TryParse(url, out _, out _, out _));

    [Fact]
    public void A_token_is_compared_whole_or_not_at_all()
    {
        var token = ShareInvite.NewToken();

        Assert.True(ShareInvite.PathCarries($"/tmb/{token}/aether.apk", token));
        Assert.False(ShareInvite.PathCarries($"/tmb/{token[..^1]}/aether.apk", token));   // one short
        Assert.False(ShareInvite.PathCarries($"/tmb/{token}0/aether.apk", token));        // one long
        Assert.False(ShareInvite.PathCarries("/tmb//aether.apk", token));
        Assert.False(ShareInvite.PathCarries("/../etc/passwd", token));
        Assert.False(ShareInvite.PathCarries(null, token));
    }

    // ── The bytes that cross on the tap ──────────────────────────────────────

    [Fact]
    public void A_uri_record_survives_the_round_trip()
    {
        const string url = "http://192.168.0.115:41234/tmb/00112233445566778899aabbccddeeff/aether.apk";
        Assert.Equal(url, Ndef.ReadUri(Ndef.Uri(url)));
    }

    [Fact]
    public void The_common_prefix_is_abbreviated_to_one_byte()
    {
        var message = Ndef.Uri("http://example.com");

        // Header, type length, payload length, 'U', then the abbreviation code.
        Assert.Equal(0x55, message[3]);
        Assert.Equal(0x03, message[4]);                       // 0x03 == "http://"
        Assert.DoesNotContain("http", Encoding.ASCII.GetString(message), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://www.example.com", (byte)0x02)]
    [InlineData("http://www.example.com", (byte)0x01)]
    [InlineData("https://example.com", (byte)0x04)]
    [InlineData("http://example.com", (byte)0x03)]
    public void The_longest_prefix_wins(string url, byte expected)
    {
        // "https://www.x" encoded as "https://" plus a literal "www.x" still parses and still points
        // at the right place — but only the longest match round-trips byte for byte, and a tag that
        // does not round-trip is one nobody can check without two phones.
        Assert.Equal(expected, Ndef.Uri(url)[4]);
        Assert.Equal(url, Ndef.ReadUri(Ndef.Uri(url)));
    }

    [Fact]
    public void A_uri_with_no_known_prefix_still_travels()
    {
        const string odd = "aether://KXJB7-MN2P4";
        Assert.Equal(0x00, Ndef.Uri(odd)[4]);
        Assert.Equal(odd, Ndef.ReadUri(Ndef.Uri(odd)));
    }

    [Fact]
    public void A_tap_carries_the_address_and_the_person()
    {
        const string url = "http://10.0.0.2:8080/tmb/00112233445566778899aabbccddeeff/aether.apk";
        var message = Ndef.UriAndTag(url, "KXJB7-MN2P4");

        // A phone with no Aether reads the first record and opens the address; a phone that has it
        // reads the second and knows who tapped.
        Assert.Equal(url, Ndef.ReadUri(message));
        Assert.Contains("KXJB7-MN2P4", Encoding.ASCII.GetString(message), StringComparison.Ordinal);
        Assert.Contains(Ndef.TagRecordType, Encoding.ASCII.GetString(message), StringComparison.Ordinal);
    }

    [Fact]
    public void The_message_begins_once_and_ends_once()
    {
        // MB on the first record, ME on the last, and never both on the first when there are two.
        // Get this wrong and a reader stops after one record — or worse, keeps reading past the end.
        var one = Ndef.Uri("http://x");
        Assert.Equal(0x80, one[0] & 0x80);
        Assert.Equal(0x40, one[0] & 0x40);

        var two = Ndef.UriAndTag("http://x", "TAG");
        Assert.Equal(0x80, two[0] & 0x80);
        Assert.Equal(0x00, two[0] & 0x40);                   // the first is no longer the last
        Assert.Equal(0x40, two[^(3 + Ndef.TagRecordType.Length + 3)] & 0x40);
    }

    [Fact]
    public void A_tap_with_no_tag_is_still_a_valid_tap()
    {
        // A phone whose identity has not come up yet must still be able to hand over the app.
        const string url = "http://10.0.0.2:8080/tmb/00112233445566778899aabbccddeeff/aether.apk";
        Assert.Equal(url, Ndef.ReadUri(Ndef.UriAndTag(url, "")));
    }

    // ── The server that answers ──────────────────────────────────────────────

    private sealed class FakeApp(byte[] payload) : IAppShareService
    {
        public bool IsSupported => true;
        public long SizeBytes => payload.Length;
        public Task<byte[]?> ReadInstallerAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<byte[]?>(payload);
        public Task<bool> OfferToInstallAsync(byte[] installer, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }

    private sealed class NothingToShare : IAppShareService
    {
        public bool IsSupported => false;
        public long SizeBytes => 0;
        public Task<byte[]?> ReadInstallerAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<byte[]?>(null);
        public Task<bool> OfferToInstallAsync(byte[] installer, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }

    private static readonly byte[] Package = Encoding.ASCII.GetBytes("PKthis-is-the-apk");

    /// <summary>Fetch a path the way a browser would, and report what came back.</summary>
    private static async Task<(string Status, byte[] Body)> FetchAsync(int port, string path, string method = "GET")
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();

        var request = Encoding.ASCII.GetBytes($"{method} {path} HTTP/1.1\r\nHost: test\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(request);

        using var all = new MemoryStream();
        await stream.CopyToAsync(all);
        var raw = all.ToArray();

        var text = Encoding.ASCII.GetString(raw);
        var split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var status = (split < 0 ? text : text[..split]).Split("\r\n")[0];
        var body = split < 0 ? [] : raw[(split + 4)..];
        return (status, body);
    }

    private static int PortOf(string invite)
    {
        Assert.True(ShareInvite.TryParse(invite, out _, out var port, out _));
        return port;
    }

    [Fact]
    public async Task A_friend_who_was_handed_the_address_gets_the_app()
    {
        using var handout = new AppHandout(new FakeApp(Package));
        var invite = handout.Start("127.0.0.1");
        Assert.NotNull(invite);

        Assert.True(ShareInvite.TryParse(invite, out _, out var port, out var token));
        var (status, body) = await FetchAsync(port, $"{ShareInvite.Path}{token}/{ShareInvite.FileName}");

        Assert.Contains("200", status, StringComparison.Ordinal);
        Assert.Equal(Package, body);
    }

    [Fact]
    public async Task Somebody_else_on_the_network_gets_nothing()
    {
        using var handout = new AppHandout(new FakeApp(Package));
        var invite = handout.Start("127.0.0.1");
        Assert.NotNull(invite);
        var port = PortOf(invite);

        // The whole security of this is the token. A shared network means anyone can knock.
        foreach (var path in new[]
                 {
                     "/",
                     "/aether.apk",
                     $"{ShareInvite.Path}00000000000000000000000000000000/{ShareInvite.FileName}",
                     "/tmb/../../etc/passwd",
                 })
        {
            var (status, body) = await FetchAsync(port, path);
            Assert.Contains("404", status, StringComparison.Ordinal);
            Assert.Empty(body);
        }

        Assert.Equal(0, handout.Served);
    }

    [Fact]
    public async Task Garbage_on_the_socket_does_not_take_the_server_down()
    {
        using var handout = new AppHandout(new FakeApp(Package));
        var invite = handout.Start("127.0.0.1");
        Assert.NotNull(invite);
        var port = PortOf(invite);
        Assert.True(ShareInvite.TryParse(invite, out _, out _, out var token));

        // Somebody scanning the network, a port checker, a browser that gave up mid-sentence.
        using (var rude = new TcpClient())
        {
            await rude.ConnectAsync(IPAddress.Loopback, port);
            await rude.GetStream().WriteAsync(new byte[] { 0xFF, 0x00, 0xFF });
        }

        using (var silent = new TcpClient()) { await silent.ConnectAsync(IPAddress.Loopback, port); }

        // And the friend still gets the app.
        var (status, body) = await FetchAsync(port, $"{ShareInvite.Path}{token}/{ShareInvite.FileName}");
        Assert.Contains("200", status, StringComparison.Ordinal);
        Assert.Equal(Package, body);
    }

    [Fact]
    public async Task The_door_closes_after_the_third_friend()
    {
        using var handout = new AppHandout(new FakeApp(Package));
        var invite = handout.Start("127.0.0.1");
        Assert.NotNull(invite);
        var port = PortOf(invite);
        Assert.True(ShareInvite.TryParse(invite, out _, out _, out var token));

        var path = $"{ShareInvite.Path}{token}/{ShareInvite.FileName}";
        for (var i = 0; i < AppHandout.MaxHandovers; i++)
        {
            var (ok, body) = await FetchAsync(port, path);
            Assert.Contains("200", ok, StringComparison.Ordinal);
            Assert.Equal(Package, body);
        }

        // A token that never spends is a token that leaks.
        var (refused, _) = await FetchAsync(port, path);
        Assert.Contains("404", refused, StringComparison.Ordinal);
        Assert.Equal(AppHandout.MaxHandovers, handout.Served);
    }

    [Fact]
    public async Task Stopping_means_stopped()
    {
        using var handout = new AppHandout(new FakeApp(Package));
        var invite = handout.Start("127.0.0.1");
        Assert.NotNull(invite);
        var port = PortOf(invite);
        Assert.True(ShareInvite.TryParse(invite, out _, out _, out var token));

        handout.Stop();
        Assert.Null(handout.Invite);

        // Leaving the screen has to actually close the socket, not merely stop advertising it.
        await Assert.ThrowsAnyAsync<SocketException>(
            () => FetchAsync(port, $"{ShareInvite.Path}{token}/{ShareInvite.FileName}"));
    }

    [Fact]
    public async Task The_door_closes_on_time_even_if_nobody_came_through_it()
    {
        // A phone that was offered and then put in a pocket must stop serving its own installer to
        // the network. Nothing here presses Stop — the window running out is the whole test.
        using var handout = new AppHandout(new FakeApp(Package), TimeSpan.FromSeconds(2));
        var invite = handout.Start("127.0.0.1");
        Assert.NotNull(invite);
        var port = PortOf(invite);
        Assert.True(ShareInvite.TryParse(invite, out _, out _, out var token));

        var (open, _) = await FetchAsync(port, $"{ShareInvite.Path}{token}/{ShareInvite.FileName}");
        Assert.Contains("200", open, StringComparison.Ordinal);

        await Task.Delay(TimeSpan.FromSeconds(3.5));

        Assert.Null(handout.Invite);
        Assert.Equal(TimeSpan.Zero, handout.Remaining);
        await Assert.ThrowsAnyAsync<SocketException>(
            () => FetchAsync(port, $"{ShareInvite.Path}{token}/{ShareInvite.FileName}"));
    }

    [Fact]
    public async Task An_invite_that_expired_is_replaced_rather_than_resurrected()
    {
        using var handout = new AppHandout(new FakeApp(Package), TimeSpan.FromSeconds(1));
        var first = handout.Start("127.0.0.1");
        Assert.NotNull(first);
        Assert.True(ShareInvite.TryParse(first, out _, out _, out var firstToken));

        await Task.Delay(TimeSpan.FromSeconds(2.5));

        var second = handout.Start("127.0.0.1");
        Assert.NotNull(second);
        Assert.NotEqual(first, second);

        // The old secret must not open the new door. Pressing Start again is a new offer, not a
        // renewal of one that already lapsed.
        Assert.True(ShareInvite.TryParse(second, out _, out var port, out var secondToken));
        Assert.NotEqual(firstToken, secondToken);

        var (refused, _) = await FetchAsync(port, $"{ShareInvite.Path}{firstToken}/{ShareInvite.FileName}");
        Assert.Contains("404", refused, StringComparison.Ordinal);
    }

    [Fact]
    public void Asking_twice_hands_out_one_invite()
    {
        using var handout = new AppHandout(new FakeApp(Package));
        var first = handout.Start("127.0.0.1");
        var second = handout.Start("127.0.0.1");

        // Two live tokens for one phone is two things to expire, and one of them gets forgotten.
        Assert.Equal(first, second);
    }

    [Fact]
    public void A_device_with_nothing_to_share_offers_nothing()
    {
        using var handout = new AppHandout(new NothingToShare());
        Assert.Null(handout.Start("127.0.0.1"));
        Assert.Null(handout.Invite);
    }

    [Fact]
    public async Task A_head_that_cannot_stream_still_serves()
    {
        // OpenInstallerAsync has a default that reads the package whole. Anything implementing only
        // the byte-array half must still hand a friend a working app.
        var plain = new FakeApp(Package);
        await using var stream = await ((IAppShareService)plain).OpenInstallerAsync();
        Assert.NotNull(stream);

        using var read = new MemoryStream();
        await stream.CopyToAsync(read);
        Assert.Equal(Package, read.ToArray());
    }
}

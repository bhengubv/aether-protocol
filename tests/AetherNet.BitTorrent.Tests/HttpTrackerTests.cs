// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AetherNet.BitTorrent.Bencoding;
using AetherNet.BitTorrent.Trackers;
using Xunit;

namespace AetherNet.BitTorrent.Tests;

public class HttpTrackerTests
{
    private static byte[] Filled(int len, byte b)
    {
        var a = new byte[len];
        Array.Fill(a, b);
        return a;
    }

    private static AnnounceRequest SampleRequest() => new()
    {
        InfoHash = Filled(20, 0xAB),
        PeerId = Filled(20, 0xCD),
        Port = 6881,
        Left = 1000,
        Event = TrackerEvent.Started,
    };

    private static byte[] CannedAnnounce()
    {
        var peers = new byte[12];
        new byte[] { 1, 2, 3, 4 }.CopyTo(peers, 0);
        BinaryPrimitives.WriteUInt16BigEndian(peers.AsSpan(4), 6881);
        new byte[] { 10, 0, 0, 1 }.CopyTo(peers, 6);
        BinaryPrimitives.WriteUInt16BigEndian(peers.AsSpan(10), 51413);

        var d = new BencodeDictionary();
        d.Add("interval", new BencodeInteger(1800));
        d.Add("complete", new BencodeInteger(5));
        d.Add("incomplete", new BencodeInteger(3));
        d.Add("peers", new BencodeString(peers));
        return d.Encode();
    }

    [Fact]
    public void BuildAnnounceUrl_percent_encodes_infohash_byte_for_byte()
    {
        var uri = HttpTrackerClient.BuildAnnounceUrl(new Uri("http://tracker.example/announce"), SampleRequest());
        var url = uri.ToString();
        Assert.Contains("/announce?", url);
        Assert.Contains("info_hash=%AB%AB%AB%AB%AB%AB%AB%AB%AB%AB%AB%AB%AB%AB%AB%AB%AB%AB%AB%AB", url);
        Assert.Contains("peer_id=%CD", url);
        Assert.Contains("port=6881", url);
        Assert.Contains("left=1000", url);
        Assert.Contains("compact=1", url);
        Assert.Contains("event=started", url);
    }

    [Fact]
    public void ParseAnnounceResponse_reads_compact_peers()
    {
        var r = HttpTrackerClient.ParseAnnounceResponse(CannedAnnounce());
        Assert.Equal(1800, r.Interval);
        Assert.Equal(5, r.Complete);
        Assert.Equal(3, r.Incomplete);
        Assert.Equal(2, r.Peers.Count);
        Assert.Equal("1.2.3.4:6881", r.Peers[0].ToString());
        Assert.Equal("10.0.0.1:51413", r.Peers[1].ToString());
    }

    [Fact]
    public void ParseAnnounceResponse_reads_dictionary_peers()
    {
        var p = new BencodeDictionary();
        p.Add("ip", new BencodeString("8.8.8.8"));
        p.Add("port", new BencodeInteger(1234));
        var d = new BencodeDictionary();
        d.Add("interval", new BencodeInteger(900));
        d.Add("peers", new BencodeList(new BencodeValue[] { p }));

        var r = HttpTrackerClient.ParseAnnounceResponse(d.Encode());
        Assert.Equal(900, r.Interval);
        Assert.Equal("8.8.8.8:1234", Assert.Single(r.Peers).ToString());
    }

    [Fact]
    public void ParseAnnounceResponse_surfaces_failure_reason()
    {
        var d = new BencodeDictionary();
        d.Add("failure reason", new BencodeString("torrent not registered"));
        var ex = Assert.Throws<TrackerException>(() => HttpTrackerClient.ParseAnnounceResponse(d.Encode()));
        Assert.Contains("torrent not registered", ex.Message);
    }

    [Fact]
    public void BuildScrapeUrl_swaps_announce_for_scrape()
    {
        var uri = HttpTrackerClient.BuildScrapeUrl(new Uri("http://tracker.example/announce"), Filled(20, 0xAB));
        Assert.StartsWith("http://tracker.example/scrape?info_hash=%AB", uri.ToString());
    }

    [Fact]
    public async Task Real_http_get_over_loopback_preserves_infohash_and_parses()
    {
        var body = CannedAnnounce();
        var requestLine = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serve = Task.Run(async () =>
        {
            using var conn = await listener.AcceptTcpClientAsync();
            using var stream = conn.GetStream();
            var header = new List<byte>();
            var one = new byte[1];
            while (await stream.ReadAsync(one) == 1)
            {
                header.Add(one[0]);
                int n = header.Count;
                if (n >= 4 && header[n - 4] == 13 && header[n - 3] == 10 && header[n - 2] == 13 && header[n - 1] == 10) break;
            }
            requestLine.TrySetResult(Encoding.ASCII.GetString(header.ToArray()).Split("\r\n")[0]);
            var head = $"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
            await stream.WriteAsync(body);
            await stream.FlushAsync();
        });

        var client = new HttpTrackerClient();
        var response = await client.AnnounceAsync(new Uri($"http://127.0.0.1:{port}/announce"), SampleRequest());

        Assert.Equal(1800, response.Interval);
        Assert.Equal(2, response.Peers.Count);

        var line = await requestLine.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.StartsWith("GET /announce?", line);
        Assert.Contains("info_hash=%AB%AB%AB", line); // byte-for-byte percent-encoding survived the round trip

        await serve;
    }
}

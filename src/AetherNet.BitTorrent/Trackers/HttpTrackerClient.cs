// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Net;
using System.Text;
using AetherNet.BitTorrent.Bencoding;

namespace AetherNet.BitTorrent.Trackers;

/// <summary>
/// An HTTP(S) BitTorrent tracker client (BEP-3 announce, BEP-23 compact peers, BEP-48 scrape).
///
/// <para>The 20-byte <c>info_hash</c> and <c>peer_id</c> are percent-encoded byte-for-byte and the
/// query is sent verbatim (canonicalisation disabled) so the tracker receives the exact bytes — the
/// classic BitTorrent HTTP pitfall.</para>
/// </summary>
public sealed class HttpTrackerClient
{
    private readonly HttpClient _http;

    public HttpTrackerClient(HttpClient? http = null) => _http = http ?? new HttpClient();

    public async Task<AnnounceResponse> AnnounceAsync(Uri announceUri, AnnounceRequest request, CancellationToken ct = default)
    {
        var url = BuildAnnounceUrl(announceUri, request);
        using var message = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return ParseAnnounceResponse(bytes);
    }

    public async Task<ScrapeResponse> ScrapeAsync(Uri announceUri, byte[] infoHash, CancellationToken ct = default)
    {
        var url = BuildScrapeUrl(announceUri, infoHash);
        using var message = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return ParseScrapeResponse(bytes, infoHash);
    }

    // ── URL building ────────────────────────────────────────────────────────────

    public static Uri BuildAnnounceUrl(Uri announceUri, AnnounceRequest request)
    {
        ArgumentNullException.ThrowIfNull(announceUri);
        ArgumentNullException.ThrowIfNull(request);
        if (request.InfoHash is not { Length: 20 }) throw new ArgumentException("info_hash must be 20 bytes");
        if (request.PeerId is not { Length: 20 }) throw new ArgumentException("peer_id must be 20 bytes");

        var sb = new StringBuilder(announceUri.AbsoluteUri);
        sb.Append(announceUri.Query.Length == 0 ? '?' : '&');
        sb.Append("info_hash=").Append(PercentEncode(request.InfoHash));
        sb.Append("&peer_id=").Append(PercentEncode(request.PeerId));
        sb.Append("&port=").Append(request.Port);
        sb.Append("&uploaded=").Append(request.Uploaded);
        sb.Append("&downloaded=").Append(request.Downloaded);
        sb.Append("&left=").Append(request.Left);
        sb.Append("&compact=").Append(request.Compact ? '1' : '0');
        sb.Append("&numwant=").Append(request.NumWant);
        if (request.Event != TrackerEvent.None)
            sb.Append("&event=").Append(request.Event switch
            {
                TrackerEvent.Started => "started",
                TrackerEvent.Stopped => "stopped",
                TrackerEvent.Completed => "completed",
                _ => "",
            });

        return new Uri(sb.ToString(), new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
    }

    public static Uri BuildScrapeUrl(Uri announceUri, byte[] infoHash)
    {
        ArgumentNullException.ThrowIfNull(announceUri);
        if (infoHash is not { Length: 20 }) throw new ArgumentException("info_hash must be 20 bytes");

        string absolute = announceUri.AbsoluteUri;
        int q = absolute.IndexOf('?');
        string path = q < 0 ? absolute : absolute[..q];
        int slash = path.LastIndexOf('/');
        string lastSegment = slash < 0 ? path : path[(slash + 1)..];
        if (!lastSegment.StartsWith("announce", StringComparison.Ordinal))
            throw new TrackerException("tracker announce URL does not support scrape");
        string scrapePath = path[..(slash + 1)] + "scrape" + lastSegment["announce".Length..];

        return new Uri(scrapePath + "?info_hash=" + PercentEncode(infoHash),
            new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
    }

    private static string PercentEncode(byte[] data)
    {
        var sb = new StringBuilder(data.Length * 3);
        foreach (byte b in data)
        {
            bool unreserved = b is (>= (byte)'A' and <= (byte)'Z')
                or (>= (byte)'a' and <= (byte)'z')
                or (>= (byte)'0' and <= (byte)'9')
                or (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~';
            if (unreserved) sb.Append((char)b);
            else sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    // ── Response parsing ────────────────────────────────────────────────────────

    public static AnnounceResponse ParseAnnounceResponse(ReadOnlySpan<byte> bencoded)
    {
        var dict = Bencode.Decode(bencoded).AsDictionary();

        if (dict["failure reason"] is { } failure)
            throw new TrackerException($"tracker failure: {failure.AsText()}");

        return new AnnounceResponse
        {
            Interval = (int)(dict["interval"]?.AsInteger() ?? 0),
            MinInterval = dict["min interval"] is { } mi ? (int)mi.AsInteger() : null,
            Complete = (int)(dict["complete"]?.AsInteger() ?? 0),
            Incomplete = (int)(dict["incomplete"]?.AsInteger() ?? 0),
            Peers = ParsePeers(dict),
        };
    }

    public static ScrapeResponse ParseScrapeResponse(ReadOnlySpan<byte> bencoded, byte[] infoHash)
    {
        var files = (Bencode.Decode(bencoded).AsDictionary()["files"]
            ?? throw new TrackerException("scrape response has no 'files'")).AsDictionary();
        var entry = (files.TryGet(infoHash, out var v) ? v
            : throw new TrackerException("scrape response has no entry for this info-hash")).AsDictionary();
        return new ScrapeResponse(
            Complete: (int)(entry["complete"]?.AsInteger() ?? 0),
            Downloaded: (int)(entry["downloaded"]?.AsInteger() ?? 0),
            Incomplete: (int)(entry["incomplete"]?.AsInteger() ?? 0));
    }

    private static IReadOnlyList<PeerAddress> ParsePeers(BencodeDictionary dict)
    {
        var peers = new List<PeerAddress>();

        switch (dict["peers"])
        {
            case BencodeString compact: // BEP-23: 6 bytes per peer (4 IPv4 + 2 port)
                var b = compact.Value;
                if (b.Length % 6 != 0) throw new TrackerException("compact peers length is not a multiple of 6");
                for (int i = 0; i + 6 <= b.Length; i += 6)
                    peers.Add(new PeerAddress(new IPAddress(b[i..(i + 4)]), BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(i + 4, 2))));
                break;
            case BencodeList list: // legacy dictionary model
                foreach (var p in list.Items)
                {
                    var pd = p.AsDictionary();
                    peers.Add(new PeerAddress(
                        IPAddress.Parse((pd["ip"] ?? throw new TrackerException("peer has no 'ip'")).AsText()),
                        (int)(pd["port"] ?? throw new TrackerException("peer has no 'port'")).AsInteger()));
                }
                break;
        }

        if (dict["peers6"] is BencodeString compact6 && compact6.Value.Length % 18 == 0) // 16 IPv6 + 2 port
        {
            var b6 = compact6.Value;
            for (int i = 0; i + 18 <= b6.Length; i += 18)
                peers.Add(new PeerAddress(new IPAddress(b6[i..(i + 16)]), BinaryPrimitives.ReadUInt16BigEndian(b6.AsSpan(i + 16, 2))));
        }

        return peers;
    }
}

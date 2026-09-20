// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Sample.Shared.Services.Cast;

/// <summary>
/// Casting to a smart TV over the LAN, the open-standard way — SSDP to find it, AVTransport SOAP to drive
/// it, and a tiny HTTP server on THIS phone so the TV can fetch the video by content hash. No Google
/// Cast, no cloud: the phone hands a bare-TV a URL on the local network and tells it to play.
///
/// <para>
/// The wire strings are built and checked in <see cref="DlnaProtocol"/>; this class owns the sockets:
/// the UDP search, the HTTP calls to the renderer, and the <see cref="HttpListener"/> that serves the
/// bytes (with Range support, which many TVs insist on). The bytes come from the same content store every
/// other attachment is served from — the TV is just one more thing asking for them.
/// </para>
/// </summary>
public sealed class DlnaCastService : IDisposable
{
    private const int MediaPort = 8099;

    private readonly AttachmentService? _attachments;
    private readonly IMulticastHold _multicastHold;
    private readonly ILogger<DlnaCastService> _log;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(6) };

    private HttpListener? _media;
    private CancellationTokenSource? _mediaStop;
    private readonly object _gate = new();

    // The one content currently offered to a TV, cached so a TV's Range requests don't re-read it each time.
    private volatile CachedMedia? _serving;

    /// <summary>A human line for the log — wired to logcat so a failed TV search can be seen, not guessed at.</summary>
    public event Action<string>? Trace;
    private void T(string m) { try { Trace?.Invoke(m); } catch { /* a logger must never throw into the caller */ } }

    public DlnaCastService(AttachmentService? attachments = null, IMulticastHold? multicastHold = null, ILogger<DlnaCastService>? log = null)
    {
        _attachments = attachments;
        _multicastHold = multicastHold ?? new NoMulticastHold();
        _log = log ?? NullLogger<DlnaCastService>.Instance;
    }

    // ── Discovery ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Find the media renderers (TVs) on this network. Sends an SSDP search and collects who answers
    /// within the window, then fetches each one's description to confirm it can play video and to learn
    /// its name and control endpoint. Best-effort: a TV that does not answer in time simply is not listed.
    /// </summary>
    public async Task<IReadOnlyList<CastTarget>> DiscoverAsync(TimeSpan? window = null, CancellationToken cancellationToken = default)
    {
        var deadline = window ?? TimeSpan.FromSeconds(3);
        var locations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Without a real LAN address there is no home Wi-Fi to search — only Wi-Fi Direct, where no TV lives.
        var lan = LocalAddress();
        if (lan is null || !IPAddress.TryParse(lan, out var lanIp))
        {
            _log.LogDebug("No home-Wi-Fi address — cannot search for TVs");
            T("cast: no home Wi-Fi address (only Wi-Fi Direct) — no network a TV would be on");
            return Array.Empty<CastTarget>();
        }
        T($"cast: searching for TVs via {lan}");

        // Android filters multicast to save power unless a MulticastLock is held — the reason a first
        // attempt found nothing. Held only for the search.
        using var hold = _multicastHold.Acquire();

        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            // Bind to the HOME Wi-Fi interface, not the Wi-Fi Direct one (192.168.49.x) the mesh runs on —
            // the search must go out the network the TV is actually on, and replies come back here.
            udp.Client.Bind(new IPEndPoint(lanIp, 0));
            try { udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, lanIp.GetAddressBytes()); }
            catch (SocketException) { /* some stacks refuse this; the bind above is the important part */ }

            var group = new IPEndPoint(IPAddress.Parse(DlnaProtocol.SsdpAddress), DlnaProtocol.SsdpPort);
            var mx = Math.Max(1, (int)deadline.TotalSeconds);

            // Ask three ways — some TVs answer "MediaRenderer", some only "AVTransport" or "ssdp:all" — and
            // each a couple of times, since a single UDP datagram is easily lost on Wi-Fi.
            string[] searchTargets =
            [
                "urn:schemas-upnp-org:device:MediaRenderer:1",
                "urn:schemas-upnp-org:service:AVTransport:1",
                "ssdp:all",
            ];
            foreach (var st in searchTargets)
            {
                var probe = Encoding.ASCII.GetBytes(DlnaProtocol.BuildSearch(mx, st));
                for (var i = 0; i < 2; i++)
                    await udp.SendAsync(probe, probe.Length, group).ConfigureAwait(false);
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(deadline);
            while (!cts.IsCancellationRequested)
            {
                UdpReceiveResult res;
                try { res = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { break; }

                var headers = DlnaProtocol.ParseHeaders(Encoding.ASCII.GetString(res.Buffer));
                headers.TryGetValue("ST", out var st);
                if (headers.TryGetValue("LOCATION", out var loc) && !string.IsNullOrWhiteSpace(loc))
                {
                    if (locations.Add(loc.Trim()))
                        T($"cast: reply from {res.RemoteEndPoint.Address} — st={st} loc={loc.Trim()}");
                }
            }
        }
        catch (SocketException ex) { _log.LogDebug(ex, "SSDP search could not open a socket"); }

        var targets = new List<CastTarget>();
        foreach (var loc in locations)
        {
            if (cancellationToken.IsCancellationRequested) break;
            try
            {
                var xml = await _http.GetStringAsync(loc, cancellationToken).ConfigureAwait(false);
                var control = DlnaProtocol.AvTransportControlUrl(xml, loc);
                if (control is null) continue;   // not a renderer we can drive
                targets.Add(new CastTarget(loc, DlnaProtocol.FriendlyName(xml), CastKind.Dlna, control));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _log.LogDebug(ex, "Could not read a device description at {Location}", loc);
            }
        }

        _log.LogDebug("DLNA discovery found {Count} renderer(s)", targets.Count);
        T($"cast: heard {locations.Count} UPnP device(s), {targets.Count} that can play video");
        return targets;
    }

    // ── Casting ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Play a piece of content (named by hash) on a TV: make sure this phone is serving the bytes, tell
    /// the TV the URL, and press play. Returns false if we have no bytes to serve or the TV refused.
    /// </summary>
    public async Task<bool> CastAsync(CastTarget target, string hash, string contentType, string title,
        CancellationToken cancellationToken = default)
    {
        if (target.ControlUrl is null || _attachments is null) return false;

        var bytes = await _attachments.GetAsync(hash, cancellationToken).ConfigureAwait(false);
        if (bytes is null) return false;   // not fully arrived yet — nothing to serve

        var ip = LocalAddress();
        if (ip is null) { _log.LogDebug("No LAN address — cannot serve media to a TV"); return false; }

        StartMediaServer();
        _serving = new CachedMedia(hash, contentType, bytes);
        var mediaUrl = $"http://{ip}:{MediaPort}/cast/{hash}";

        var didl = DlnaProtocol.Didl(title, contentType, mediaUrl);
        if (!await PostAsync(target.ControlUrl, "SetAVTransportURI", DlnaProtocol.SetAvTransportUri(mediaUrl, didl), cancellationToken).ConfigureAwait(false))
            return false;
        return await PostAsync(target.ControlUrl, "Play", DlnaProtocol.Play(), cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> PlayAsync(CastTarget t, CancellationToken ct = default) => Drive(t, "Play", DlnaProtocol.Play(), ct);
    public Task<bool> PauseAsync(CastTarget t, CancellationToken ct = default) => Drive(t, "Pause", DlnaProtocol.Pause(), ct);
    public Task<bool> SeekAsync(CastTarget t, long positionMs, CancellationToken ct = default) => Drive(t, "Seek", DlnaProtocol.Seek(positionMs), ct);

    /// <summary>Stop playback on the TV and stop serving the bytes — the cast is over.</summary>
    public async Task<bool> StopAsync(CastTarget t, CancellationToken ct = default)
    {
        var ok = await Drive(t, "Stop", DlnaProtocol.Stop(), ct).ConfigureAwait(false);
        _serving = null;
        return ok;
    }

    /// <summary>
    /// Ask the screen what it is doing right now — state and position — so the caster's remote can show a
    /// truthful "Buffering / Playing 0:37 / 5:00" instead of a blind guess. Null if the screen did not answer.
    /// </summary>
    public async Task<CastStatus?> StatusAsync(CastTarget t, CancellationToken ct = default)
    {
        if (t.ControlUrl is null) return null;

        var stateXml = await PostReadAsync(t.ControlUrl, "GetTransportInfo", DlnaProtocol.GetTransportInfo(), ct).ConfigureAwait(false);
        if (stateXml is null) return null;
        var state = DlnaProtocol.ReadTransportState(stateXml) ?? "UNKNOWN";

        long pos = 0, dur = 0;
        var posXml = await PostReadAsync(t.ControlUrl, "GetPositionInfo", DlnaProtocol.GetPositionInfo(), ct).ConfigureAwait(false);
        if (posXml is not null) (pos, dur) = DlnaProtocol.ReadPosition(posXml);

        return new CastStatus(state, pos, dur);
    }

    private Task<bool> Drive(CastTarget t, string action, string body, CancellationToken ct) =>
        t.ControlUrl is null ? Task.FromResult(false) : PostAsync(t.ControlUrl, action, body, ct);

    private async Task<bool> PostAsync(string controlUrl, string action, string body, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, controlUrl)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/xml"),
            };
            req.Headers.TryAddWithoutValidation("SOAPACTION", DlnaProtocol.SoapAction(action));
            using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
                _log.LogDebug("TV refused {Action}: {Status}", action, (int)res.StatusCode);
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _log.LogDebug(ex, "Could not send {Action} to the TV", action);
            return false;
        }
    }

    /// <summary>Like <see cref="PostAsync"/> but returns the SOAP response body (for the read actions), or null on failure.</summary>
    private async Task<string?> PostReadAsync(string controlUrl, string action, string body, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, controlUrl)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/xml"),
            };
            req.Headers.TryAddWithoutValidation("SOAPACTION", DlnaProtocol.SoapAction(action));
            using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) { _log.LogDebug("TV refused {Action}: {Status}", action, (int)res.StatusCode); return null; }
            return await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _log.LogDebug(ex, "Could not read {Action} from the TV", action);
            return null;
        }
    }

    // ── The media server the TV fetches from ──────────────────────────────────────

    private void StartMediaServer()
    {
        lock (_gate)
        {
            if (_media is not null) return;
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://+:{MediaPort}/");
            try { listener.Start(); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not start the cast media server on :{Port}", MediaPort);
                return;
            }
            _media = listener;
            _mediaStop = new CancellationTokenSource();
            _ = ServeLoopAsync(listener, _mediaStop.Token);
        }
    }

    private async Task ServeLoopAsync(HttpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception) { break; }   // listener stopped
            _ = Task.Run(() => ServeOneAsync(ctx), token);
        }
    }

    private async Task ServeOneAsync(HttpListenerContext ctx)
    {
        try
        {
            var media = _serving;
            var path = ctx.Request.Url?.AbsolutePath ?? "";
            if (media is null || !path.EndsWith(media.Hash, StringComparison.Ordinal))
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }

            var bytes = media.Bytes;
            ctx.Response.ContentType = media.ContentType;
            ctx.Response.Headers["Accept-Ranges"] = "bytes";

            // Many TVs open with a Range request; honour a single range so playback starts.
            var (start, end) = ParseRange(ctx.Request.Headers["Range"], bytes.Length);
            if (start > 0 || end < bytes.Length - 1)
            {
                ctx.Response.StatusCode = 206;
                ctx.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{bytes.Length}";
            }

            var length = end - start + 1;
            ctx.Response.ContentLength64 = length;
            if (ctx.Request.HttpMethod != "HEAD")
                await ctx.Response.OutputStream.WriteAsync(bytes.AsMemory(start, (int)length)).ConfigureAwait(false);
            ctx.Response.Close();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "A cast media request failed");
            try { ctx.Response.Abort(); } catch { /* already gone */ }
        }
    }

    /// <summary>Parse an HTTP Range header ("bytes=start-end") into inclusive bounds; the whole file if absent or malformed.</summary>
    private static (int Start, int End) ParseRange(string? header, int total)
    {
        var whole = (0, total - 1);
        if (string.IsNullOrWhiteSpace(header) || !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return whole;
        var spec = header["bytes=".Length..].Split(',')[0].Split('-');
        if (spec.Length != 2) return whole;

        var start = int.TryParse(spec[0], out var s) ? s : 0;
        var end = int.TryParse(spec[1], out var e) ? e : total - 1;
        if (start < 0 || start >= total) start = 0;
        if (end < start || end >= total) end = total - 1;
        return (start, end);
    }

    private static string? LocalAddress()
    {
        try
        {
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                .Select(a => a.ToString())
                .ToArray();
            // Prefer a real LAN address over the Wi-Fi Direct subnet, which a TV on the home Wi-Fi cannot reach.
            return candidates.FirstOrDefault(a => !a.StartsWith("192.168.49.", StringComparison.Ordinal))
                ?? candidates.FirstOrDefault();
        }
        catch (NetworkInformationException) { return null; }
    }

    public void Dispose()
    {
        _mediaStop?.Cancel();
        try { _media?.Stop(); _media?.Close(); } catch { /* already stopped */ }
        _media = null;
        _mediaStop?.Dispose();
        _http.Dispose();
    }

    private sealed record CachedMedia(string Hash, string ContentType, byte[] Bytes);
}

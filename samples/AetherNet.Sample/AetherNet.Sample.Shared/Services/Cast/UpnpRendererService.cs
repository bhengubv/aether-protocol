// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Sample.Shared.Services.Cast;

/// <summary>
/// Aether AS a screen: a UPnP/DLNA MediaRenderer any caster can send a video to over the LAN — another
/// Aether phone, a PC, a TV's "play to". No third-party renderer app, no Google. This advertises itself on
/// SSDP, serves its device/service descriptions, and turns AVTransport commands (SetAVTransportURI, Play,
/// Pause, Stop, Seek) into events a full-screen player acts on. The format lives in <see cref="UpnpRenderer"/>;
/// this owns the sockets and the transport state.
/// </summary>
public sealed class UpnpRendererService : IDisposable
{
    private const int HttpPort = 8098;
    private static readonly TimeSpan NotifyEvery = TimeSpan.FromSeconds(30);

    private readonly IMulticastHold _multicastHold;
    private readonly ILogger<UpnpRendererService> _log;

    private string _udn = "";
    private string _friendlyName = "Aether";
    private string _location = "";
    private string? _lanIp;

    private HttpListener? _http;
    private Socket? _ssdp;
    private CancellationTokenSource? _stop;
    private Timer? _notifyTimer;
    private IDisposable? _hold;
    private bool _running;
    private readonly object _gate = new();

    // Current transport, as a caster sees it.
    private string _state = "NO_MEDIA_PRESENT";   // PLAYING · PAUSED_PLAYBACK · STOPPED · NO_MEDIA_PRESENT
    private string _uri = "";
    private long _positionMs;
    private long _durationMs;

    public UpnpRendererService(IMulticastHold? multicastHold = null, ILogger<UpnpRendererService>? log = null)
    {
        _multicastHold = multicastHold ?? new NoMulticastHold();
        _log = log ?? NullLogger<UpnpRendererService>.Instance;
    }

    /// <summary>Something on the LAN cast a video to this phone — play it full screen. Carries the media URL + a title.</summary>
    public event Action<string, string>? PlayRequested;
    public event Action? PauseRequested;
    public event Action? StopRequested;
    public event Action<long>? SeekRequested;
    public event Action<string>? Trace;
    private void T(string m) { try { Trace?.Invoke(m); } catch { } }

    /// <summary>What the phone should currently be showing, so a re-opened player can catch up.</summary>
    public string CurrentUri => _uri;
    public bool IsCasting => _state is "PLAYING" or "PAUSED_PLAYBACK";

    /// <summary>The player tells us where it is, so a caster's progress bar and transport state are truthful.</summary>
    public void ReportProgress(long positionMs, long durationMs) { _positionMs = positionMs; _durationMs = durationMs; }
    public void ReportEnded() { _state = "STOPPED"; }

    /// <summary>Begin advertising this phone as a castable screen under <paramref name="friendlyName"/>.</summary>
    public void Start(string aetherTag, string friendlyName)
    {
        lock (_gate)
        {
            if (_running) return;
            _lanIp = LocalAddress();
            if (_lanIp is null) { _log.LogDebug("No LAN address — renderer cannot advertise"); T("renderer: no home Wi-Fi, not advertising"); return; }

            _udn = UpnpRenderer.Udn(aetherTag);
            _friendlyName = string.IsNullOrWhiteSpace(friendlyName) ? "Aether" : friendlyName;
            _location = $"http://{_lanIp}:{HttpPort}/desc.xml";
            _stop = new CancellationTokenSource();

            if (!StartHttp() || !StartSsdp()) { StopInternal(); return; }

            _hold = _multicastHold.Acquire();   // hear M-SEARCH multicast
            _running = true;
            _notifyTimer = new Timer(_ => SendAlive(), null, TimeSpan.Zero, NotifyEvery);
            T($"renderer: advertising \"{_friendlyName}\" at {_location}");
        }
    }

    // ── HTTP: descriptions + control ─────────────────────────────────────────────

    private bool StartHttp()
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://+:{HttpPort}/");
        try { listener.Start(); }
        catch (Exception ex) { _log.LogWarning(ex, "Renderer HTTP could not start on :{Port}", HttpPort); return false; }
        _http = listener;
        _ = HttpLoopAsync(listener, _stop!.Token);
        return true;
    }

    private async Task HttpLoopAsync(HttpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception) { break; }
            _ = Task.Run(() => ServeAsync(ctx), token);
        }
    }

    private async Task ServeAsync(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "";
            if (ctx.Request.HttpMethod == "GET")
            {
                var xml = path switch
                {
                    "/desc.xml" => UpnpRenderer.DeviceDescription(_friendlyName, _udn),
                    "/avt/scpd.xml" => UpnpRenderer.AvTransportScpd(),
                    "/cm/scpd.xml" => UpnpRenderer.ConnectionManagerScpd(),
                    _ => null,
                };
                if (xml is null) { ctx.Response.StatusCode = 404; ctx.Response.Close(); return; }
                await WriteXmlAsync(ctx, xml).ConfigureAwait(false);
                return;
            }

            if (ctx.Request.HttpMethod == "POST" && (path == "/avt/control" || path == "/cm/control"))
            {
                string body;
                using (var r = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) body = await r.ReadToEndAsync().ConfigureAwait(false);
                var action = UpnpRenderer.ActionOf(ctx.Request.Headers["SOAPACTION"]);
                var reply = Handle(action, body);
                await WriteXmlAsync(ctx, reply).ConfigureAwait(false);
                return;
            }

            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Renderer request failed");
            try { ctx.Response.Abort(); } catch { }
        }
    }

    private string Handle(string? action, string body)
    {
        switch (action)
        {
            case "SetAVTransportURI":
                var (uri, title) = UpnpRenderer.ReadSetUri(body);
                if (string.IsNullOrEmpty(uri)) return UpnpRenderer.Fault(716, "No CurrentURI");
                _uri = uri; _state = "STOPPED"; _positionMs = 0; _durationMs = 0;
                T($"renderer: SetAVTransportURI {uri}");
                _ = InvokeOnMain(() => PlayRequested?.Invoke(uri, title ?? "Casting"));
                _state = "PLAYING";
                return UpnpRenderer.Ack("urn:schemas-upnp-org:service:AVTransport:1", "SetAVTransportURI");
            case "Play":
                _state = "PLAYING"; T("renderer: Play"); _ = InvokeOnMain(() => PlayRequested?.Invoke(_uri, "Casting"));
                return UpnpRenderer.Ack("urn:schemas-upnp-org:service:AVTransport:1", "Play");
            case "Pause":
                _state = "PAUSED_PLAYBACK"; T("renderer: Pause"); _ = InvokeOnMain(() => PauseRequested?.Invoke());
                return UpnpRenderer.Ack("urn:schemas-upnp-org:service:AVTransport:1", "Pause");
            case "Stop":
                _state = "STOPPED"; T("renderer: Stop"); _ = InvokeOnMain(() => StopRequested?.Invoke());
                return UpnpRenderer.Ack("urn:schemas-upnp-org:service:AVTransport:1", "Stop");
            case "Seek":
                var target = ReadSeekTarget(body);
                T($"renderer: Seek {target}ms"); _ = InvokeOnMain(() => SeekRequested?.Invoke(target));
                return UpnpRenderer.Ack("urn:schemas-upnp-org:service:AVTransport:1", "Seek");
            case "GetTransportInfo":
                return UpnpRenderer.TransportInfo(_state);
            case "GetPositionInfo":
                return UpnpRenderer.PositionInfo(_uri, Hms(_durationMs), Hms(_positionMs));
            case "GetProtocolInfo":
                return UpnpRenderer.ProtocolInfo();
            default:
                return UpnpRenderer.Fault(401, "Invalid Action");
        }
    }

    // ── SSDP: answer searches + announce ─────────────────────────────────────────

    private bool StartSsdp()
    {
        try
        {
            var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            sock.Bind(new IPEndPoint(IPAddress.Any, DlnaProtocol.SsdpPort));
            sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                new MulticastOption(IPAddress.Parse(DlnaProtocol.SsdpAddress), IPAddress.Parse(_lanIp!)));
            _ssdp = sock;
            _ = SsdpLoopAsync(sock, _stop!.Token);
            return true;
        }
        catch (SocketException ex) { _log.LogWarning(ex, "Renderer SSDP could not bind :1900"); return false; }
    }

    private async Task SsdpLoopAsync(Socket sock, CancellationToken token)
    {
        var buf = new byte[2048];
        while (!token.IsCancellationRequested)
        {
            SocketReceiveFromResult res;
            try
            {
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                res = await sock.ReceiveFromAsync(buf, SocketFlags.None, from, token).ConfigureAwait(false);
            }
            catch (Exception) { break; }

            var text = Encoding.ASCII.GetString(buf, 0, res.ReceivedBytes);
            if (!text.StartsWith("M-SEARCH", StringComparison.OrdinalIgnoreCase)) continue;

            var headers = DlnaProtocol.ParseHeaders(text);
            headers.TryGetValue("ST", out var st);
            if (!UpnpRenderer.Answers(st)) continue;

            foreach (var (rst, usn) in ReplyIdentities(st!))
            {
                var reply = Encoding.ASCII.GetBytes(UpnpRenderer.SearchResponse(_location, rst, usn));
                try { await sock.SendToAsync(reply, SocketFlags.None, res.RemoteEndPoint).ConfigureAwait(false); } catch { }
            }
            T($"renderer: answered M-SEARCH ({st}) from {res.RemoteEndPoint}");
        }
    }

    /// <summary>Which (ST, USN) pairs to send back for a given search target.</summary>
    private IEnumerable<(string St, string Usn)> ReplyIdentities(string st)
    {
        var ads = UpnpRenderer.Advertisements(_udn);
        if (st is "ssdp:all")
            return ads.Select(a => (a.Nt, a.Usn));
        var match = ads.FirstOrDefault(a => a.Nt == st);
        if (match.Nt is not null) return new[] { (st, match.Usn) };
        // uuid:… or an unlisted-but-answered ST: echo it with our uuid.
        return new[] { (st, "uuid:" + _udn) };
    }

    private void SendAlive()
    {
        var sock = _ssdp;
        if (sock is null) return;
        var group = new IPEndPoint(IPAddress.Parse(DlnaProtocol.SsdpAddress), DlnaProtocol.SsdpPort);
        foreach (var (nt, usn) in UpnpRenderer.Advertisements(_udn))
        {
            var bytes = Encoding.ASCII.GetBytes(UpnpRenderer.NotifyAlive(_location, nt, usn));
            try { sock.SendTo(bytes, group); } catch { }
        }
    }

    private void SendByebye()
    {
        var sock = _ssdp;
        if (sock is null) return;
        var group = new IPEndPoint(IPAddress.Parse(DlnaProtocol.SsdpAddress), DlnaProtocol.SsdpPort);
        foreach (var (nt, usn) in UpnpRenderer.Advertisements(_udn))
        {
            var bytes = Encoding.ASCII.GetBytes(UpnpRenderer.NotifyByebye(nt, usn));
            try { sock.SendTo(bytes, group); } catch { }
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    /// <summary>Marshalling hook — set by the head so events reach the UI thread. Defaults to direct.</summary>
    public Func<Action, Task>? Dispatcher { get; set; }
    private Task InvokeOnMain(Action a) => Dispatcher is not null ? Dispatcher(a) : SafeRun(a);
    private static Task SafeRun(Action a) { try { a(); } catch { } return Task.CompletedTask; }

    private static long ReadSeekTarget(string body)
    {
        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(body);
            var target = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Target")?.Value;
            if (string.IsNullOrWhiteSpace(target)) return 0;
            var parts = target.Split(':');
            if (parts.Length == 3 && int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var m) && double.TryParse(parts[2], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var s))
                return (long)(((h * 60 + m) * 60 + s) * 1000);
        }
        catch (System.Xml.XmlException) { }
        return 0;
    }

    private static string Hms(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return ((int)t.TotalHours).ToString("00") + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00");
    }

    private static async Task WriteXmlAsync(HttpListenerContext ctx, string xml)
    {
        var bytes = Encoding.UTF8.GetBytes(xml);
        ctx.Response.ContentType = "text/xml; charset=\"utf-8\"";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        ctx.Response.Close();
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
            return candidates.FirstOrDefault(a => !a.StartsWith("192.168.49.", StringComparison.Ordinal)) ?? candidates.FirstOrDefault();
        }
        catch (NetworkInformationException) { return null; }
    }

    private void StopInternal()
    {
        try { if (_ssdp is not null && _running) SendByebye(); } catch { }
        _notifyTimer?.Dispose(); _notifyTimer = null;
        _stop?.Cancel();
        try { _http?.Stop(); _http?.Close(); } catch { } _http = null;
        try { _ssdp?.Close(); } catch { } _ssdp = null;
        _hold?.Dispose(); _hold = null;
        _stop?.Dispose(); _stop = null;
        _running = false;
    }

    public void Dispose() { lock (_gate) StopInternal(); }
}

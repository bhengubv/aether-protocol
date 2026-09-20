// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Security;
using System.Text;
using System.Xml.Linq;

namespace AetherNet.Sample.Shared.Services.Cast;

/// <summary>
/// The plain, testable half of casting to a smart TV: the UPnP/DLNA wire strings, with no sockets in
/// sight. SSDP finds the TV, its device description names the AVTransport control endpoint, and SOAP
/// tells it what to play and when. Everything here is an open standard — no Google Cast, no GMS — so a
/// bare Android TV or a cheap dongle answers it. The live sockets that carry these strings live in
/// <see cref="DlnaCastService"/>; keeping the format here means it can be checked byte-for-byte in a test
/// without a TV in the room.
/// </summary>
public static class DlnaProtocol
{
    /// <summary>The SSDP multicast group and port every UPnP device listens on.</summary>
    public const string SsdpAddress = "239.255.255.250";
    public const int SsdpPort = 1900;

    private const string AvTransport = "urn:schemas-upnp-org:service:AVTransport:1";

    /// <summary>
    /// The M-SEARCH datagram that asks every media renderer on the segment to announce itself. CRLF line
    /// endings and the trailing blank line are part of the format — a device ignores anything malformed.
    /// </summary>
    public static string BuildSearch(int mxSeconds = 2, string st = "urn:schemas-upnp-org:device:MediaRenderer:1") =>
        "M-SEARCH * HTTP/1.1\r\n" +
        $"HOST: {SsdpAddress}:{SsdpPort}\r\n" +
        "MAN: \"ssdp:discover\"\r\n" +
        $"MX: {mxSeconds.ToString(CultureInfo.InvariantCulture)}\r\n" +
        $"ST: {st}\r\n" +
        "\r\n";

    /// <summary>
    /// Parse an SSDP response (or any HTTP-style header block) into a case-insensitive header map. The
    /// one header that matters is LOCATION — the URL of the device's description document.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseHeaders(string text)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(text)) return headers;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (key.Length > 0) headers[key] = value;
        }
        return headers;
    }

    /// <summary>The friendly name a person would recognise ("Living Room TV"), or a fallback.</summary>
    public static string FriendlyName(string deviceXml)
    {
        try
        {
            var doc = XDocument.Parse(deviceXml);
            var name = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "friendlyName")?.Value;
            return string.IsNullOrWhiteSpace(name) ? "TV" : name.Trim();
        }
        catch (System.Xml.XmlException) { return "TV"; }
    }

    /// <summary>
    /// Find the AVTransport service's control URL in a device description, resolved to an absolute URL
    /// against <paramref name="location"/> (the description's own URL) or an explicit &lt;URLBase&gt;.
    /// Returns null when the device has no AVTransport — i.e. it is not something that plays video.
    /// </summary>
    public static string? AvTransportControlUrl(string deviceXml, string location)
    {
        try
        {
            var doc = XDocument.Parse(deviceXml);
            var urlBase = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "URLBase")?.Value;

            var service = doc.Descendants().FirstOrDefault(e =>
                e.Name.LocalName == "service" &&
                (e.Elements().FirstOrDefault(c => c.Name.LocalName == "serviceType")?.Value ?? "")
                    .Contains("AVTransport", StringComparison.OrdinalIgnoreCase));
            if (service is null) return null;

            var control = service.Elements().FirstOrDefault(c => c.Name.LocalName == "controlURL")?.Value;
            if (string.IsNullOrWhiteSpace(control)) return null;

            var baseUri = new Uri(string.IsNullOrWhiteSpace(urlBase) ? location : urlBase!);
            return new Uri(baseUri, control.Trim()).ToString();
        }
        catch (System.Xml.XmlException) { return null; }
        catch (UriFormatException) { return null; }
    }

    // ── SOAP bodies ────────────────────────────────────────────────────────────

    /// <summary>The value of the SOAPACTION header for one AVTransport action.</summary>
    public static string SoapAction(string action) => $"\"{AvTransport}#{action}\"";

    private static string Envelope(string action, string innerXml) =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
        "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
        "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>" +
        $"<u:{action} xmlns:u=\"{AvTransport}\"><InstanceID>0</InstanceID>{innerXml}</u:{action}>" +
        "</s:Body></s:Envelope>";

    /// <summary>
    /// The DIDL-Lite metadata that rides with the media URL. A TV shows the title from it and picks a
    /// player from the class + protocolInfo, so a video announces itself as a video.
    /// </summary>
    public static string Didl(string title, string contentType, string mediaUrl)
    {
        var cls = contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ? "object.item.audioItem"
            : contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? "object.item.imageItem"
            : "object.item.videoItem";
        return "<DIDL-Lite xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\" " +
               "xmlns:dc=\"http://purl.org/dc/elements/1.1/\" " +
               "xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\">" +
               "<item id=\"0\" parentID=\"-1\" restricted=\"1\">" +
               $"<dc:title>{Esc(title)}</dc:title><upnp:class>{cls}</upnp:class>" +
               $"<res protocolInfo=\"http-get:*:{Esc(contentType)}:*\">{Esc(mediaUrl)}</res>" +
               "</item></DIDL-Lite>";
    }

    public static string SetAvTransportUri(string mediaUrl, string didl) => Envelope("SetAVTransportURI",
        $"<CurrentURI>{Esc(mediaUrl)}</CurrentURI><CurrentURIMetaData>{Esc(didl)}</CurrentURIMetaData>");

    public static string Play() => Envelope("Play", "<Speed>1</Speed>");
    public static string Pause() => Envelope("Pause", "");
    public static string Stop() => Envelope("Stop", "");

    /// <summary>Seek to a wall-clock position. <paramref name="positionMs"/> is milliseconds from the start.</summary>
    public static string Seek(long positionMs)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, positionMs));
        var hhmmss = ((int)t.TotalHours).ToString("00", CultureInfo.InvariantCulture)
            + ":" + t.Minutes.ToString("00", CultureInfo.InvariantCulture)
            + ":" + t.Seconds.ToString("00", CultureInfo.InvariantCulture);
        return Envelope("Seek", $"<Unit>REL_TIME</Unit><Target>{hhmmss}</Target>");
    }

    // ── Reading back what the screen is doing ──────────────────────────────────

    /// <summary>Ask the renderer for its transport state (playing / paused / buffering / stopped).</summary>
    public static string GetTransportInfo() => Envelope("GetTransportInfo", "");

    /// <summary>Ask the renderer where it is and how long the media is.</summary>
    public static string GetPositionInfo() => Envelope("GetPositionInfo", "");

    /// <summary>
    /// The transport state from a GetTransportInfo response — "PLAYING", "PAUSED_PLAYBACK",
    /// "TRANSITIONING" (buffering), "STOPPED", "NO_MEDIA_PRESENT". Null when absent or malformed.
    /// </summary>
    public static string? ReadTransportState(string soapXml)
    {
        try
        {
            var v = XDocument.Parse(soapXml).Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "CurrentTransportState")?.Value;
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }
        catch (System.Xml.XmlException) { return null; }
    }

    /// <summary>
    /// Position and duration in milliseconds from a GetPositionInfo response (its RelTime and
    /// TrackDuration). A field that is absent or "NOT_IMPLEMENTED" reads as 0.
    /// </summary>
    public static (long PositionMs, long DurationMs) ReadPosition(string soapXml)
    {
        try
        {
            var doc = XDocument.Parse(soapXml);
            string? Get(string n) => doc.Descendants().FirstOrDefault(e => e.Name.LocalName == n)?.Value;
            return (ParseClock(Get("RelTime")), ParseClock(Get("TrackDuration")));
        }
        catch (System.Xml.XmlException) { return (0, 0); }
    }

    /// <summary>Parse a UPnP wall-clock time ("H:MM:SS", seconds may carry a fraction) into milliseconds.</summary>
    public static long ParseClock(string? hhmmss)
    {
        if (string.IsNullOrWhiteSpace(hhmmss)) return 0;
        var parts = hhmmss.Trim().Split(':');
        if (parts.Length != 3) return 0;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)) return 0;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m)) return 0;
        if (!int.TryParse(parts[2].Split('.')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)) return 0;
        return ((h * 3600L) + (m * 60L) + s) * 1000L;
    }

    private static string Esc(string s) => SecurityElement.Escape(s) ?? s;
}

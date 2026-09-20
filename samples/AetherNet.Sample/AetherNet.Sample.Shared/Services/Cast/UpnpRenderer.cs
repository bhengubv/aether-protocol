// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Net;
using System.Security;
using System.Xml.Linq;

namespace AetherNet.Sample.Shared.Services.Cast;

/// <summary>
/// The plain, testable half of BEING a cast target — the UPnP/DLNA MediaRenderer strings a phone serves
/// so any caster (another Aether phone, a PC, a TV's "play to") can send it a video over the LAN. This is
/// the mirror of <see cref="DlnaProtocol"/> (which drives a TV); here Aether IS the TV. Open standard, no
/// Google. The live sockets live in <see cref="UpnpRendererService"/>; the format is pinned here so it can
/// be checked without another device in the room.
/// </summary>
public static class UpnpRenderer
{
    private const string AvTransport = "urn:schemas-upnp-org:service:AVTransport:1";
    private const string ConnectionManager = "urn:schemas-upnp-org:service:ConnectionManager:1";
    private const string DeviceType = "urn:schemas-upnp-org:device:MediaRenderer:1";

    /// <summary>A stable UDN for this device, derived from its AetherTag so it never changes between runs.</summary>
    public static string Udn(string aetherTag) =>
        new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes("aether-renderer:" + aetherTag))).ToString();

    /// <summary>The device description a caster fetches to learn our name and where to send AVTransport commands.</summary>
    public static string DeviceDescription(string friendlyName, string udn) =>
        "<?xml version=\"1.0\"?>" +
        "<root xmlns=\"urn:schemas-upnp-org:device-1-0\"><specVersion><major>1</major><minor>0</minor></specVersion>" +
        "<device>" +
        $"<deviceType>{DeviceType}</deviceType>" +
        $"<friendlyName>{Esc(friendlyName)}</friendlyName>" +
        "<manufacturer>AetherNet</manufacturer><manufacturerURL>https://github.com/bhengubv/aether-protocol</manufacturerURL>" +
        "<modelName>Aether</modelName><modelNumber>1</modelNumber>" +
        $"<UDN>uuid:{udn}</UDN>" +
        "<serviceList>" +
        "<service>" +
        $"<serviceType>{AvTransport}</serviceType><serviceId>urn:upnp-org:serviceId:AVTransport</serviceId>" +
        "<controlURL>/avt/control</controlURL><eventSubURL>/avt/event</eventSubURL><SCPDURL>/avt/scpd.xml</SCPDURL></service>" +
        "<service>" +
        $"<serviceType>{ConnectionManager}</serviceType><serviceId>urn:upnp-org:serviceId:ConnectionManager</serviceId>" +
        "<controlURL>/cm/control</controlURL><eventSubURL>/cm/event</eventSubURL><SCPDURL>/cm/scpd.xml</SCPDURL></service>" +
        "</serviceList></device></root>";

    /// <summary>A minimal-but-valid AVTransport service description — the actions a caster needs to drive playback.</summary>
    public static string AvTransportScpd() =>
        "<?xml version=\"1.0\"?><scpd xmlns=\"urn:schemas-upnp-org:service-1-0\"><specVersion><major>1</major><minor>0</minor></specVersion>" +
        "<actionList>" +
        Action("SetAVTransportURI", ("InstanceID", "in", "A_ARG_TYPE_InstanceID"), ("CurrentURI", "in", "AVTransportURI"), ("CurrentURIMetaData", "in", "AVTransportURIMetaData")) +
        Action("Play", ("InstanceID", "in", "A_ARG_TYPE_InstanceID"), ("Speed", "in", "TransportPlaySpeed")) +
        Action("Pause", ("InstanceID", "in", "A_ARG_TYPE_InstanceID")) +
        Action("Stop", ("InstanceID", "in", "A_ARG_TYPE_InstanceID")) +
        Action("Seek", ("InstanceID", "in", "A_ARG_TYPE_InstanceID"), ("Unit", "in", "A_ARG_TYPE_SeekMode"), ("Target", "in", "A_ARG_TYPE_SeekTarget")) +
        Action("GetTransportInfo", ("InstanceID", "in", "A_ARG_TYPE_InstanceID"), ("CurrentTransportState", "out", "TransportState"), ("CurrentTransportStatus", "out", "TransportStatus"), ("CurrentSpeed", "out", "TransportPlaySpeed")) +
        Action("GetPositionInfo", ("InstanceID", "in", "A_ARG_TYPE_InstanceID"), ("Track", "out", "CurrentTrack"), ("TrackDuration", "out", "CurrentTrackDuration"), ("TrackURI", "out", "CurrentTrackURI"), ("RelTime", "out", "RelativeTimePosition"), ("AbsTime", "out", "AbsoluteTimePosition")) +
        "</actionList>" +
        "<serviceStateTable>" +
        StateVar("TransportState", "string") + StateVar("TransportStatus", "string") + StateVar("TransportPlaySpeed", "string") +
        StateVar("AVTransportURI", "string") + StateVar("AVTransportURIMetaData", "string") +
        StateVar("CurrentTrack", "ui4") + StateVar("CurrentTrackDuration", "string") + StateVar("CurrentTrackURI", "string") +
        StateVar("RelativeTimePosition", "string") + StateVar("AbsoluteTimePosition", "string") +
        StateVar("A_ARG_TYPE_InstanceID", "ui4") + StateVar("A_ARG_TYPE_SeekMode", "string") + StateVar("A_ARG_TYPE_SeekTarget", "string") +
        "</serviceStateTable></scpd>";

    /// <summary>A minimal ConnectionManager SCPD — some controllers insist the service exists.</summary>
    public static string ConnectionManagerScpd() =>
        "<?xml version=\"1.0\"?><scpd xmlns=\"urn:schemas-upnp-org:service-1-0\"><specVersion><major>1</major><minor>0</minor></specVersion>" +
        "<actionList>" + Action("GetProtocolInfo", ("Source", "out", "SourceProtocolInfo"), ("Sink", "out", "SinkProtocolInfo")) + "</actionList>" +
        "<serviceStateTable>" + StateVar("SourceProtocolInfo", "string") + StateVar("SinkProtocolInfo", "string") + "</serviceStateTable></scpd>";

    // ── SSDP advertisement ───────────────────────────────────────────────────────

    /// <summary>The 200 OK reply to an M-SEARCH, announcing one of our advertised identities.</summary>
    public static string SearchResponse(string location, string searchTarget, string usn) =>
        "HTTP/1.1 200 OK\r\n" +
        "CACHE-CONTROL: max-age=1800\r\nEXT:\r\n" +
        $"LOCATION: {location}\r\n" +
        "SERVER: Aether/1.0 UPnP/1.0 AetherRenderer/1.0\r\n" +
        $"ST: {searchTarget}\r\nUSN: {usn}\r\n\r\n";

    /// <summary>A NOTIFY ssdp:alive so controllers that listen (rather than search) also find us.</summary>
    public static string NotifyAlive(string location, string nt, string usn) =>
        "NOTIFY * HTTP/1.1\r\n" +
        $"HOST: {DlnaProtocol.SsdpAddress}:{DlnaProtocol.SsdpPort}\r\n" +
        "CACHE-CONTROL: max-age=1800\r\n" +
        $"LOCATION: {location}\r\nNT: {nt}\r\nNTS: ssdp:alive\r\n" +
        "SERVER: Aether/1.0 UPnP/1.0 AetherRenderer/1.0\r\n" +
        $"USN: {usn}\r\n\r\n";

    /// <summary>A NOTIFY ssdp:byebye, sent on shutdown so we don't linger in caster lists.</summary>
    public static string NotifyByebye(string nt, string usn) =>
        "NOTIFY * HTTP/1.1\r\n" +
        $"HOST: {DlnaProtocol.SsdpAddress}:{DlnaProtocol.SsdpPort}\r\nNT: {nt}\r\nNTS: ssdp:byebye\r\n" +
        $"USN: {usn}\r\n\r\n";

    /// <summary>The identities a MediaRenderer advertises: the root device, its UDN, the device type, and each service type.</summary>
    public static IReadOnlyList<(string Nt, string Usn)> Advertisements(string udn)
    {
        var u = "uuid:" + udn;
        return new[]
        {
            ("upnp:rootdevice", $"{u}::upnp:rootdevice"),
            (u, u),
            (DeviceType, $"{u}::{DeviceType}"),
            (AvTransport, $"{u}::{AvTransport}"),
            (ConnectionManager, $"{u}::{ConnectionManager}"),
        };
    }

    /// <summary>Whether an M-SEARCH's ST matches something a MediaRenderer should answer.</summary>
    public static bool Answers(string? searchTarget) => searchTarget is { Length: > 0 } st &&
        (st is "ssdp:all" or "upnp:rootdevice" || st.Contains("MediaRenderer", StringComparison.OrdinalIgnoreCase)
         || st.Contains("AVTransport", StringComparison.OrdinalIgnoreCase) || st.Contains("ConnectionManager", StringComparison.OrdinalIgnoreCase)
         || st.StartsWith("uuid:", StringComparison.OrdinalIgnoreCase));

    // ── SOAP: reading a request, writing a response ──────────────────────────────

    /// <summary>The action name from a SOAPACTION header value like "urn:...:AVTransport:1#Play".</summary>
    public static string? ActionOf(string? soapAction)
    {
        if (string.IsNullOrEmpty(soapAction)) return null;
        var hash = soapAction.IndexOf('#');
        return hash < 0 ? null : soapAction[(hash + 1)..].Trim().Trim('"');
    }

    /// <summary>Pull the media URL (and best-effort title) out of a SetAVTransportURI request body.</summary>
    public static (string? Uri, string? Title) ReadSetUri(string soapBody)
    {
        try
        {
            var doc = XDocument.Parse(soapBody);
            var uri = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "CurrentURI")?.Value;
            var meta = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "CurrentURIMetaData")?.Value;
            string? title = null;
            if (!string.IsNullOrWhiteSpace(meta))
            {
                try { title = XDocument.Parse(meta).Descendants().FirstOrDefault(e => e.Name.LocalName == "title")?.Value; }
                catch (System.Xml.XmlException) { /* metadata is optional and often junk */ }
            }
            return (string.IsNullOrWhiteSpace(uri) ? null : uri.Trim(), string.IsNullOrWhiteSpace(title) ? null : title.Trim());
        }
        catch (System.Xml.XmlException) { return (null, null); }
    }

    /// <summary>An empty action response (SetAVTransportURI, Play, Pause, Stop, Seek all just acknowledge).</summary>
    public static string Ack(string service, string action) => Response(service, action, "");

    public static string TransportInfo(string state) => Response(AvTransport, "GetTransportInfo",
        $"<CurrentTransportState>{state}</CurrentTransportState><CurrentTransportStatus>OK</CurrentTransportStatus><CurrentSpeed>1</CurrentSpeed>");

    public static string PositionInfo(string trackUri, string durationHms, string relTimeHms) => Response(AvTransport, "GetPositionInfo",
        $"<Track>1</Track><TrackDuration>{durationHms}</TrackDuration><TrackMetaData></TrackMetaData>" +
        $"<TrackURI>{Esc(trackUri)}</TrackURI><RelTime>{relTimeHms}</RelTime><AbsTime>{relTimeHms}</AbsTime>" +
        "<RelCount>2147483647</RelCount><AbsCount>2147483647</AbsCount>");

    public static string ProtocolInfo() => Response(ConnectionManager, "GetProtocolInfo",
        "<Source></Source><Sink>http-get:*:video/mp4:*,http-get:*:video/*:*,http-get:*:audio/*:*,http-get:*:image/*:*</Sink>");

    public static string Fault(int code, string reason) =>
        "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
        $"<s:Body><s:Fault><faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring><detail>" +
        $"<UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\"><errorCode>{code}</errorCode><errorDescription>{Esc(reason)}</errorDescription></UPnPError>" +
        "</detail></s:Fault></s:Body></s:Envelope>";

    private static string Response(string service, string action, string inner) =>
        "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
        $"<s:Body><u:{action}Response xmlns:u=\"{service}\">{inner}</u:{action}Response></s:Body></s:Envelope>";

    private static string Action(string name, params (string Name, string Dir, string Related)[] args)
    {
        var sb = new System.Text.StringBuilder($"<action><name>{name}</name><argumentList>");
        foreach (var (n, d, r) in args)
            sb.Append($"<argument><name>{n}</name><direction>{d}</direction><relatedStateVariable>{r}</relatedStateVariable></argument>");
        return sb.Append("</argumentList></action>").ToString();
    }

    private static string StateVar(string name, string type) =>
        $"<stateVariable sendEvents=\"no\"><name>{name}</name><dataType>{type}</dataType></stateVariable>";

    private static string Esc(string s) => SecurityElement.Escape(s) ?? s;
}

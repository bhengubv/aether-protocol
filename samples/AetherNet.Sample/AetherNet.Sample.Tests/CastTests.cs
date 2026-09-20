// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Services.Cast;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// The DLNA/UPnP wire format that lets Aether cast to a bare smart TV — checked byte-for-byte without a
/// TV in the room. The sockets that carry these strings are exercised on-device; the strings themselves
/// are pinned here so a stray character can't silently stop a TV from playing.
/// </summary>
public class CastTests
{
    private const string DeviceXml = """
        <?xml version="1.0"?>
        <root xmlns="urn:schemas-upnp-org:device-1-0">
          <device>
            <friendlyName>Living Room TV</friendlyName>
            <deviceType>urn:schemas-upnp-org:device:MediaRenderer:1</deviceType>
            <serviceList>
              <service>
                <serviceType>urn:schemas-upnp-org:service:RenderingControl:1</serviceType>
                <controlURL>/rc/control</controlURL>
              </service>
              <service>
                <serviceType>urn:schemas-upnp-org:service:AVTransport:1</serviceType>
                <controlURL>/avt/control</controlURL>
              </service>
            </serviceList>
          </device>
        </root>
        """;

    [Fact]
    public void The_search_datagram_is_a_well_formed_M_SEARCH()
    {
        var s = DlnaProtocol.BuildSearch(2);
        Assert.StartsWith("M-SEARCH * HTTP/1.1\r\n", s);
        Assert.Contains("HOST: 239.255.255.250:1900\r\n", s);
        Assert.Contains("MAN: \"ssdp:discover\"\r\n", s);
        Assert.Contains("ST: urn:schemas-upnp-org:device:MediaRenderer:1\r\n", s);
        Assert.EndsWith("\r\n\r\n", s);   // the trailing blank line is part of the format
    }

    [Fact]
    public void Response_headers_parse_case_insensitively()
    {
        var text = "HTTP/1.1 200 OK\r\nLOCATION: http://192.168.0.50:8200/desc.xml\r\nST: upnp:rootdevice\r\n\r\n";
        var headers = DlnaProtocol.ParseHeaders(text);
        Assert.Equal("http://192.168.0.50:8200/desc.xml", headers["location"]);
        Assert.Equal("upnp:rootdevice", headers["St"]);
    }

    [Fact]
    public void The_AVTransport_control_url_is_found_and_made_absolute()
    {
        var url = DlnaProtocol.AvTransportControlUrl(DeviceXml, "http://192.168.0.50:8200/desc.xml");
        Assert.Equal("http://192.168.0.50:8200/avt/control", url);   // the AVTransport one, not RenderingControl
    }

    [Fact]
    public void An_explicit_URLBase_wins_over_the_description_location()
    {
        var xml = DeviceXml.Replace("<device>", "<URLBase>http://10.0.0.9:9000/</URLBase><device>");
        var url = DlnaProtocol.AvTransportControlUrl(xml, "http://192.168.0.50:8200/desc.xml");
        Assert.Equal("http://10.0.0.9:9000/avt/control", url);
    }

    [Fact]
    public void A_device_with_no_AVTransport_is_not_castable()
    {
        var xml = """
            <root><device><friendlyName>Speaker</friendlyName><serviceList>
              <service><serviceType>urn:schemas-upnp-org:service:RenderingControl:1</serviceType><controlURL>/rc</controlURL></service>
            </serviceList></device></root>
            """;
        Assert.Null(DlnaProtocol.AvTransportControlUrl(xml, "http://192.168.0.50:8200/desc.xml"));
    }

    [Fact]
    public void The_friendly_name_is_read_with_a_fallback()
    {
        Assert.Equal("Living Room TV", DlnaProtocol.FriendlyName(DeviceXml));
        Assert.Equal("TV", DlnaProtocol.FriendlyName("<root><device/></root>"));
    }

    [Fact]
    public void Set_uri_carries_the_media_url_and_metadata_and_instance_zero()
    {
        var didl = DlnaProtocol.Didl("My clip", "video/mp4", "http://192.168.0.2:8099/cast/HASH");
        var body = DlnaProtocol.SetAvTransportUri("http://192.168.0.2:8099/cast/HASH", didl);
        Assert.Contains("<u:SetAVTransportURI xmlns:u=\"urn:schemas-upnp-org:service:AVTransport:1\">", body);
        Assert.Contains("<InstanceID>0</InstanceID>", body);
        Assert.Contains("http://192.168.0.2:8099/cast/HASH", body);
        Assert.Contains("&lt;DIDL-Lite", body);   // the metadata is XML-escaped inside the element
    }

    [Fact]
    public void Play_pause_stop_are_well_formed_and_the_action_header_matches()
    {
        Assert.Contains("<u:Play xmlns:u=\"urn:schemas-upnp-org:service:AVTransport:1\">", DlnaProtocol.Play());
        Assert.Contains("<Speed>1</Speed>", DlnaProtocol.Play());
        Assert.Contains("<u:Pause", DlnaProtocol.Pause());
        Assert.Contains("<u:Stop", DlnaProtocol.Stop());
        Assert.Equal("\"urn:schemas-upnp-org:service:AVTransport:1#Play\"", DlnaProtocol.SoapAction("Play"));
    }

    [Fact]
    public void Seek_formats_the_position_as_wall_clock_time()
    {
        var body = DlnaProtocol.Seek((90 * 60 + 5) * 1000);   // 1h30m05s
        Assert.Contains("<Unit>REL_TIME</Unit>", body);
        Assert.Contains("<Target>01:30:05</Target>", body);
    }

    [Fact]
    public void Didl_escapes_a_title_so_it_cannot_break_the_xml()
    {
        var didl = DlnaProtocol.Didl("Tom & Jerry <fun>", "video/mp4", "http://x/y");
        Assert.Contains("Tom &amp; Jerry &lt;fun&gt;", didl);
        Assert.DoesNotContain("<fun>", didl);
    }

    [Fact]
    public void The_status_queries_are_well_formed_soap_with_instance_zero()
    {
        var ti = DlnaProtocol.GetTransportInfo();
        Assert.Contains("<u:GetTransportInfo xmlns:u=\"urn:schemas-upnp-org:service:AVTransport:1\">", ti);
        Assert.Contains("<InstanceID>0</InstanceID>", ti);

        var pi = DlnaProtocol.GetPositionInfo();
        Assert.Contains("<u:GetPositionInfo xmlns:u=\"urn:schemas-upnp-org:service:AVTransport:1\">", pi);
        Assert.Contains("<InstanceID>0</InstanceID>", pi);
    }

    [Fact]
    public void The_transport_state_is_read_from_a_GetTransportInfo_response()
    {
        var xml = """
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body>
              <u:GetTransportInfoResponse xmlns:u="urn:schemas-upnp-org:service:AVTransport:1">
                <CurrentTransportState>PLAYING</CurrentTransportState>
                <CurrentTransportStatus>OK</CurrentTransportStatus>
                <CurrentSpeed>1</CurrentSpeed>
              </u:GetTransportInfoResponse>
            </s:Body></s:Envelope>
            """;
        Assert.Equal("PLAYING", DlnaProtocol.ReadTransportState(xml));
        Assert.Null(DlnaProtocol.ReadTransportState("<garbage"));
    }

    [Fact]
    public void The_position_and_duration_are_read_from_a_GetPositionInfo_response()
    {
        var xml = """
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body>
              <u:GetPositionInfoResponse xmlns:u="urn:schemas-upnp-org:service:AVTransport:1">
                <Track>1</Track>
                <TrackDuration>0:05:00</TrackDuration>
                <RelTime>0:00:37</RelTime>
              </u:GetPositionInfoResponse>
            </s:Body></s:Envelope>
            """;
        var (pos, dur) = DlnaProtocol.ReadPosition(xml);
        Assert.Equal(37_000, pos);
        Assert.Equal(300_000, dur);
    }

    [Theory]
    [InlineData("0:00:37", 37_000)]
    [InlineData("01:30:05", (90 * 60 + 5) * 1000)]
    [InlineData("0:00:05.000", 5_000)]   // some renderers carry a fraction of a second
    [InlineData("NOT_IMPLEMENTED", 0)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    public void A_upnp_clock_parses_to_milliseconds(string? clock, long expectedMs)
        => Assert.Equal(expectedMs, DlnaProtocol.ParseClock(clock));
}

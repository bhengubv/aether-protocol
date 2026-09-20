// SPDX-License-Identifier: MIT

using System.Xml.Linq;
using AetherNet.Sample.Shared.Services.Cast;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// The strings Aether serves as a UPnP MediaRenderer — so any caster can send it a video. Checked
/// without another device: the device description parses and names the right control endpoint, the SSDP
/// replies are well-formed, and a SetAVTransportURI from the sender round-trips back to the URL + title.
/// </summary>
public class UpnpRendererTests
{
    [Fact]
    public void The_device_description_is_valid_xml_and_names_the_AVTransport_control()
    {
        var udn = UpnpRenderer.Udn("KXJB7-MN2P4");
        var xml = UpnpRenderer.DeviceDescription("Aether — Redmi 12", udn);

        var doc = XDocument.Parse(xml);   // throws if malformed
        Assert.Contains("urn:schemas-upnp-org:device:MediaRenderer:1", xml);
        Assert.Contains("Aether — Redmi 12", xml);
        Assert.Contains($"uuid:{udn}", xml);
        // A caster (our own sender) must be able to find the AVTransport control URL in it.
        Assert.Equal("http://10.0.0.5:8098/avt/control",
            DlnaProtocol.AvTransportControlUrl(xml, "http://10.0.0.5:8098/desc.xml"));
    }

    [Fact]
    public void The_udn_is_stable_per_device_and_distinct_across_devices()
    {
        Assert.Equal(UpnpRenderer.Udn("TAG-AAAAA"), UpnpRenderer.Udn("TAG-AAAAA"));
        Assert.NotEqual(UpnpRenderer.Udn("TAG-AAAAA"), UpnpRenderer.Udn("TAG-BBBBB"));
    }

    [Fact]
    public void The_service_descriptions_are_valid_xml()
    {
        Assert.NotNull(XDocument.Parse(UpnpRenderer.AvTransportScpd()));
        Assert.NotNull(XDocument.Parse(UpnpRenderer.ConnectionManagerScpd()));
    }

    [Fact]
    public void A_set_uri_from_the_sender_round_trips_to_the_url_and_title()
    {
        // Exactly what DlnaCastService sends: an ephemeral header is irrelevant here; the DIDL carries the title.
        var mediaUrl = "http://192.168.0.115:8099/cast/ABC123";
        var didl = DlnaProtocol.Didl("Holiday clip", "video/mp4", mediaUrl);
        var body = DlnaProtocol.SetAvTransportUri(mediaUrl, didl);

        var (uri, title) = UpnpRenderer.ReadSetUri(body);
        Assert.Equal(mediaUrl, uri);
        Assert.Equal("Holiday clip", title);
    }

    [Fact]
    public void The_soap_action_name_is_read_from_the_header()
    {
        Assert.Equal("Play", UpnpRenderer.ActionOf("\"urn:schemas-upnp-org:service:AVTransport:1#Play\""));
        Assert.Null(UpnpRenderer.ActionOf(null));
        Assert.Null(UpnpRenderer.ActionOf("garbage-without-a-hash"));
    }

    [Theory]
    [InlineData("ssdp:all", true)]
    [InlineData("upnp:rootdevice", true)]
    [InlineData("urn:schemas-upnp-org:device:MediaRenderer:1", true)]
    [InlineData("urn:schemas-upnp-org:service:AVTransport:1", true)]
    [InlineData("uuid:something", true)]
    [InlineData("urn:schemas-upnp-org:device:MediaServer:1", false)]
    [InlineData("urn:dial-multiscreen-org:service:dial:1", false)]
    public void It_answers_only_searches_a_renderer_should(string st, bool answers)
        => Assert.Equal(answers, UpnpRenderer.Answers(st));

    [Fact]
    public void A_search_response_is_a_well_formed_200_with_location_st_and_usn()
    {
        var r = UpnpRenderer.SearchResponse("http://10.0.0.5:8098/desc.xml",
            "urn:schemas-upnp-org:device:MediaRenderer:1", "uuid:x::urn:schemas-upnp-org:device:MediaRenderer:1");
        Assert.StartsWith("HTTP/1.1 200 OK\r\n", r);
        Assert.Contains("LOCATION: http://10.0.0.5:8098/desc.xml\r\n", r);
        Assert.Contains("ST: urn:schemas-upnp-org:device:MediaRenderer:1\r\n", r);
        Assert.Contains("USN: uuid:x::urn:schemas-upnp-org:device:MediaRenderer:1\r\n", r);
        Assert.EndsWith("\r\n\r\n", r);
    }

    [Fact]
    public void A_media_renderer_advertises_root_device_uuid_type_and_each_service()
    {
        var udn = UpnpRenderer.Udn("TAG-AAAAA");
        var ads = UpnpRenderer.Advertisements(udn);
        Assert.Contains(ads, a => a.Nt == "upnp:rootdevice" && a.Usn == $"uuid:{udn}::upnp:rootdevice");
        Assert.Contains(ads, a => a.Nt == $"uuid:{udn}" && a.Usn == $"uuid:{udn}");
        Assert.Contains(ads, a => a.Nt.Contains("MediaRenderer"));
        Assert.Contains(ads, a => a.Nt.Contains("AVTransport"));
    }

    [Fact]
    public void Transport_and_position_responses_are_valid_soap()
    {
        var ti = UpnpRenderer.TransportInfo("PLAYING");
        Assert.NotNull(XDocument.Parse(ti));
        Assert.Contains("<CurrentTransportState>PLAYING</CurrentTransportState>", ti);

        var pi = UpnpRenderer.PositionInfo("http://x/y", "00:03:20", "00:01:05");
        Assert.NotNull(XDocument.Parse(pi));
        Assert.Contains("<RelTime>00:01:05</RelTime>", pi);
    }
}

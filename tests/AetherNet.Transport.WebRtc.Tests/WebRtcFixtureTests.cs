// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace AetherNet.Transport.WebRtc.Tests;

/// <summary>
/// Cross-language WebRTC signalling-frame wire-format verifier. Reads
/// fixtures/webrtc/inputs.json + expected/*.bin (the C#-reference output committed to the repo)
/// and asserts this language's <see cref="RelayWebRtcSignaling.Frame"/> produces the same bytes for
/// each canonical input and that <see cref="RelayWebRtcSignaling.Deframe"/> round-trips every field.
/// Every other language SDK runs an equivalent test against the same .bin — that is the parity gate.
/// </summary>
public class WebRtcFixtureTests
{
    private record WebRtcFixtureInput(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("from_uhid")] string? FromUhid,
        [property: JsonPropertyName("to_uhid")] string? ToUhid,
        [property: JsonPropertyName("type")] int Type,
        [property: JsonPropertyName("sdp")] string? Sdp,
        [property: JsonPropertyName("candidate")] string? Candidate,
        [property: JsonPropertyName("sdp_mid")] string? SdpMid,
        [property: JsonPropertyName("sdp_mline_index")] ushort SdpMLineIndex);

    // Empty sdp/candidate/sdp_mid means the field is omitted from the frame (WhenWritingNull),
    // so normalise absent/empty strings to null to mirror the fixture exactly.
    private static string? OrNull(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static string FixturesDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir, "fixtures", "webrtc", "inputs.json");
            if (File.Exists(candidate)) return Path.Combine(dir, "fixtures", "webrtc");
            var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (parent is null || parent == dir) break;
            dir = parent;
        }
        throw new FileNotFoundException("Could not locate fixtures/webrtc/inputs.json from " + AppContext.BaseDirectory);
    }

    private static List<WebRtcFixtureInput> LoadInputs()
    {
        var path = Path.Combine(FixturesDir(), "inputs.json");
        return JsonSerializer.Deserialize<List<WebRtcFixtureInput>>(File.ReadAllText(path))!;
    }

    public static IEnumerable<object[]> AllFixtures() =>
        LoadInputs().Select(x => new object[] { x.Name });

    private static WebRtcSignal ToSignal(WebRtcFixtureInput i) => new()
    {
        FromUhid = i.FromUhid ?? string.Empty,
        ToUhid = i.ToUhid ?? string.Empty,
        Type = (WebRtcSignalType)i.Type,
        Sdp = OrNull(i.Sdp),
        Candidate = OrNull(i.Candidate),
        SdpMid = OrNull(i.SdpMid),
        SdpMLineIndex = i.SdpMLineIndex,
    };

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Frame_MatchesExpectedBytes(string name)
    {
        var input = LoadInputs().Single(x => x.Name == name);
        var framed = RelayWebRtcSignaling.Frame(ToSignal(input));
        var expected = File.ReadAllBytes(Path.Combine(FixturesDir(), "expected", name + ".bin"));
        Assert.Equal(expected, framed);
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Deframe_FromExpectedBytes_MatchesInputFields(string name)
    {
        var input = LoadInputs().Single(x => x.Name == name);
        var bytes = File.ReadAllBytes(Path.Combine(FixturesDir(), "expected", name + ".bin"));
        var s = RelayWebRtcSignaling.Deframe(bytes);

        Assert.NotNull(s);
        Assert.Equal(input.FromUhid ?? string.Empty, s!.FromUhid);
        Assert.Equal(input.ToUhid ?? string.Empty, s.ToUhid);
        Assert.Equal((WebRtcSignalType)input.Type, s.Type);
        Assert.Equal(OrNull(input.Sdp), s.Sdp);
        Assert.Equal(OrNull(input.Candidate), s.Candidate);
        Assert.Equal(OrNull(input.SdpMid), s.SdpMid);
        Assert.Equal(input.SdpMLineIndex, s.SdpMLineIndex);
    }
}

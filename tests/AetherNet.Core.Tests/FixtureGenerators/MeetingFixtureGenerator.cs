// SPDX-License-Identifier: MIT
//
// Deterministic cross-language fixture generator + self-check for the rendezvous derivation that every
// language port (Go / Python / Rust / Swift / Kotlin / TS / C / HarmonyOS) must reproduce byte-for-byte:
//
//   Meeting  — AetherNet.Rendezvous.Meeting.With() + .Uuid() + .Address()  -> fixtures/meeting/meeting_basic.json
//
// SAME PATTERN as CrossLangFixtureGenerator (fixtures/tipping, /vault, /market) and fixtures/signal, /erid:
// the C# reference implementation is the single source of truth; this runs it against FIXED, adversarial
// inputs, emits the expected values as JSON (hex for bytes), then re-reads its own output and verifies the
// reference re-derives it — plus the two algebraic invariants that make a rendezvous a rendezvous (both
// orderings of a pair land on the SAME meeting-point, with OPPOSITE host roles). Keep this in the tree so
// the fixtures can be regenerated whenever the reference intentionally changes (a wire-break event — every
// language port must then be re-verified against the new bytes).
//
// Run (writes the file AND runs the self-check — both must stay green):
//   dotnet test tests/AetherNet.Core.Tests/AetherNet.Core.Tests.csproj -c Debug --nologo \
//     --filter "FullyQualifiedName~MeetingFixtureGenerator"

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using AetherNet.Rendezvous;   // Meeting, GroupRole (public, AetherNet.Core)
using Xunit;

namespace AetherNet.Core.Tests.FixtureGenerators;

/// <summary>
/// One-shot generator for the cross-language rendezvous-derivation parity fixture, plus an in-process
/// self-check that the C# reference reads its own emitted fixture back and re-derives it exactly.
/// </summary>
public sealed class MeetingFixtureGenerator
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>The bit-widths of <see cref="Meeting.Address(int)"/> pinned by every case.</summary>
    private static readonly int[] AddressBits = [8, 16, 24, 32];

    /// <summary>
    /// Fixed, adversarial input pairs. Real AetherTags are Crockford base32, but the derivation is defined
    /// on arbitrary UTF-8, and a port that gets UTF-8 encoding, ordinal ordering or length handling wrong
    /// must fail here rather than in the field.
    /// </summary>
    private static readonly (string Name, string My, string Their)[] Inputs =
    [
        // The plain case: two ordinary tags, this phone's tag ordinally lower (so it hosts).
        ("basic_ascii", "BH8CZ-B09CA", "DY5CF-84G9T"),
        // The SAME pair, handed to the derivation the other way round. Rendezvous / uuid / address must be
        // identical to basic_ascii; i_start must flip. This is the property the ordering exists to give.
        ("basic_ascii_swapped", "DY5CF-84G9T", "BH8CZ-B09CA"),
        // Ordinal ordering, not lexical-by-category: a digit ('9' = 0x39) sorts BEFORE a letter
        // ('A' = 0x41). A port that sorts letters ahead of digits orders the pair the wrong way into the
        // KDF and lands on a different rendezvous.
        ("digit_before_letter", "9ZZZZ-ZZZZZ", "A0000-00000"),
        // Non-ASCII: multi-byte UTF-8 either side of the '\n' separator. Pins the encoding, not a real tag.
        ("unicode_utf8", "café-Ω", "naïve-🜁"),
        // Longer than a rendezvous: the output is still exactly Meeting.Length characters.
        ("long_tags", "this-is-a-deliberately-long-tag-000", "this-is-a-deliberately-long-tag-001"),
        // All digits, adjacent by a single trailing character — the tightest ordinal decision.
        ("numeric_adjacent", "00000-00000", "00000-00001"),
    ];

    /// <summary>
    /// Input pairs that MUST NOT produce a meeting. A port that derives a rendezvous for any of these has
    /// a hole: two case-variants are the same identity, a phone does not meet itself, and a missing tag is
    /// nothing to meet.
    /// </summary>
    private static readonly (string Name, string? My, string? Their)[] Rejects =
    [
        ("identical",     "Q7WER-TY123", "Q7WER-TY123"),   // a phone does not meet itself
        ("case_variant",  "ABCDE-FGHIJ", "ABCDE-fghij"),   // same identity — tags are case-insensitive
        ("empty_their",   "BH8CZ-B09CA", ""),              // nothing on the other side
        ("whitespace_my", "   ",         "DY5CF-84G9T"),   // nor on this one
        ("null_their",    "BH8CZ-B09CA", null),            // absent entirely
    ];

    [Fact]
    public void Generate_And_SelfCheck()
    {
        var path = Path.Combine(FindFixturesRoot(), "meeting", "meeting_basic.json");

        WriteMeetingFixture(path);
        VerifyMeetingFixture(path);
    }

    private static void WriteMeetingFixture(string path)
    {
        var cases = Inputs.Select(inp =>
        {
            var meeting = Meeting.With(inp.My, inp.Their)
                ?? throw new InvalidOperationException($"Fixture input '{inp.Name}' produced no meeting.");

            return new
            {
                name       = inp.Name,
                my_tag     = inp.My,
                their_tag  = inp.Their,
                rendezvous = meeting.Rendezvous,
                i_start    = meeting.IStart,
                uuid       = Hex(meeting.Uuid().ToByteArray()),   // .NET mixed-endian layout — pinned deliberately
                uuid_string = meeting.Uuid().ToString("D"),
                // One entry per pinned bit-width: { "8": n, "16": n, ... }.
                address    = AddressBits.ToDictionary(b => b.ToString(), b => meeting.Address(b)),
            };
        }).ToArray();

        var fixture = new
        {
            note = "Canonical rendezvous-derivation parity vectors from the C# reference "
                 + "(AetherNet.Rendezvous.Meeting). Order the two tags by ordinal comparison into (first, "
                 + "second); rendezvous = CrockfordBase32( HKDF-SHA256( ikm=UTF8(first + '\\n' + second), "
                 + "salt=empty, info=UTF8('aether-meeting-v1'), L=16 ) )[..25]. i_start = the ordinally-lower "
                 + "tag hosts (this phone starts when its own tag is the lower one). uuid = "
                 + "SHA256(UTF8('aether-meeting-v1-uuid\\n' + rendezvous))[..16] with byte[7]=(b&0x0F)|0x40 and "
                 + "byte[8]=(b&0x3F)|0x80, read as a .NET Guid (uuid is Guid.ToByteArray() — bytes 0..3, 4..5, "
                 + "6..7 are little-endian). address[n] = SHA256(UTF8('aether-meeting-v1-addr\\n' + rendezvous)) "
                 + "read big-endian u32, masked to the low n bits. Every language port MUST reproduce "
                 + "rendezvous, i_start, uuid and every address byte-for-byte.",
            info      = "aether-meeting-v1",
            alphabet  = "0123456789ABCDEFGHJKMNPQRSTVWXYZ",
            length    = Meeting.Length,
            address_bits = AddressBits,
            cases,
            // Inputs that MUST yield no meeting. A port has to reject exactly these.
            rejects = Rejects.Select(r => new { name = r.Name, my_tag = r.My, their_tag = r.Their }).ToArray(),
        };

        WriteJson(path, fixture);
    }

    private static void VerifyMeetingFixture(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        Assert.Equal("aether-meeting-v1", root.GetProperty("info").GetString());
        Assert.Equal(Meeting.Length, root.GetProperty("length").GetInt32());

        // Keep the recorded rendezvous of each case by name, to assert the swapped-pair invariant after.
        var byName = new System.Collections.Generic.Dictionary<string, (string Rv, bool Start, string Uuid)>(StringComparer.Ordinal);

        foreach (var c in root.GetProperty("cases").EnumerateArray())
        {
            var name  = c.GetProperty("name").GetString()!;
            var my    = c.GetProperty("my_tag").GetString()!;
            var their = c.GetProperty("their_tag").GetString()!;

            var meeting = Meeting.With(my, their)
                ?? throw new InvalidOperationException($"Case '{name}' produced no meeting.");

            // (a) the reference re-derives every recorded field
            Assert.Equal(c.GetProperty("rendezvous").GetString(), meeting.Rendezvous);
            Assert.Equal(c.GetProperty("i_start").GetBoolean(), meeting.IStart);
            Assert.Equal(c.GetProperty("uuid").GetString(), Hex(meeting.Uuid().ToByteArray()));
            Assert.Equal(c.GetProperty("uuid_string").GetString(), meeting.Uuid().ToString("D"));
            foreach (var bits in AddressBits)
                Assert.Equal(c.GetProperty("address").GetProperty(bits.ToString()).GetUInt32(), meeting.Address(bits));

            // (b) shape: a rendezvous is exactly Length characters, all drawn from the Crockford alphabet
            Assert.Equal(Meeting.Length, meeting.Rendezvous.Length);
            Assert.All(meeting.Rendezvous, ch =>
                Assert.Contains(ch, "0123456789ABCDEFGHJKMNPQRSTVWXYZ"));

            byName[name] = (meeting.Rendezvous, meeting.IStart, Hex(meeting.Uuid().ToByteArray()));
        }

        // The invariant the whole ordering exists for: the same pair, fed either way round, meets at the
        // same place with opposite host roles.
        var a = byName["basic_ascii"];
        var b = byName["basic_ascii_swapped"];
        Assert.Equal(a.Rv, b.Rv);
        Assert.Equal(a.Uuid, b.Uuid);
        Assert.NotEqual(a.Start, b.Start);

        // Every rejected input re-reads as null — the reference refuses exactly what the fixture says ports must.
        foreach (var r in root.GetProperty("rejects").EnumerateArray())
        {
            var my    = r.GetProperty("my_tag");
            var their = r.GetProperty("their_tag");
            var myTag    = my.ValueKind == JsonValueKind.Null ? null : my.GetString();
            var theirTag = their.ValueKind == JsonValueKind.Null ? null : their.GetString();
            Assert.Null(Meeting.With(myTag, theirTag));
        }
    }

    // ── shared IO (same helpers as CrossLangFixtureGenerator) ─────────────────────────────────────────

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    private static void WriteJson(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOpts));
    }

    /// <summary>
    /// Locate the repo's <c>fixtures/</c> directory by walking up from the test bin directory, anchored on
    /// the known sibling <c>fixtures/signal</c> so a stray "fixtures" elsewhere can't match.
    /// </summary>
    private static string FindFixturesRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "fixtures");
            if (Directory.Exists(candidate) && Directory.Exists(Path.Combine(candidate, "signal")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate the repo 'fixtures' directory (with a 'signal' subdirectory) above " +
            AppContext.BaseDirectory + ". Run from within the aether-protocol working tree.");
    }
}

// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// What you are looking at, given to the phone you just touched.
///
/// <para>
/// Two people who have added each other touch phones, and the thing that was open on one is open on
/// the other — in the same place, still live. No transfer bar, no "sending…", no accept. There is
/// nothing to ask permission for between two people who already chose each other, and asking would
/// turn a gesture into a form.
/// </para>
///
/// <para>
/// <b>What crosses is a description, not a copy.</b> The receiving phone already has Aether, already
/// has the mesh, and can already reach whatever this names. So a handoff is a few dozen bytes saying
/// <em>where to stand</em>, and the phone walks there itself. Sending the content would be slower,
/// larger, and would go stale the moment it arrived.
/// </para>
///
/// <para>
/// That is also why this is a different problem from giving somebody the app. There, the far side has
/// nothing and must be persuaded. Here, both ends are ours.
/// </para>
/// </summary>
public static class Handoff
{
    /// <summary>"Here is what I am holding." Rides <c>PacketType.Data</c>, like every other envelope here.</summary>
    public const string Marker = "AETHERHOF";

    /// <summary>
    /// "I just touched your phone — what have you got?"
    /// </summary>
    /// <remarks>
    /// The tap is one-way: one phone is a tag and the other is a reader, so only the reader learns who
    /// it touched. It therefore has to speak first. That is why this exists and why the gesture still
    /// reads as giving even though the asking runs the other way.
    /// </remarks>
    public const string WantMarker = "AETHERHOW";

    /// <summary>
    /// The wire version.
    /// </summary>
    /// <remarks>
    /// A phone running an older build must ignore a handoff it does not understand rather than guess
    /// at it — landing somebody on the wrong screen is worse than landing them nowhere.
    /// </remarks>
    public const int Version = 1;

    /// <summary>The kinds of thing that can be handed over.</summary>
    /// <remarks>
    /// Deliberately a small closed set. Anything a phone can be told to open by name belongs here; a
    /// screen whose state cannot be named in a few bytes does not, because then this stops being a
    /// gesture and becomes a file transfer.
    /// </remarks>
    public enum Kind
    {
        /// <summary>Unknown to this build. Ignored.</summary>
        Unknown = 0,

        /// <summary>A page on the mesh-web, by its <c>aether://</c> address.</summary>
        Card = 1,

        /// <summary>A conversation, by the other person's AetherTag.</summary>
        Chat = 2,
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>One handed-over thing, and where you were in it.</summary>
    /// <param name="V">Wire version.</param>
    /// <param name="Kind">What it is.</param>
    /// <param name="Target">Which one — an address, or a tag.</param>
    /// <param name="Draft">
    ///   What you had typed and not sent. This is the part that makes it feel alive: you are
    ///   mid-sentence, you touch a phone, and the sentence goes with it. Null when there was nothing
    ///   half-written.
    /// </param>
    /// <param name="At">
    ///   How far down you were, as a fraction of the whole rather than a pixel count — the other
    ///   phone is a different size, and pixels would land somebody somewhere else entirely.
    /// </param>
    public sealed record Note(int V, Kind Kind, string Target, string? Draft = null, double? At = null);

    /// <summary>
    /// The longest half-written message that will travel.
    /// </summary>
    /// <remarks>
    /// A handoff has to stay a gesture. Past a certain size this stops being "where you were" and
    /// becomes a file transfer wearing a tap, so a very long draft is left behind rather than allowed
    /// to turn one thing into another.
    /// </remarks>
    public const int LongestDraft = 2000;

    /// <summary>
    /// Describe what is on screen, or null when this screen is not something worth handing over.
    /// </summary>
    /// <remarks>
    /// Null is a normal answer. Most screens are not a place — a settings list or a wizard step is
    /// somewhere you are passing through, and putting somebody else's phone there would be a rude
    /// non-sequitur rather than a gift.
    /// </remarks>
    /// <param name="draft">What is typed and unsent, if anything.</param>
    /// <param name="at">How far down the screen is scrolled, 0 to 1.</param>
    public static Note? Describe(string? route, string? draft = null, double? at = null)
    {
        if (string.IsNullOrWhiteSpace(route)) return null;

        var path = route.Trim();
        var query = "";

        if (path.IndexOf('?', StringComparison.Ordinal) is var q and >= 0)
        {
            query = path[(q + 1)..];
            path = path[..q];
        }

        path = path.TrimEnd('/');

        // A conversation: /chat/{tag}
        if (path.StartsWith("/chat/", StringComparison.OrdinalIgnoreCase))
        {
            var tag = path["/chat/".Length..];
            return string.IsNullOrWhiteSpace(tag)
                ? null
                : new Note(Version, Kind.Chat, Uri.UnescapeDataString(tag), Trim(draft), Fraction(at));
        }

        // A page on the mesh-web: /meshweb?a=aether://…
        if (path.Equals("/meshweb", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!pair.StartsWith("a=", StringComparison.Ordinal)) continue;
                var address = Uri.UnescapeDataString(pair[2..]);
                // A card has nothing to type into, so a draft here would be somebody else's state
                // leaking across screens.
                return string.IsNullOrWhiteSpace(address)
                    ? null
                    : new Note(Version, Kind.Card, address, null, Fraction(at));
            }

            // The mesh-web with nothing open is not a place.
            return null;
        }

        return null;
    }

    /// <summary>Where a phone should stand when it receives this, or null when it cannot.</summary>
    public static string? RouteFor(Note? note)
    {
        if (note is null) return null;
        if (note.V > Version) return null;                          // from a newer build; do not guess
        if (string.IsNullOrWhiteSpace(note.Target)) return null;

        return note.Kind switch
        {
            Kind.Chat => "/chat/" + Uri.EscapeDataString(note.Target),
            Kind.Card => "/meshweb?a=" + Uri.EscapeDataString(note.Target),
            _ => null,                                              // a kind this build does not know
        };
    }

    /// <summary>A draft worth carrying, or null.</summary>
    private static string? Trim(string? draft)
    {
        if (string.IsNullOrWhiteSpace(draft)) return null;
        return draft.Length > LongestDraft ? null : draft;
    }

    /// <summary>A scroll position that means the same thing on a screen of a different size.</summary>
    private static double? Fraction(double? at) =>
        at is not { } value || double.IsNaN(value) || double.IsInfinity(value)
            ? null
            : Math.Clamp(value, 0, 1);

    /// <summary>
    /// The note, as bytes to be sealed.
    /// </summary>
    /// <remarks>
    /// Just the body. The marker goes on the outside of the ciphertext where the receiving phone can
    /// sort on it without opening anything, which is how every other envelope on this wire works.
    /// </remarks>
    public static byte[] Encode(Note note)
    {
        ArgumentNullException.ThrowIfNull(note);
        return JsonSerializer.SerializeToUtf8Bytes(note, Json);
    }

    /// <summary>
    /// Read a handoff off the wire, or null when this payload is not one.
    /// </summary>
    /// <remarks>
    /// Everything arriving here has already been through a session, so it came from somebody this
    /// phone added — but "from a friend" is not the same as "well formed", and a build talking to a
    /// newer one will meet shapes it has never seen.
    /// </remarks>
    public static Note? Decode(byte[]? body)
    {
        if (body is null || body.Length == 0) return null;

        try
        {
            var note = JsonSerializer.Deserialize<Note>(body, Json);
            if (note is null || note.V <= 0 || string.IsNullOrWhiteSpace(note.Target)) return null;
            return note;
        }
        catch (JsonException) { return null; }
    }
}

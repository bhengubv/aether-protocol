// SPDX-License-Identifier: MIT

using System.Globalization;
using AetherNet.Sample.Shared.Data;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// The few bytes on the wire that say "this message is meant to be seen and then gone".
///
/// <para>
/// It carries only the RULE the sender chose — view once, view twice, or a timeout window — so the
/// receiver's phone can enforce the same limit the sender intended. How many times THIS phone has
/// opened it, and when its clock started, are the receiver's own business and never travel; they live
/// only in that device's store.
/// </para>
///
/// <para>
/// The encoding mirrors <see cref="AttachmentRef"/> exactly, but opens with a DIFFERENT control
/// character (<see cref="Start"/> = U+0002, vs U+0001) so the two headers compose without ambiguity:
/// an ephemeral note is an ephemeral header wrapped around an attachment header wrapped around the
/// caption. A message with no ephemeral rule goes out byte-for-byte as it always did.
/// </para>
/// </summary>
/// <param name="Kind">One of <see cref="ChatMessage.EphNone"/> / <c>EphOnce</c> / <c>EphTwice</c> / <c>EphTimeout</c>.</param>
/// <param name="WindowMs">For a timeout message, the window once opened (≤ 5 min); 0 otherwise.</param>
public sealed record EphemeralRef(int Kind, long WindowMs)
{
    /// <summary>Opens and closes the header. U+0002 — a control character no message box will ever produce.</summary>
    public const char Start = '';

    /// <summary>Separates the two fields. U+001F, the unit separator, as in <see cref="AttachmentRef"/>.</summary>
    public const char Field = '';

    /// <summary>Put the ephemeral header in front of whatever the rest of the body is (attachment header, or caption).</summary>
    public string Encode(string rest = "")
        => $"{Start}{Kind.ToString(CultureInfo.InvariantCulture)}{Field}{WindowMs.ToString(CultureInfo.InvariantCulture)}{Start}{rest}";

    /// <summary>
    /// Read a body that arrived from another phone. Anything that is not a well-formed ephemeral header
    /// comes back as (null, body) — an ordinary, permanent message — never as a half-read one.
    /// </summary>
    public static (EphemeralRef? Ephemeral, string Remainder) Decode(string body)
    {
        if (string.IsNullOrEmpty(body) || body[0] != Start) return (null, body);

        var end = body.IndexOf(Start, 1);
        if (end < 0) return (null, string.Empty);

        var parts = body[1..end].Split(Field);
        if (parts.Length != 2) return (null, string.Empty);

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kind)) return (null, string.Empty);
        if (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var windowMs)) return (null, string.Empty);
        if (kind is < ChatMessage.EphNone or > ChatMessage.EphTimeout) return (null, string.Empty);

        // A timeout window is clamped to the promised ceiling on the way IN too, so a peer cannot ask
        // this phone to hold a "5-minute" message for an hour.
        if (windowMs < 0) windowMs = 0;
        if (windowMs > ChatMessage.EphemeralMaxWindowMs) windowMs = ChatMessage.EphemeralMaxWindowMs;

        return (new EphemeralRef(kind, windowMs), body[(end + 1)..]);
    }

    /// <summary>Take the marker out of text somebody typed, so a caption can never be mistaken for a header.</summary>
    public static string Clean(string text) => string.IsNullOrEmpty(text)
        ? text
        : text.Replace(Start.ToString(), string.Empty, StringComparison.Ordinal);
}

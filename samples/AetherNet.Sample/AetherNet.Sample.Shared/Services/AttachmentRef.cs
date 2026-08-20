// SPDX-License-Identifier: MIT

using System.Globalization;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// The few bytes on the wire that say "this message has a voice note on it".
///
/// <para>
/// The note itself does not travel here. This names it — content hash, kind, size — and the bytes
/// follow separately through <see cref="AttachmentService"/>, in chunks, resumably. That separation
/// is the whole point: the message appears in the conversation the instant it is recorded, and a
/// forty-second note crossing a slow radio does not hold up the words that came with it.
/// </para>
///
/// <para>
/// The encoding is deliberately additive. A message with no attachment goes out exactly as it always
/// did — the same bytes, byte for byte — so nothing about plain text changes, and only the new
/// feature depends on the new parse. A header starts with <see cref="Start"/>, which is a control
/// character no one can type into a message box and which this strips from outgoing text anyway.
/// </para>
/// </summary>
/// <param name="Hash">Content hash of the note, as it is known to the content store.</param>
/// <param name="ContentType">What kind of thing it is — <c>audio/opus</c>, <c>video/mp4</c>.</param>
/// <param name="Bytes">How big it is, so the far end can draw a real progress bar from the first frame.</param>
public sealed record AttachmentRef(string Hash, string ContentType, long Bytes)
{
    /// <summary>Opens and closes the header. U+0001, which no message box will ever produce.</summary>
    public const char Start = '\u0001';

    /// <summary>Separates the three fields. U+001F is the unit separator, and means exactly this.</summary>
    public const char Field = '\u001F';

    /// <summary>
    /// Put the header in front of the caption. A message may carry a note and words together, and the
    /// caption is simply whatever follows the header.
    /// </summary>
    public string Encode(string caption = "")
        => $"{Start}{Hash}{Field}{ContentType}{Field}{Bytes.ToString(CultureInfo.InvariantCulture)}{Start}{caption}";

    /// <summary>
    /// Read a body that arrived from another phone.
    ///
    /// <para>
    /// Anything that is not a well-formed header comes back as plain text with no attachment, never as
    /// a half-read one. This is parsing input from a radio: a body that begins with the marker but
    /// does not finish it is a corrupt message, and showing its raw innards as if they were somebody's
    /// words is worse than showing nothing.
    /// </para>
    /// </summary>
    public static (AttachmentRef? Attachment, string Caption) Decode(string body)
    {
        if (string.IsNullOrEmpty(body) || body[0] != Start) return (null, body);

        var end = body.IndexOf(Start, 1);
        if (end < 0) return (null, string.Empty);

        var parts = body[1..end].Split(Field);
        if (parts.Length != 3) return (null, string.Empty);

        var hash = parts[0];
        var contentType = parts[1];

        if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(contentType)) return (null, string.Empty);
        if (!long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var bytes)) return (null, string.Empty);

        return (new AttachmentRef(hash, contentType, bytes), body[(end + 1)..]);
    }

    /// <summary>
    /// Take the marker out of text somebody typed, so a caption can never be mistaken for a header.
    /// Cheap, and it means the parse above can trust its first character absolutely.
    /// </summary>
    public static string Clean(string text) => string.IsNullOrEmpty(text)
        ? text
        : text.Replace(Start.ToString(), string.Empty, StringComparison.Ordinal);
}

// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Services;

// MAUI's implicit global usings pull in Microsoft.Maui.Storage, which ALSO declares an IFilePicker —
// so the bare name is ambiguous. This alias pins it to ours; MAUI's static FilePicker is fully
// qualified where it is used, so nothing here reaches for MAUI's interface at all.
using IFilePicker = AetherNet.Sample.Shared.Services.IFilePicker;

namespace AetherNet.Sample;

/// <summary>
/// Picks a file with the phone's own chooser — MAUI's cross-platform FilePicker beneath. Whatever the
/// person taps comes back as bytes, a type and its own name, ready to send like any note.
/// </summary>
public sealed class MauiFilePicker : IFilePicker
{
    /// <summary>How big a single share may be, so reading it whole cannot take the phone down.</summary>
    /// <remarks>
    /// The transfer underneath chunks anything, but the picker reads the file into memory to hand it
    /// over, and a low-end phone has little to spare. A photo or a document is comfortably under this;
    /// something the size of a film is a job for a different flow than "tap and send".
    /// </remarks>
    private const long MaxBytes = 64L * 1024 * 1024;

    public bool CanPick => true;

    public async Task<PickedFile?> PickAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await Microsoft.Maui.Storage.FilePicker.Default.PickAsync().ConfigureAwait(false);
            if (result is null) return null;   // backed out of the chooser

            await using var stream = await result.OpenReadAsync().ConfigureAwait(false);
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
            var bytes = memory.ToArray();

            if (bytes.Length == 0 || bytes.Length > MaxBytes) return null;

            var name = string.IsNullOrWhiteSpace(result.FileName) ? "file" : result.FileName;
            var type = !string.IsNullOrWhiteSpace(result.ContentType)
                ? result.ContentType!
                : GuessType(name);

            return new PickedFile(bytes, type, name);
        }
        catch (Exception)
        {
            // A chooser that will not open, or a file that will not read, is a no-op — the person can
            // try again. It must never take the conversation down with it.
            return null;
        }
    }

    /// <summary>A type from the file's own name, for the platforms whose chooser does not report one.</summary>
    private static string GuessType(string name)
    {
        var dot = name.LastIndexOf('.');
        var ext = dot >= 0 ? name[(dot + 1)..].ToLowerInvariant() : "";
        return ext switch
        {
            "jpg" or "jpeg" => "image/jpeg",
            "png" => "image/png",
            "gif" => "image/gif",
            "webp" => "image/webp",
            "heic" or "heif" => "image/heic",
            "pdf" => "application/pdf",
            "mp4" or "m4v" => "video/mp4",
            "mov" => "video/quicktime",
            "mp3" => "audio/mpeg",
            "m4a" => "audio/mp4",
            "ogg" or "opus" => "audio/ogg",
            "txt" => "text/plain",
            "zip" => "application/zip",
            "doc" or "docx" => "application/msword",
            _ => "application/octet-stream",
        };
    }
}

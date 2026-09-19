// SPDX-License-Identifier: MIT

using System.IO;
using System.Linq;
using AetherNet.Sample.Shared.Services;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Storage;

namespace AetherNet.Sample;

/// <summary>
/// Hands the AetherTag to another app through the phone's own share sheet — MAUI's cross-platform
/// <see cref="Share"/> beneath, so whatever the person picks (WhatsApp, txtMe!, Instagram, …) receives
/// it. A link goes as text — tap it and Aether opens on "add you"; the QR goes as a PNG written to the
/// cache, so a post or a chat can carry the image.
/// </summary>
public sealed class MauiTagShare : ITagShare
{
    public bool CanShare => true;

    public Task ShareLinkAsync(string tag, string invite) =>
        Share.Default.RequestAsync(new ShareTextRequest
        {
            Title = "Add me on Aether",
            Subject = "Add me on Aether",
            Text = $"Add me on Aether — my tag is {tag}\n{invite}",
        });

    public async Task ShareQrAsync(string tag, byte[] png)
    {
        if (png is null || png.Length == 0) return;

        // A stable, tag-named file in the cache: the share sheet needs a real file to hand across, and
        // the cache is the right home for something regenerated on demand and not worth keeping.
        var safe = string.Concat(tag.Where(c => char.IsLetterOrDigit(c) || c == '-'));
        var path = Path.Combine(FileSystem.CacheDirectory, $"aether-{safe}.png");
        await File.WriteAllBytesAsync(path, png).ConfigureAwait(false);

        await Share.Default.RequestAsync(new ShareFileRequest
        {
            Title = $"My Aether QR — {tag}",
            File = new ShareFile(path),
        });
    }
}

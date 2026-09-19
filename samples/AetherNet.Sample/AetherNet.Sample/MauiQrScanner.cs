// SPDX-License-Identifier: MIT

using System.Linq;
using AetherNet.Sample.Shared.Services;

namespace AetherNet.Sample;

/// <summary>
/// Opens a native camera page over the Blazor UI to scan a QR — ZXing.Net.Maui (managed decode +
/// CameraX, no ML Kit) does the work; this just hosts it and hands the decoded text back to the Blazor
/// add flow. A native page because our UI is Blazor in a WebView and ZXing's reader is a MAUI control,
/// so it is pushed modally over the WebView host and popped when a code is read or the person cancels.
/// </summary>
public sealed class MauiQrScanner : IQrScanner
{
    public bool CanScan => true;

    public async Task<string?> ScanAsync()
    {
        var tcs = new TaskCompletionSource<string?>();

        // Navigation touches the visual tree, so it must run on the UI thread — ScanAsync may be
        // awaited from anywhere.
        await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var nav = Application.Current?.Windows.FirstOrDefault()?.Page?.Navigation;
            if (nav is null) { tcs.TrySetResult(null); return; }
            await nav.PushModalAsync(new ScannerPage(r => tcs.TrySetResult(r)));
        });

        var scanned = await tcs.Task.ConfigureAwait(false);

        await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var nav = Application.Current?.Windows.FirstOrDefault()?.Page?.Navigation;
            try { if (nav is not null) await nav.PopModalAsync(); } catch { /* already gone */ }
        });

        return scanned;
    }
}

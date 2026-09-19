// SPDX-License-Identifier: MIT

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// Scan someone else's AetherTag QR with the camera — the consume side of sharing. Opens the camera,
/// decodes a QR, and hands back what it encoded (an <c>aether://…/add?k=…</c> invite), or null if the
/// person backed out.
/// </summary>
public interface IQrScanner
{
    /// <summary>Whether this build can open a camera to scan — a phone can; a plain web page cannot.</summary>
    bool CanScan { get; }

    /// <summary>Open the scanner and return the decoded text, or null if cancelled / unavailable.</summary>
    Task<string?> ScanAsync();
}

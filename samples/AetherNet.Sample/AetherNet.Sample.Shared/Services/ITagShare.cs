// SPDX-License-Identifier: MIT

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// Hand your AetherTag to another app through the phone's own share sheet.
///
/// <para>
/// Two ways, because "give someone my tag" splits into two real situations. A <b>link</b> to send
/// privately — WhatsApp, txtMe!, a DM — carries the <c>aether://…/add?k=…</c> invite as text, so the
/// person taps it and Aether opens straight on "add you", no scanning. A <b>QR image</b> to post
/// publicly — a profile, a poster, a story — is a picture anyone can scan to add you. Both encode the
/// same invite that backs the on-screen QR.
/// </para>
/// </summary>
public interface ITagShare
{
    /// <summary>Whether this build can hand things to other apps — a phone can; a plain web page cannot.</summary>
    bool CanShare { get; }

    /// <summary>Share the invite as a tappable link plus your tag, over the phone's own share sheet.</summary>
    Task ShareLinkAsync(string tag, string invite);

    /// <summary>Share the QR as a PNG image, over the phone's own share sheet.</summary>
    Task ShareQrAsync(string tag, byte[] png);
}

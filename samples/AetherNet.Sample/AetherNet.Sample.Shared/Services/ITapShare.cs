// SPDX-License-Identifier: MIT

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// Touch My Blood — handing somebody the app by touching phones.
///
/// <para>
/// The giver's handset presents itself as an NFC tag while this is armed. The friend on the other
/// side taps, their phone reads it exactly as it would read a sticker on a poster, and it offers them
/// the address the tag carries. They need nothing installed for that to work, which is the whole
/// point — you cannot ask somebody to install an app in order to be given an app.
/// </para>
///
/// <para>
/// <b>NFC means NFC.</b> A handset without the hardware does not get this, and is not offered a
/// consolation prize dressed up as the same feature. It is the same rule the radios follow: never
/// show a capability the silicon does not have. What such a phone gets instead is the QR code, which
/// is its own thing and says so.
/// </para>
/// </summary>
public interface ITapShare
{
    /// <summary>Whether this phone can be tapped — the hardware is present and switched on.</summary>
    bool IsSupported { get; }

    /// <summary>
    /// Why not, in words somebody holding the phone can act on, or null when it can.
    /// </summary>
    /// <remarks>
    /// "No NFC in this phone" and "NFC is switched off" are different sentences because only one of
    /// them is worth offering a tap to fix.
    /// </remarks>
    string? UnavailableReason => null;

    /// <summary>Can the person do something about <see cref="UnavailableReason"/>?</summary>
    bool IsFixable => false;

    /// <summary>Whether a tap right now would hand anything over.</summary>
    bool IsArmed { get; }

    /// <summary>
    /// Arm the tap with the address to hand over and this phone's AetherTag.
    /// </summary>
    /// <remarks>
    /// Armed only while somebody is looking at the screen that armed it. A phone that quietly offers
    /// its own installer to anything that brushes past it in a taxi is not a feature.
    /// </remarks>
    /// <param name="ssid">The network to hand over, when one is being offered.</param>
    /// <param name="passphrase">Its key. Nobody reads it — the tap carries it.</param>
    void Arm(string aetherTag, string? ssid = null, string? passphrase = null);

    /// <summary>
    /// Arm the tap with a message built elsewhere.
    /// </summary>
    /// <param name="message">A complete NDEF message.</param>
    /// <param name="what">What it is, in words, for the log — a tap that fails is otherwise silent.</param>
    /// <remarks>
    /// Every tap this app knows how to build is assembled and tested on the platform-neutral side,
    /// where a reader's whole walk can be played against it. This keeps it that way: the platform
    /// decides whether a tap is possible at all and then hands the bytes over, and never decides what
    /// those bytes say.
    /// </remarks>
    void ArmRaw(byte[] message, string what);

    /// <summary>Stop offering. Called when the screen goes away, not left to a timeout.</summary>
    void Disarm();

    /// <summary>A phone actually took the message. The moment the tap landed.</summary>
    event Action? Tapped;
}

/// <summary>
/// For heads with no NFC hardware behind them — the web head, and any desktop.
/// </summary>
/// <remarks>
/// It reports itself unsupported rather than pretending, so the screen shows the QR alone and never
/// asks somebody to tap a laptop against a phone.
/// </remarks>
public sealed class NoTapShare : ITapShare
{
    public bool IsSupported => false;
    public string? UnavailableReason => "this device has no NFC";
    public bool IsArmed => false;
    public void Arm(string aetherTag, string? ssid = null, string? passphrase = null) { }
    public void ArmRaw(byte[] message, string what) { }
    public void Disarm() { }
    public event Action? Tapped { add { } remove { } }
}

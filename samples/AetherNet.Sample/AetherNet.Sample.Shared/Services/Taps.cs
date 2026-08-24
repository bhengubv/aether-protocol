// SPDX-License-Identifier: MIT

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// Where "you just touched somebody's phone" goes after the operating system hands it over.
///
/// <para>
/// A tap is one-way. One phone presents itself as a tag and the other reads it, so only the reader
/// comes away knowing who it touched — and the reader is the phone the system wakes up with an
/// intent. This is where that lands.
/// </para>
///
/// <para>
/// Same shape as <see cref="InviteLinks"/> and for the same reason: an Android activity is built by
/// the system rather than by the container, so it has nothing to inject into, and a tap on a phone
/// whose app is not running arrives before there is any UI to hand it to.
/// </para>
/// </summary>
public sealed class Taps
{
    private readonly object _gate = new();
    private string? _waiting;

    /// <summary>The one instance, for platform code that cannot be given one.</summary>
    public static Taps? Current { get; set; }

    /// <summary>A phone was touched, and it turned out to be this person.</summary>
    public event Action<string>? Touched;

    /// <summary>
    /// Hand over whose phone was touched. Held if nothing is listening yet.
    /// </summary>
    /// <remarks>
    /// The commonest case of all is a tap that launches the app cold — somebody touches a phone whose
    /// Aether was not running, and the tag arrives long before a page exists to act on it.
    /// </remarks>
    public void Deliver(string? aetherTag)
    {
        if (string.IsNullOrWhiteSpace(aetherTag)) return;

        Action<string>? listeners;
        lock (_gate)
        {
            listeners = Touched;
            if (listeners is null) { _waiting = aetherTag; return; }
        }

        listeners.Invoke(aetherTag);
    }

    /// <summary>Take whoever was touched before anyone was listening, once.</summary>
    public string? TakeWaiting()
    {
        lock (_gate)
        {
            var tag = _waiting;
            _waiting = null;
            return tag;
        }
    }
}

// SPDX-License-Identifier: MIT

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// Where an <c>aether://TAG/add?k=…</c> invite goes after the operating system hands it over.
///
/// <para>
/// It went nowhere. The Android activity parsed the link, checked it carried a usable tag, stored it
/// in a static and raised an event — and nothing anywhere subscribed to that event or ever read that
/// static. Scanning somebody's invite opened the app and added nobody, silently, which is the worst
/// possible way for it to fail because it looks exactly like it worked.
/// </para>
///
/// <para>
/// That mattered far more than one broken button. The invite is the <b>only</b> path that carries a
/// public key, and without a key two phones cannot derive the Wi-Fi Direct group they are supposed to
/// meet on. Dropping the link did not just skip an add — it made a pair of phones that had never met
/// unable to pair at all.
/// </para>
/// </summary>
public sealed class InviteLinks
{
    private readonly object _gate = new();
    private string? _waiting;

    /// <summary>
    /// The one instance, for platform code that cannot be given one.
    /// </summary>
    /// <remarks>
    /// An Android activity is built by the system, not by the container, so it has nothing to inject
    /// into. Set once at startup and read from the activity — the alternative is the static event that
    /// nobody wired, which is what this replaces.
    /// </remarks>
    public static InviteLinks? Current { get; set; }

    /// <summary>An invite arrived and something is listening.</summary>
    public event Action<string>? Arrived;

    /// <summary>
    /// Hand over a link. Held if nothing is listening yet, because the commonest case of all is a scan
    /// that launches the app cold — the link arrives before there is any UI to hand it to.
    /// </summary>
    public void Deliver(string? link)
    {
        if (string.IsNullOrWhiteSpace(link)) return;

        Action<string>? listeners;
        lock (_gate)
        {
            listeners = Arrived;
            if (listeners is null) { _waiting = link; return; }
        }

        listeners.Invoke(link);
    }

    /// <summary>Take whatever arrived before anyone was listening, once.</summary>
    public string? TakeWaiting()
    {
        lock (_gate)
        {
            var link = _waiting;
            _waiting = null;
            return link;
        }
    }
}

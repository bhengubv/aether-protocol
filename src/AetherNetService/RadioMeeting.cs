// SPDX-License-Identifier: MIT
#if ANDROID
using AetherNet.Mesh;
using AetherNetNodeService;
using AetherNetNodeService.Host;
using AetherNet.Rendezvous;
using Microsoft.Extensions.Logging;

namespace AetherNetService;

/// <summary>
/// Tells AetherNetService's radios whom to keep reachable, from the contacts a connected app hands over. Every
/// contact is met on the network both phones are already on (<see cref="IRadioMesh.MeetPeer"/>), re-asserted every
/// half-minute: meeting is idempotent, and a rendezvous that dropped is picked back up.
///
/// <para>
/// The pair-by-pair radios meet one contact, chosen by <see cref="MeetingHost.Choose"/> — the lowest-sorting one who
/// is actually here, the same rule on both phones. They are pointed again only when that choice changes or its
/// contact is not reachable; re-pointing them every pass while a link was up is what kept pulling them toward an
/// absent contact who happened to sort lowest.
/// </para>
///
/// <para>
/// The fast radio — a Wi-Fi Direct group, for calls and video — is not formed here; chat rides Bluetooth and Wi-Fi.
/// </para>
/// </summary>
internal sealed class RadioMeeting : INodeMeeting, IDisposable
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(30);

    private readonly IIdentityService _me;
    private readonly IRadioMesh _radio;
    private readonly ILogger? _log;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private readonly object _applying = new();
    private IReadOnlyList<NodeContact> _contacts = Array.Empty<NodeContact>();

    /// <summary>Whom the pair-by-pair radios point at now.</summary>
    private string? _host;

    /// <summary>Which radios could be used on the last pass, so a change — a permission just granted — is seen.</summary>
    private string _usable = "";

    /// <summary>The radios usable right now, by name, in the mesh's order.</summary>
    private string Usable()
    {
        var names = new List<string>();
        foreach (var r in _radio.Radios)
        {
            if (r.Available) names.Add(r.Name);
        }

        return string.Join(",", names);
    }

    public RadioMeeting(IIdentityService me, IRadioMesh radio, ILogger<RadioMeeting>? log = null)
    {
        _me = me ?? throw new ArgumentNullException(nameof(me));
        _radio = radio ?? throw new ArgumentNullException(nameof(radio));
        _log = log;
        _timer = new Timer(_ => Apply(), null, Every, Every);
    }

    public void Meet(IReadOnlyList<NodeContact> contacts)
    {
        lock (_gate)
        {
            _contacts = contacts ?? Array.Empty<NodeContact>();
        }

        _log?.LogInformation("Keeping {Count} contact(s) reachable", _contacts.Count);
        Apply();
    }

    private void Apply()
    {
        IReadOnlyList<NodeContact> contacts;
        lock (_gate)
        {
            contacts = _contacts;
        }

        if (contacts.Count == 0) return;

        // The timer and a new contact list can both arrive at once; one pass at a time keeps _host honest.
        lock (_applying)
        {
            try
            {
                var me = _me.AetherTag;
                var tags = new List<string>(contacts.Count);
                foreach (var contact in contacts)
                {
                    var tag = contact.Tag.Value;
                    if (string.IsNullOrEmpty(tag)) continue;
                    tags.Add(tag);

                    if (Meeting.With(me, tag) is { } meeting)
                    {
                        _radio.MeetPeer(meeting);
                    }
                }

                var host = MeetingHost.Choose(tags, _radio.IsReachable, _host);
                if (host is null) return;

                var usable = Usable();
                var usableChanged = !string.Equals(usable, _usable, StringComparison.Ordinal);
                _usable = usable;

                // Already with them, and no radio has come or gone since: leave the radios alone. A radio that has
                // just become usable — its permission granted while the service ran — is pointed at them too, or
                // it would sit idle until the host changed.
                if (host == _host && _radio.IsReachable(host) && !usableChanged) return;

                if (Meeting.With(me, host) is { } hostMeeting)
                {
                    if (host != _host) _log?.LogInformation("Radios now meet {Host} (was {Before})", host, _host ?? "nobody");
                    _host = host;
                    _radio.Link(hostMeeting);
                }
            }
            catch (Exception ex)
            {
                // On a timer thread with nobody to throw to; the next pass tries again.
                _log?.LogWarning(ex, "Could not bring the radios to the contacts");
            }
        }
    }

    public void Dispose() => _timer.Dispose();
}
#endif

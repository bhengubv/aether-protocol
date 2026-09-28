// SPDX-License-Identifier: MIT
#if ANDROID
using AetherNet.Mesh;
using AetherNet.Node;
using AetherNet.Node.Host;
using AetherNet.Rendezvous;
using Microsoft.Extensions.Logging;

namespace AetherNetService;

/// <summary>
/// Tells AetherNetService's radios whom to keep reachable, from the contacts a connected app hands over. Every
/// contact is met on the network both phones are already on (<see cref="IRadioMesh.MeetPeer"/>), and the radios
/// link with the lowest-sorting contact — the same rule on both phones, so they agree on where to meet without a
/// word passing between them. Re-asserted every half-minute: meeting is idempotent, and a radio that dropped a
/// rendezvous picks it back up.
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
    private IReadOnlyList<NodeContact> _contacts = Array.Empty<NodeContact>();

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

        try
        {
            var me = _me.AetherTag;
            string? host = null;
            foreach (var contact in contacts)
            {
                var tag = contact.Tag.Value;
                if (string.IsNullOrEmpty(tag)) continue;

                if (Meeting.With(me, tag) is { } meeting)
                {
                    _radio.MeetPeer(meeting);
                }

                if (host is null || string.CompareOrdinal(tag, host) < 0)
                {
                    host = tag;
                }
            }

            if (host is not null && Meeting.With(me, host) is { } hostMeeting)
            {
                _radio.Link(hostMeeting);
            }
        }
        catch (Exception ex)
        {
            // On a timer thread with nobody to throw to; the next pass tries again.
            _log?.LogWarning(ex, "Could not bring the radios to the contacts");
        }
    }

    public void Dispose() => _timer.Dispose();
}
#endif

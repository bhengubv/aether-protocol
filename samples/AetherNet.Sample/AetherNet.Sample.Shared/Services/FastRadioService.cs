// SPDX-License-Identifier: MIT

using AetherNet.Identity;
using AetherNet.Rendezvous;
using AetherNet.Sample.Shared.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// Brings the fast radio up, from the contact list alone.
///
/// <para>
/// The thing that took longest to see is that discovery was never needed. Two phones that have added
/// each other already hold everything required to meet: both AetherTags, so both compute the same
/// answer about who hosts, and the host's public key, so both derive the same network name and
/// passphrase. There is nothing to find out. Every earlier attempt failed trying to learn something
/// both phones already knew — service discovery could not complete, and sending credentials over the
/// link needed the link the credentials were for.
/// </para>
///
/// <para>
/// So this asks no radio anything. It reads the contact list, works out whether this phone hosts, and
/// either creates that group or joins it. It runs at start-up and whenever the contact list changes,
/// which are the only two moments the answer can differ.
/// </para>
/// </summary>
public sealed class FastRadioService : IDisposable
{
    private readonly AetherStore _store;
    private readonly IIdentityService _me;
    private readonly IWifiDirectGroup _group;

    /// <summary>Every radio on the phone, so a meeting reaches all of them and not just one.</summary>
    private readonly IRadioMesh? _mesh;
    private readonly ContactService? _contacts;
    private readonly ILogger<FastRadioService> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _currentGroup;
    private bool _disposed;
    private int _keepingUp;

    /// <summary>
    /// How often to check we are still where we should be.
    /// <para>
    /// The joiner can be ready before the host is — measured: one phone tried to join five seconds
    /// before the other had finished creating the group, and with nothing retrying, that was the end
    /// of it until somebody restarted the app. Neither phone can know when the other became ready, so
    /// the answer is simply to keep asking.
    /// </para>
    /// </summary>
    private static readonly TimeSpan KeepUpEvery = TimeSpan.FromSeconds(8);

    /// <param name="onIdle">
    ///   Called when the radio is put away, so the host can release whatever it took to hold the link.
    /// </param>
    public FastRadioService(AetherStore store, IIdentityService me, IWifiDirectGroup group,
        ContactService? contacts = null, ILogger<FastRadioService>? logger = null,
        Action? onIdle = null, IRadioMesh? mesh = null)
    {
        _mesh = mesh;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _me = me ?? throw new ArgumentNullException(nameof(me));
        _group = group ?? throw new ArgumentNullException(nameof(group));
        _contacts = contacts;
        _log = logger ?? NullLogger<FastRadioService>.Instance;

        if (_contacts is not null) _contacts.Changed += OnContactsChanged;

        // A group that goes away has to come back, and the radio cannot decide that for itself — it
        // does not know who belongs in one. Waiting for somebody to open the app and tap something is
        // how a mesh ends up looking unreliable when the radio was fine all along.
        _group.GroupLost += OnGroupLost;
    }

    /// <summary>Running commentary, for the radio log.</summary>
    public event Action<string>? Trace;

    /// <summary>What this phone is doing right now, in words a person could read.</summary>
    public string State { get; private set; } = "not started";

    /// <summary>What was last said, so saying it again changes nothing.</summary>
    private string _said = "";

    /// <summary>
    /// Say something, once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This loop runs every few seconds forever, and while a phone has nobody added it has the same
    /// thing to report every time. Written out each pass, that is two lines every eight seconds, and
    /// Android's log daemon responds by evicting the tag — <c>chatty: WifiP2pService expire 58
    /// lines</c>. It does not throttle the noise, it drops whatever the buffer needs to lose.
    /// </para>
    /// <para>
    /// Measured 2026-08-25: the app's entire buffer held 98 lines, nearly all of them this one
    /// sentence, and the Wi-Fi Direct lines that would have shown whether a group formed had already
    /// been expired out of it. I read that absence twice and reported a working radio as broken. A log
    /// that eats itself is worse than no log, because it looks like evidence.
    /// </para>
    /// <para>
    /// The screen still updates every pass — <see cref="State"/> and <see cref="Trace"/> are set
    /// unconditionally. Only the writing-down is suppressed, and only while nothing has changed.
    /// </para>
    /// </remarks>
    private void T(string message)
    {
        State = message;
        Trace?.Invoke(message);

        if (string.Equals(message, _said, StringComparison.Ordinal)) return;
        _said = message;
        _log.LogInformation("[FastRadio] {Message}", message);
    }

    /// <summary>
    /// Who hosts for this Circle: the lowest AetherTag among this phone and everybody it has added.
    /// </summary>
    /// <remarks>
    /// Deliberately not pairwise. A phone can only be in one Wi-Fi Direct group at a time, so a
    /// per-pair answer would have a phone with two contacts trying to be in two groups. One host for
    /// the Circle means a third phone joining computes the same credentials the second one did, and
    /// they all end up in the same group.
    /// </remarks>
    private ContactRecord? HostContact()
    {
        ContactRecord? lowest = null;
        foreach (var contact in _store.GetContacts())
        {
            // A key is all this needs, and demanding mutuality as well was circular. Becoming mutual
            // requires their add-request to arrive; their add-request travels over the radio; the
            // radio needs a group; the group needs a host chosen HERE. So two phones that had never
            // met sat forever, each one added by the other, each one refusing to form the group that
            // was the only way to finish adding. Measured on a clean pair: both said "nobody added
            // yet — the group forms as soon as there is somebody to form it with", with the other
            // person plainly in the contact list.
            //
            // Adding somebody is a decision to associate with them. Their half of it is a fact about
            // the conversation, not a precondition for switching a radio on.
            //
            // And their key is not one either — which is the same circle again, one layer down. Who
            // hosts is decided by comparing two tags, and the host builds the group out of its OWN
            // key, which it always has. Requiring the contact's key HERE meant a phone that had been
            // given a tag and nothing else never even reached the question, so the lower-tagged phone
            // never hosted, so the group that was the only way to learn the key never formed.
            //
            // Measured on this pair: both added by typed tag, both showed "waiting for them", and
            // neither radio started a single P2P operation — the framework logs were empty, because
            // nothing had asked them for anything.
            if (contact.Tag is not { Length: > 0 }) continue;
            if (lowest is null || string.CompareOrdinal(contact.Tag, lowest.Tag) < 0) lowest = contact;
        }
        return lowest;
    }

    /// <summary>
    /// Keep the fast radio up for as long as the app is running.
    ///
    /// <para>
    /// Idempotent and cheap: when the phone is already in the right group this does nothing at all.
    /// It exists because there is no moment either phone can point to and say "the other one is ready
    /// now" — so rather than trying to be clever about timing, both sides simply keep asking until
    /// they are in the same group.
    /// </para>
    /// </summary>
    public void KeepUp(CancellationToken cancellationToken = default)
    {
        if (_disposed || Interlocked.Exchange(ref _keepingUp, 1) == 1) return;

        _ = Task.Run(async () =>
        {
            while (!_disposed && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Held for as long as the app is running, and measured rather than assumed.
                    //
                    // It was torn down after 45s idle, to spare the phone's own Wi-Fi. The numbers that
                    // justified that were taken on a handset whose Wi-Fi was switched off, and are
                    // wrong. Measured properly, on two healthy phones with the group up on 2437 while
                    // both stations sat on 5500: the client goes from 21ms to 39ms average and the host
                    // from 15ms to 23ms, and NEITHER station drops. That is a real cost and a small one.
                    //
                    // Tearing down cost far more than it saved: a phone whose radio is down cannot be
                    // told it has an incoming call, so every call to an idle phone rang out. A doubling
                    // of ping latency is worth paying to be reachable.
                    await BringUpAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _log.LogDebug(ex, "[FastRadio] keep-up"); }

                try { await Task.Delay(KeepUpEvery, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            Interlocked.Exchange(ref _keepingUp, 0);
        }, CancellationToken.None);
    }

    /// <summary>
    /// Say that something needs the fast radio now — a message to send, a call, a transfer.
    /// </summary>
    /// <remarks>
    /// Cheap and idempotent; call it whenever traffic appears. The link comes up if it is not up and
    /// the clock restarts, so a conversation keeps it alive without anybody managing it.
    /// </remarks>
    public void Wake()
    {
        _wantedUntil = DateTimeOffset.UtcNow + HoldFor;
        if (_disposed || _group.IsInGroup) return;

        _ = Task.Run(async () =>
        {
            try { await BringUpAsync().ConfigureAwait(false); }
            catch (Exception ex) { _log.LogDebug(ex, "[FastRadio] wake"); }
        });
    }

    /// <summary>Whether anything has wanted the link recently enough to keep holding it.</summary>
    private bool Wanted => DateTimeOffset.UtcNow < _wantedUntil;

    /// <summary>
    /// Put the radio away when nobody has needed it for a while.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the difference between a connection and a residence. Holding a Wi-Fi Direct group
    /// permanently makes the phone an access point that beacons around the clock, and on this hardware
    /// the host loses its own Wi-Fi for as long as it does — measured, the station went to
    /// "DISCONNECTED, Frequency: -1MHz" and the other phone's latency to its gateway went from 4ms to
    /// 836ms. All of that to carry nothing, because most of the time there is nothing to carry.
    /// </para>
    /// <para>
    /// The cost is a few seconds before the first packet of a new conversation. That is a tick
    /// appearing a moment later, or a ring taking slightly longer — against a radio the phone gets to
    /// keep the rest of the day.
    /// </para>
    /// </remarks>
    private async Task DropIfIdleAsync()
    {
        if (!_group.IsInGroup || _currentGroup is null) return;

        T("nothing needs the fast radio — putting it away so this phone gets its Wi-Fi back");
        _currentGroup = null;
        await _group.LeaveAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// How long the link is held after the last thing that wanted it.
    /// </summary>
    /// <remarks>
    /// Long enough to cover the gaps in a conversation — nobody types continuously — and short enough
    /// that a phone put down in a pocket is not still running an access point ten minutes later.
    /// </remarks>
    private static readonly TimeSpan HoldFor = TimeSpan.FromSeconds(45);

    private DateTimeOffset _wantedUntil = DateTimeOffset.MinValue;

    /// <summary>
    /// How many refused joins before a phone that was told to join hosts instead.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tags decide who hosts, and they decide it without asking whether the radio can. A phone
    /// whose driver refuses to create a group at every channel on the ladder returned null, said "could
    /// not create the group", and tried again the same way for as long as the app ran — while the other
    /// phone sat in the join branch, perfectly able to host, and was never asked. Neither of the two
    /// phones here has ever done that, which is exactly why it went unnoticed.
    /// </para>
    /// <para>
    /// So the tag ordering is a first answer rather than a final one. The phone it chose gets its whole
    /// attempt, and only when that has actually failed does the other take over. Nothing is negotiated
    /// and nothing new is on the wire: a radio that can host succeeds on the first pass, so the joiner
    /// never reaches this count and the stand-in never happens.
    /// </para>
    /// </remarks>
    private const int StandInAfter = 3;

    /// <summary>Refused joins in a row, as the phone the tags told to join.</summary>
    private int _joinsRefused;

    /// <summary>Set when this phone was told to host and the radio would not, at any channel.</summary>
    private bool _cannotHost;

    /// <summary>
    /// Host the Circle's group, or join it. Safe to call as often as you like — it does nothing when
    /// the phone is already where it should be.
    /// </summary>
    public async Task BringUpAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || !_group.IsSupported) return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var peer = HostContact();
            if (peer is null)
            {
                T("nobody added yet — the group forms as soon as there is somebody to form it with");
                return;
            }

            // Everyone in the Circle derives from the same phone's key, so everyone lands on the same
            // group. Whether that phone is this one only changes whether we create it or join it.
            var iHost = GroupRole.HostsTheGroup(_me.AetherTag, peer.Tag);
            var hostKey = iHost ? _me.PublicKey : peer.PublicKey;

            // Two ways to the same door, and both phones have to choose the same one.
            //
            // The stronger door is derived from the host's key: nobody who has not been given that key
            // can compute it. The other is derived from the two tags, which both phones certainly hold
            // the moment one has added the other — and it is what makes a typed tag work at all, since
            // a tag is a hash of a key and cannot be turned back into one. Without it a phone holding
            // only a tag had nowhere to go, and waited for a key that could only arrive over the link
            // it was trying to build. See Meeting.
            //
            // WHICH door has to be answered the same way on both handsets, and "do I have their key"
            // is not that question — the host has its own key and the joiner does not, so each would
            // answer for itself and they would stand at different doors. Measured: one hosting
            // DIRECT-612K452N1 while the other knocked at DIRECT-S2YY2QM37.
            //
            // So the question is whether the two of them have exchanged keys AT ALL, which flips on
            // both phones at the same moment: their key is here, and they have added us back, which
            // they could only have done by reaching us.
            var settled = peer.PublicKey is { Length: > 0 } && peer.AddedByThem;

            var credentials = settled
                ? GroupCredentials.ForHost(hostKey)
                : GroupCredentials.ForMeeting(Meeting.With(_me.AetherTag, peer.Tag));

            if (!WifiDirectCredentials.IsUsable(credentials))
            {
                T($"cannot work out where to meet {peer.Tag}");
                return;
            }

            var firstTime = !settled;

            // Ask the radio, do not trust what we remember. A join that was accepted and then failed
            // to form would otherwise be recorded as done and never tried again.
            if (string.Equals(_currentGroup, credentials!.NetworkName, StringComparison.Ordinal)
                && _group.IsInGroup)
                return;   // genuinely where we should be

            _currentGroup = null;

            // Every other radio is told the same thing at the same time, in terms it can use. Wi-Fi
            // puts the rendezvous on a multicast group, LoRa will put it in an address; none of them
            // waits on Wi-Fi Direct, and Wi-Fi Direct waits on none of them.
            // Every radio is told the same thing at the same time, in terms it can use. Wi-Fi puts
            // the rendezvous on a multicast group, LoRa will put it in an address; none of them waits
            // on Wi-Fi Direct, and Wi-Fi Direct waits on none of them.
            if (Meeting.With(_me.AetherTag, peer.Tag) is { } meeting) _mesh?.Link(meeting);

            // Who the tags chose, and whether the radio agreed. A phone told to host that could not
            // joins instead; a phone told to join that cannot get in hosts instead. See StandInAfter.
            var standingIn = !iHost && _joinsRefused >= StandInAfter;
            var hosting = (iHost && !_cannotHost) || standingIn;

            if (hosting)
            {
                T(standingIn
                    ? $"{peer.Tag} should be hosting {credentials!.NetworkName} and is not — hosting it here instead"
                    : firstTime
                        ? $"hosting {credentials!.NetworkName} — where {peer.Tag} will look for us the first time"
                        : $"hosting {credentials!.NetworkName} for the Circle");
                var hosted = await _group.HostAsync(credentials, cancellationToken).ConfigureAwait(false);
                _currentGroup = hosted is null ? null : credentials.NetworkName;

                if (hosted is null)
                {
                    // Only the phone the tags chose stands down. A stand-in that fails simply keeps
                    // trying: if it gave up too, both ends would be joining a group nobody hosts.
                    if (iHost) _cannotHost = true;
                    T(iHost
                        ? "could not create the group — this radio refused every channel, so the other phone will be left to host"
                        : "could not create the group");
                }
                else
                {
                    _cannotHost = false;
                    _joinsRefused = 0;
                    T($"hosting {credentials.NetworkName} — anyone in the Circle can join it now");
                }
            }
            else
            {
                T(_cannotHost
                    ? $"this radio will not host, so {peer.Tag} has to — joining {credentials.NetworkName}"
                    : $"{peer.Tag} hosts {credentials.NetworkName} — joining");
                var joined = await _group.JoinAsync(credentials, cancellationToken).ConfigureAwait(false);
                _currentGroup = joined ? credentials.NetworkName : null;

                // Counted only for the phone the tags told to join. The one that stood down from
                // hosting must keep waiting for its peer rather than taking the role back.
                if (!iHost) _joinsRefused = joined ? 0 : _joinsRefused + 1;

                T(joined
                    ? $"on {credentials.NetworkName} with {peer.Tag} — the fast radio is up"
                    : $"{credentials.NetworkName} is not up yet — trying again shortly");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _currentGroup = null;
            _log.LogWarning(ex, "[FastRadio] Could not bring the fast radio up");
            T("could not bring the fast radio up");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The group dropped. Work out where we should be and go back there.</summary>
    private void OnGroupLost()
    {
        if (_disposed) return;
        _currentGroup = null;
        _ = Task.Run(async () =>
        {
            // A moment's grace: the radio has just torn the interface down, and asking it to build a
            // group while it is doing that is how you get BUSY back.
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                await BringUpAsync().ConfigureAwait(false);
            }
            catch (Exception ex) { _log.LogDebug(ex, "[FastRadio] rebuild after the group dropped"); }
        });
    }

    /// <summary>
    /// Somebody was added or removed. That can change who hosts, so the answer is worked out again.
    /// </summary>
    private void OnContactsChanged()
    {
        if (_disposed) return;
        _ = Task.Run(async () =>
        {
            try { await BringUpAsync().ConfigureAwait(false); }
            catch (Exception ex) { _log.LogDebug(ex, "[FastRadio] rebuild after contact change"); }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_contacts is not null) _contacts.Changed -= OnContactsChanged;
        _group.GroupLost -= OnGroupLost;
        _gate.Dispose();
    }
}

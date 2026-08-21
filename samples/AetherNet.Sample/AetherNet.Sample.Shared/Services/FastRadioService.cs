// SPDX-License-Identifier: MIT

using AetherNet.Identity;
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

    public FastRadioService(AetherStore store, IIdentityService me, IWifiDirectGroup group,
        ContactService? contacts = null, ILogger<FastRadioService>? logger = null)
    {
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

    private void T(string message)
    {
        State = message;
        Trace?.Invoke(message);
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
            if (!contact.IsMutual || contact.PublicKey is not { Length: > 0 }) continue;
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
                try { await BringUpAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _log.LogDebug(ex, "[FastRadio] keep-up"); }

                try { await Task.Delay(KeepUpEvery, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            Interlocked.Exchange(ref _keepingUp, 0);
        }, CancellationToken.None);
    }

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

            var credentials = GroupCredentials.ForHost(hostKey);
            if (!WifiDirectCredentials.IsUsable(credentials))
            {
                T($"no public key for {peer.Tag} yet — cannot work out the group without it");
                return;
            }

            // Ask the radio, do not trust what we remember. A join that was accepted and then failed
            // to form would otherwise be recorded as done and never tried again.
            if (string.Equals(_currentGroup, credentials!.NetworkName, StringComparison.Ordinal)
                && _group.IsInGroup)
                return;   // genuinely where we should be

            _currentGroup = null;

            if (iHost)
            {
                T($"hosting {credentials.NetworkName} for the Circle");
                var hosted = await _group.HostAsync(credentials, cancellationToken).ConfigureAwait(false);
                _currentGroup = hosted is null ? null : credentials.NetworkName;
                T(hosted is null
                    ? "could not create the group"
                    : $"hosting {credentials.NetworkName} — anyone in the Circle can join it now");
            }
            else
            {
                T($"{peer.Tag} hosts {credentials.NetworkName} — joining");
                var joined = await _group.JoinAsync(credentials, cancellationToken).ConfigureAwait(false);
                _currentGroup = joined ? credentials.NetworkName : null;
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

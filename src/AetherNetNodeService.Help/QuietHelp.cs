// SPDX-License-Identifier: MIT

using System.Text;
using AetherNet.Identity;
using AetherNetNodeService.Host;
using Microsoft.Extensions.Logging;
using Aware = AetherNet.Aware;

namespace AetherNetNodeService.Help;

/// <summary>
/// Quiet help inside the node. The person's own session goes out two ways at once — as a Bluetooth advert to phones
/// standing near them, and over the mesh to each guardian they chose — and the guardians' side reads what comes back
/// the other way and keeps the trail.
///
/// <para>
/// Only <see cref="Start"/> begins a session and it is only ever called for the person's own action on their own
/// phone. Nothing arriving over the mesh or the air starts one, and no guardian can start one for them.
/// <see cref="MarkSafe"/> is the only thing that ends one. Every message is sealed with the person's
/// <see cref="Aware.HelpKey"/>, which only the guardians they chose hold, so every other phone in range hears bytes
/// it cannot read and keeps nothing.
/// </para>
/// </summary>
public sealed class QuietHelp : INodeHelpSource, IDisposable
{
    /// <summary>How often a running session puts a fresh message on the air and over the mesh.</summary>
    public const int SendEverySeconds = 5;

    /// <summary>The first byte of a "here is my help key" message, which no help message can be (its own is 0x01).</summary>
    public const byte KeyShareMarker = 0x02;

    /// <summary>How many people this device will be a guardian for. A cap, so nobody can fill it up.</summary>
    public const int MostWatched = 32;

    private readonly HelpStore _store;
    private readonly INodeMessaging _messaging;
    private readonly IHelpRadio _radio;
    private readonly TimeProvider _time;
    private readonly ILogger? _log;
    private readonly ushort? _registeredAdvertId;
    private readonly Aware.HelpWatch _watch;
    private readonly object _gate = new();

    private Aware.HelpSession? _session;
    private ITimer? _timer;
    private double? _lat;
    private double? _lon;
    private double? _accuracyM;
    private long? _fixAt;
    private int? _battery;
    private bool _nearby;
    private int _reached;
    private string? _why;
    private bool _disposed;

    /// <param name="registeredAdvertId">
    /// The registered 16-bit Bluetooth service ID, once there is one. Null until then, and
    /// <see cref="HelpAdvertForm.Registered16"/> is reported unavailable with the reason.
    /// </param>
    public QuietHelp(
        HelpStore store,
        INodeMessaging messaging,
        IHelpRadio? radio = null,
        TimeProvider? timeProvider = null,
        ILogger<QuietHelp>? log = null,
        ushort? registeredAdvertId = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _messaging = messaging ?? throw new ArgumentNullException(nameof(messaging));
        _radio = radio ?? new NoHelpRadio();
        _time = timeProvider ?? TimeProvider.System;
        _log = log;
        _registeredAdvertId = registeredAdvertId;
        _watch = new Aware.HelpWatch(_time);

        _messaging.Inbound += OnInbound;
        _radio.Heard += OnHeard;
    }

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public HelpReport Current
    {
        get
        {
            // Never report a trail that should already have been forgotten.
            _watch.Refresh();
            lock (_gate)
            {
                return new HelpReport { Mine = Mine(), Watching = Watching() };
            }
        }
    }

    /// <inheritdoc />
    public bool Start(HelpKind kind)
    {
        lock (_gate)
        {
            if (kind == HelpKind.Safe)
            {
                return false;   // "safe" starts nothing; MarkSafe ends a session.
            }

            var guardians = _store.Guardians;
            if (guardians.Count == 0)
            {
                // Nobody holds the key, so nobody could read it. Said plainly rather than sent into the void.
                _why = "nobody chosen to ask yet — add the people you want to reach";
                _log?.LogWarning("Quiet help refused: no guardians chosen");
                Raise();
                return false;
            }

            _why = null;
            _session = new Aware.HelpSession(_store.Key, kind == HelpKind.Walk ? Aware.HelpKind.Walk : Aware.HelpKind.Help, _time);

            // Start from what the phone already knows. Waiting for the next fix would send the first messages —
            // the ones most likely to be the only ones — with no position at all.
            if (_lat is { } lat && _lon is { } lon)
            {
                _session.UpdatePosition(lat, lon, _fixAt ?? _time.GetUtcNow().ToUnixTimeMilliseconds(), _accuracyM);
            }

            if (_battery is { } battery)
            {
                _session.UpdateBattery(battery);
            }

            _reached = 0;
            _nearby = false;
            _log?.LogWarning("Quiet help started: {Kind}, {Guardians} to reach", kind, guardians.Count);
        }

        // The key first, so a guardian who missed it can read what follows, then the message itself at once.
        _ = ShareKeyAsync();
        _ = TickAsync();
        _timer = _time.CreateTimer(_ => _ = TickAsync(), null,
            TimeSpan.FromSeconds(SendEverySeconds), TimeSpan.FromSeconds(SendEverySeconds));
        Raise();
        return true;
    }

    /// <inheritdoc />
    public void MarkSafe()
    {
        lock (_gate)
        {
            if (_session is null)
            {
                return;
            }

            _session.MarkSafe();
            _log?.LogWarning("Quiet help: the person says they are safe");
        }

        // The "safe" message keeps going for a minute so guardians nearby hear it; the tick then puts it to bed.
        _ = TickAsync();
        Raise();
    }

    /// <inheritdoc />
    public void SetGuardians(IReadOnlyList<HelpGuardian> guardians)
    {
        ArgumentNullException.ThrowIfNull(guardians);
        var keyChanged = _store.SetGuardians(guardians);
        _log?.LogInformation("Quiet help: {Count} guardians chosen{Key}", guardians.Count, keyChanged ? ", with a new key" : "");
        if (keyChanged)
        {
            _ = ShareKeyAsync();
        }

        Raise();
    }

    /// <inheritdoc />
    public void SetOptions(HelpTriggers triggers, HelpAdvertForm advert)
    {
        ArgumentNullException.ThrowIfNull(triggers);
        // A container this device cannot use is not taken; Current says which it can.
        var usable = Usable(advert) ? advert : Chosen(advert);
        _store.SetOptions(triggers, usable);
        Raise();
    }

    /// <summary>Where the person's phone is now, for the message to carry. The host's own GPS, never a network lookup.</summary>
    public void UpdatePosition(double lat, double lon, long atMs, double? accuracyM = null)
    {
        lock (_gate)
        {
            _session?.UpdatePosition(lat, lon, atMs, accuracyM);
            if (Aware.PayloadLocation.ValidCoord(lat, lon))
            {
                _lat = lat;
                _lon = lon;
                _fixAt = atMs;
                _accuracyM = accuracyM;
            }
        }
    }

    /// <summary>What the message says of the battery, so a guardian knows how long the phone may keep talking.</summary>
    public void UpdateBattery(int percent)
    {
        lock (_gate)
        {
            _battery = Math.Clamp(percent, 0, 100);
            _session?.UpdateBattery(percent);
        }
    }

    /// <summary>
    /// An advert this device heard. Only a help message from someone this device is a guardian for is read; every
    /// other advert — which is nearly all of them — is nothing to Quiet help and nothing of it is kept.
    /// </summary>
    public void Heard(Aware.RadioFacts facts, int? rssi = null, Aware.GpsSample? whereIAm = null)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (_watch.Hear(facts, rssi, whereIAm, _registeredAdvertId) is { } heard)
        {
            _log?.LogWarning("Quiet help heard nearby: {Name} is {Kind}", heard.Name, heard.Kind);
            Raise();
        }
    }

    /// <summary>
    /// One round: a fresh message on the air and over the mesh, or the end of a session the person has finished.
    /// The timer calls it; a test awaits it.
    /// </summary>
    internal async Task TickAsync()
    {
        byte[] message;
        IReadOnlyList<HelpGuardian> guardians;
        lock (_gate)
        {
            if (_session is null)
            {
                return;
            }

            if (_session.IsOver)
            {
                _session = null;
                _nearby = false;
                _reached = 0;
                _timer?.Dispose();
                _timer = null;
                _radio.Stop();
                _log?.LogInformation("Quiet help is over");
                Raise();
                return;
            }

            message = _session.NextMessage();
            guardians = _store.Guardians;
        }

        // On the air, for the phones standing near this person. The radio wraps the message in its own advert.
        var form = Chosen(_store.Advert);
        if (Usable(form))
        {
            try
            {
                _radio.Advertise(message, form, _registeredAdvertId);
                lock (_gate)
                {
                    _nearby = true;
                }
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    _nearby = false;
                }

                _log?.LogWarning(ex, "Quiet help could not go out over Bluetooth");
            }
        }

        // And over the mesh, to each guardian. A send the mesh holds for later still counts as taken.
        var reached = 0;
        foreach (var guardian in guardians)
        {
            try
            {
                var result = await _messaging.SendAsync(guardian.Tag, message, Guid.NewGuid()).ConfigureAwait(false);
                if (result.Accepted)
                {
                    reached++;
                }
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "Quiet help could not reach {Guardian}", guardian.Name);
            }
        }

        lock (_gate)
        {
            _reached = reached;
        }

        Raise();
    }

    /// <summary>Hand each guardian the key, inside the mesh's own encryption, so they can read what follows.</summary>
    private async Task ShareKeyAsync()
    {
        var guardians = _store.Guardians;
        if (guardians.Count == 0)
        {
            return;
        }

        var key = _store.Key.ToBytes();
        foreach (var guardian in guardians)
        {
            var name = Encoding.UTF8.GetBytes(guardian.Name ?? string.Empty);
            var payload = new byte[1 + Aware.HelpKey.Length + 1 + name.Length];
            payload[0] = KeyShareMarker;
            key.CopyTo(payload, 1);
            payload[1 + Aware.HelpKey.Length] = (byte)guardian.Alert;
            name.CopyTo(payload, 2 + Aware.HelpKey.Length);
            try
            {
                await _messaging.SendAsync(guardian.Tag, payload, Guid.NewGuid()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "Could not hand {Guardian} the help key", guardian.Name);
            }
        }
    }

    /// <summary>
    /// A message the mesh delivered. Either somebody this device is a guardian for asking for help, or somebody
    /// choosing this device's owner as a guardian and handing over their key. Anything else is not ours.
    /// </summary>
    private void OnInbound(InboundMessage message)
    {
        var payload = message.Payload;
        if (payload.Length == 0)
        {
            return;
        }

        var bytes = payload.ToArray();
        if (bytes[0] == KeyShareMarker)
        {
            LearnKey(message.From, bytes);
            return;
        }

        // A help message is self-identifying: only a key this device holds can read it, so trying costs a hash.
        if (bytes.Length == Aware.HelpCodec.Length && _watch.Hear(bytes) is { } heard)
        {
            _log?.LogWarning("Quiet help over the mesh: {Name} is {Kind}", heard.Name, heard.Kind);
            Raise();
        }
    }

    private void LearnKey(AetherNetTag from, byte[] payload)
    {
        if (payload.Length < 2 + Aware.HelpKey.Length || string.IsNullOrEmpty(from.Value))
        {
            return;
        }

        // Already a guardian for them: a fresh key replaces the one held, which is how changing the set reaches here.
        if (!_watch.IsWatching(from.Value) && _watch.WatchedCount >= MostWatched)
        {
            _log?.LogWarning("Not watching over {From}: already watching {Count} people", from, MostWatched);
            return;
        }

        var key = Aware.HelpKey.FromBytes(payload.AsSpan(1, Aware.HelpKey.Length));
        var alert = payload[1 + Aware.HelpKey.Length] == (byte)HelpAlert.Quiet
            ? Aware.HelpAlertStyle.Quiet
            : Aware.HelpAlertStyle.Loud;
        var name = payload.Length > 2 + Aware.HelpKey.Length
            ? Encoding.UTF8.GetString(payload.AsSpan(2 + Aware.HelpKey.Length))
            : from.Value;
        _watch.Watch(from.Value, string.IsNullOrWhiteSpace(name) ? from.Value : name, key, alert);
        _log?.LogInformation("Watching over {Name} — they chose this device", name);
        Raise();
    }

    /// <summary>The container to send in: the person's choice when this device can, else whichever it can.</summary>
    private HelpAdvertForm Chosen(HelpAdvertForm wanted)
    {
        if (Usable(wanted))
        {
            return wanted;
        }

        foreach (var form in HelpAdvertForms.Preferred)
        {
            if (Usable(form))
            {
                return form;
            }
        }

        return wanted;
    }

    private bool Usable(HelpAdvertForm form)
        => _radio.Can(form) && (form != HelpAdvertForm.Registered16 || _registeredAdvertId is not null);

    private string? WhyNot(HelpAdvertForm form)
    {
        if (form == HelpAdvertForm.Registered16 && _registeredAdvertId is null)
        {
            return "needs a 16-bit Bluetooth service ID registered to us";
        }

        return _radio.Can(form) ? null : _radio.Why(form) ?? "this device cannot send it";
    }

    private HelpState Mine()
    {
        var session = _session;
        var over = session?.IsOver ?? true;
        var adverts = new List<HelpAdvertChoice>(HelpAdvertForms.Preferred.Length);
        var chosen = Chosen(_store.Advert);
        foreach (var form in HelpAdvertForms.Preferred)
        {
            var usable = Usable(form);
            adverts.Add(new HelpAdvertChoice(form, usable, usable && form == chosen, WhyNot(form)));
        }

        return new HelpState
        {
            On = session is not null && !over,
            Kind = session is null ? HelpKind.Safe : Kind(session.Kind),
            StartedAt = session is null ? null : When(session.StartedAt),
            SafeAt = session?.Kind == Aware.HelpKind.Safe ? When(_time.GetUtcNow().ToUnixTimeMilliseconds()) : null,
            Lat = _lat,
            Lon = _lon,
            AccuracyM = _accuracyM is { } metres ? (int)Math.Ceiling(metres) : null,
            FixAt = _fixAt is { } at ? When(at) : null,
            BatteryPercent = _battery,
            Nearby = _nearby && session is not null,
            GuardiansReached = _reached,
            Guardians = _store.Guardians,
            Triggers = _store.Triggers,
            Adverts = adverts,
            Why = _why,
        };
    }

    private IReadOnlyList<HelpWatchCase> Watching()
    {
        var cases = _watch.Cases;
        var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        var output = new List<HelpWatchCase>(cases.Count);
        foreach (var c in cases)
        {
            var trail = new HelpPoint[c.Trail.Count];
            for (var i = 0; i < trail.Length; i++)
            {
                var crumb = c.Trail[i];
                trail[i] = new HelpPoint(
                    When(crumb.At), crumb.Lat, crumb.Lon, crumb.AccuracyM,
                    crumb.Source == Aware.CrumbSource.Reported, crumb.Rssi);
            }

            output.Add(new HelpWatchCase
            {
                Person = AetherNetTag.TryParse(c.PersonId, out var tag) ? tag : default,
                Name = c.Name,
                Kind = Kind(c.Kind),
                Alert = c.Alert == Aware.HelpAlertStyle.Quiet ? HelpAlert.Quiet : HelpAlert.Loud,
                FirstHeardAt = When(c.FirstHeardAt),
                LastHeardAt = When(c.LastHeardAt),
                LastNearbyAt = c.LastNearbyAt is { } near ? When(near) : null,
                Lat = c.Lat,
                Lon = c.Lon,
                AccuracyM = c.AccuracyM,
                FixAt = c.FixAt is { } fix ? When(fix) : null,
                BatteryPercent = c.BatteryPercent,
                SafeAt = c.SafeAt is { } safe ? When(safe) : null,
                Find = Cue(c.FindIt(now)),
                Rssi = c.Loudness.Count > 0 ? c.Loudness[^1].Rssi : null,
                Trail = trail,
            });
        }

        return output;
    }

    private static DateTimeOffset When(long unixMs) => DateTimeOffset.FromUnixTimeMilliseconds(unixMs);

    private static HelpKind Kind(Aware.HelpKind kind) => kind switch
    {
        Aware.HelpKind.Help => HelpKind.Help,
        Aware.HelpKind.Walk => HelpKind.Walk,
        _ => HelpKind.Safe,
    };

    private static HelpFindCue Cue(Aware.HuntCue cue) => cue switch
    {
        Aware.HuntCue.VeryClose => HelpFindCue.VeryClose,
        Aware.HuntCue.Closer => HelpFindCue.Closer,
        Aware.HuntCue.Further => HelpFindCue.Further,
        Aware.HuntCue.Same => HelpFindCue.Same,
        Aware.HuntCue.Quiet => HelpFindCue.Quiet,
        Aware.HuntCue.Gone => HelpFindCue.Gone,
        _ => HelpFindCue.Waiting,
    };

    private void Raise() => Changed?.Invoke();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _messaging.Inbound -= OnInbound;
        _radio.Heard -= OnHeard;
        _timer?.Dispose();
        _timer = null;
        try
        {
            _radio.Stop();
        }
        catch (Exception ex)
        {
            _log?.LogDebug(ex, "stopping the help advert");
        }
    }

    private void OnHeard(Aware.RadioFacts facts, int? rssi, Aware.GpsSample? whereIAm) => Heard(facts, rssi, whereIAm);
}

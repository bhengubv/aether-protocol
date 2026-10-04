// SPDX-License-Identifier: MIT

using AetherNetNodeService;
using Microsoft.Extensions.Logging;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// Quiet help, as a screen sees it. A thin client: the node owns the session, the key, the guardians and the trail
/// (<c>AetherNetNodeService.Help</c>), and this only holds the last report it was given, asks the node to change
/// something when the person taps, and says when a screen should redraw.
/// </summary>
public sealed class QuietHelpService : IDisposable
{
    private readonly IAetherNodeClient? _node;
    private readonly ILogger? _log;
    private IDisposable? _listening;
    private bool _disposed;
    private bool _saidAboutListening;

    /// <param name="node">
    /// The device's node. Null on a head with none — the web one — where Quiet help cannot run at all, because it
    /// needs the radios and the mesh that AetherNetService owns. The screen then says so.
    /// </param>
    public QuietHelpService(IAetherNodeClient? node, ILoggerFactory? loggerFactory = null)
    {
        _node = node;
        _log = loggerFactory?.CreateLogger<QuietHelpService>();
    }

    /// <summary>Whether this device has a node that could carry Quiet help.</summary>
    public bool Available => _node is not null;

    /// <summary>
    /// Whether the node's pushes are being heard. False means a screen only knows what it last asked for — it will
    /// not go still for long, because every refresh tries again, but while it is false nothing arrives on its own.
    /// </summary>
    public bool Live => _listening is not null;

    /// <summary>Something a screen would redraw: a session started or ended, somebody moved, somebody is safe.</summary>
    public event Action? Changed;

    /// <summary>What the node last said. Empty until the first <see cref="RefreshAsync"/>.</summary>
    public HelpReport Report { get; private set; } = HelpReport.None;

    /// <summary>What this person is sending, and what they chose.</summary>
    public HelpState Mine => Report.Mine;

    /// <summary>The people who chose this person as a guardian and are asking for help now.</summary>
    public IReadOnlyList<HelpWatchCase> Watching => Report.Watching;

    /// <summary>Start listening for the node's pushes, so a screen is right without asking again.</summary>
    public void Listen()
    {
        if (_listening is not null || _disposed)
        {
            return;
        }

        if (_node is null)
        {
            return;
        }

        try
        {
            _listening = _node.Subscribe(new Pushes(this));
        }
        catch (Exception ex)
        {
            // Nothing else retried this, and it is asked for once: a failure here meant a screen that never updated
            // again — on the one feature where somebody is waiting to be told. Now every refresh tries again, so it
            // heals itself, and the first failure is said plainly instead of whispered to a debug log.
            if (!_saidAboutListening)
            {
                _saidAboutListening = true;
                _log?.LogWarning(ex, "Quiet help is not hearing the node's own news yet — it will keep trying");
            }
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_node is null)
        {
            Report = new HelpReport
            {
                Mine = new HelpState { Why = "Quiet help needs AetherNetService — the radios and the mesh are its." },
            };
            Changed?.Invoke();
            return;
        }

        // Ask to be told again. It costs nothing when it is already listening, and it is what turns a subscription
        // that failed once into one that simply starts late.
        Listen();

        try
        {
            Report = await _node.GetHelpAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A service that does not carry Quiet help, or one that is not there: the screen says so rather than
            // showing a half-filled page.
            _log?.LogDebug(ex, "the node did not say how Quiet help stands");
            Report = HelpReport.None;
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// The person asks for help, or starts sharing their way. False when the node cannot send it, and
    /// <see cref="HelpState.Why"/> then says why.
    /// </summary>
    public async Task<bool> StartAsync(HelpKind kind, CancellationToken cancellationToken = default)
    {
        if (_node is null)
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        bool started;
        try
        {
            started = await _node.StartHelpAsync(kind, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Quiet help could not start");
            started = false;
        }

        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        return started;
    }

    /// <summary>The person says they are safe — the only thing that ends it.</summary>
    public async Task MarkSafeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (_node is not null) await _node.MarkSafeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "could not mark safe");
        }

        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The guardians this person chooses, replacing the set they chose before.</summary>
    public async Task SetGuardiansAsync(IReadOnlyList<HelpGuardian> guardians, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(guardians);
        try
        {
            if (_node is not null) await _node.SetHelpGuardiansAsync(guardians, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "could not change who to ask");
        }

        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Which of their own actions start it, and which Bluetooth container carries it.</summary>
    public async Task SetOptionsAsync(HelpTriggers triggers, HelpAdvertForm advert, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(triggers);
        try
        {
            if (_node is not null) await _node.SetHelpOptionsAsync(triggers, advert, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "could not change the Quiet help options");
        }

        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Turn one trigger on or off, leaving the rest as they are.</summary>
    public Task SetTriggerAsync(HelpTrigger trigger, bool on, CancellationToken cancellationToken = default)
    {
        var triggers = Mine.Triggers;
        var enabled = on ? triggers.Enabled | trigger : triggers.Enabled & ~trigger;
        return SetOptionsAsync(triggers with { Enabled = enabled }, Chosen, cancellationToken);
    }

    /// <summary>The container in use, or ours when the node has not said.</summary>
    public HelpAdvertForm Chosen
    {
        get
        {
            foreach (var advert in Mine.Adverts)
            {
                if (advert.Chosen)
                {
                    return advert.Form;
                }
            }

            return HelpAdvertForm.AetherNet128;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _listening?.Dispose();
        _listening = null;
    }

    /// <summary>The node's pushes. Only Quiet help is of interest here; the rest of the app listens for the rest.</summary>
    private sealed class Pushes(QuietHelpService owner) : IAetherNodeEvents
    {
        public void OnInbound(InboundMessage message)
        {
        }

        public void OnLinkChanged(NodeLinkStatus status)
        {
        }

        public void OnGrantChanged(GrantState state)
        {
        }

        public void OnHelpChanged(HelpReport report)
        {
            owner.Report = report;
            owner.Changed?.Invoke();
        }
    }
}

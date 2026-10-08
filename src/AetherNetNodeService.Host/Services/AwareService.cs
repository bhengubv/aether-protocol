// SPDX-License-Identifier: MIT

using AetherNetNodeService;
using Microsoft.Extensions.Logging;
using AetherNet.Mesh;

namespace AetherNetNodeService.Host;

/// <summary>
/// Aether Aware, as a screen sees it. A thin client: the node does the hearing, the naming and the working out of
/// what has kept up with the person, and this only holds the last report it was given and says when to redraw.
/// </summary>
public sealed class AwareService : IDisposable
{
    private readonly IAetherNodeClient? _node;
    private readonly ILogger? _log;
    private IDisposable? _listening;
    private bool _disposed;
    private bool _saidAboutListening;

    /// <param name="node">
    /// The device's node. Null on a head with none — the web one — where there are no radios to hear with, and the
    /// screen says so.
    /// </param>
    public AwareService(IAetherNodeClient? node, ILoggerFactory? loggerFactory = null)
    {
        _node = node;
        _log = loggerFactory?.CreateLogger<AwareService>();
    }

    /// <summary>Whether this device has a node that could be listening.</summary>
    public bool Available => _node is not null;

    /// <summary>Whether the node's own news is being heard, rather than only what was last asked for.</summary>
    public bool Live => _listening is not null;

    /// <summary>Something a screen would redraw: something arrived, left, or is keeping up.</summary>
    public event Action? Changed;

    /// <summary>What the node last said.</summary>
    public AwareReport Report { get; private set; } = AwareReport.None;

    /// <summary>Everything heard lately, loudest first.</summary>
    public IReadOnlyList<AwareThing> Things => Report.Things;

    /// <summary>What has kept up with this person while they moved — the part worth telling somebody.</summary>
    public IReadOnlyList<AwareThing> MovingWithYou
    {
        get
        {
            var following = new List<AwareThing>();
            foreach (var thing in Report.Things)
            {
                if (thing.MovingWithYou)
                {
                    following.Add(thing);
                }
            }

            return following;
        }
    }

    /// <summary>Start listening for the node's pushes, so a screen is right without asking again.</summary>
    public void Listen()
    {
        if (_listening is not null || _disposed || _node is null)
        {
            return;
        }

        try
        {
            _listening = _node.Subscribe(new Pushes(this));
        }
        catch (Exception ex)
        {
            // Asked for once, so every refresh asks again rather than leaving a screen still for ever.
            if (!_saidAboutListening)
            {
                _saidAboutListening = true;
                _log?.LogWarning(ex, "Aether Aware is not hearing the node's own news yet — it will keep trying");
            }
        }
    }

    /// <summary>Ask the node what it hears, now.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_node is null)
        {
            Report = new AwareReport
            {
                Why = "Aether Aware needs AetherNetService — the radios that do the hearing are its.",
            };
            Changed?.Invoke();
            return;
        }

        Listen();

        try
        {
            Report = await _node.GetAwareAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A service that does not carry Aware, or one that is not there: say so rather than showing a half page.
            _log?.LogDebug(ex, "the node did not say what it hears");
            Report = AwareReport.None;
        }

        Changed?.Invoke();
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

    /// <summary>The node's own news, which arrives without being asked for.</summary>
    private sealed class Pushes(AwareService owner) : IAetherNodeEvents
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

        public void OnAwareChanged(AwareReport report)
        {
            owner.Report = report;
            owner.Changed?.Invoke();
        }
    }
}

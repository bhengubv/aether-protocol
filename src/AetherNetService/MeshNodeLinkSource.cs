// SPDX-License-Identifier: MIT
#if ANDROID
using AetherNet.Mesh;
using AetherNetNodeService;        // NodeLinkStatus, RadioStatus
using AetherNetNodeService.Host;
using Microsoft.Extensions.Logging;

namespace AetherNetService;

/// <summary>
/// Projects the node's <see cref="IRadioMesh"/> onto the node contract's <see cref="NodeLinkStatus"/> — the
/// presence seam the host reads, replacing <c>OfflineNodeLinkSource</c>. A report of what is linked right now
/// and over which radio, never a picker; the widest linked radio carries and this only says so. It carries
/// AetherNetService's permissions too, so a connected app can show them.
/// </summary>
internal sealed class MeshNodeLinkSource : INodeLinkSource
{
    /// <summary>How often to look again while a permission is still missing.</summary>
    private static readonly TimeSpan LookEvery = TimeSpan.FromSeconds(3);

    private readonly IRadioMesh _radio;
    private readonly ILogger? _logger;
    private readonly INodeNearby? _nearby;
    private readonly PermissionWatch _permissions;
    private readonly Timer _look;
    private int _looking;

    /// <param name="nearby">The device's switch for its nearby radios; switched off, a permission allowed wakes none.</param>
    public MeshNodeLinkSource(IRadioMesh radio, ILogger<MeshNodeLinkSource>? logger = null, INodeNearby? nearby = null)
    {
        _radio = radio ?? throw new ArgumentNullException(nameof(radio));
        _logger = logger;
        _nearby = nearby;
        _radio.Changed += () => Changed?.Invoke();

        // The phone tells an app nothing when the person allows one of its permissions on the phone's own page. So
        // while one is still missing, look again every few seconds: when it has been allowed, bring up the radio it
        // was holding back — without a restart — and tell every connected app, whose settings then show it.
        _permissions = new PermissionWatch(ServicePermissions.Now);
        _look = new Timer(_ => Look(), null, LookEvery, LookEvery);
    }

    public event Action? Changed;

    public NodeLinkStatus Current
    {
        get
        {
            var carrying = _radio.LinkRadio;
            var radios = new List<AetherNetNodeService.RadioStatus>();
            foreach (var r in _radio.Radios)
            {
                var linked = _radio.IsLinked && string.Equals(r.Name, carrying, StringComparison.Ordinal);
                radios.Add(new AetherNetNodeService.RadioStatus(r.Name, r.Available, linked, linked ? _radio.LinkBandwidthBps : 0)
                {
                    Reason = r.Available ? null : r.Reason,
                    Fixable = !r.Available && r.Fixable,
                    NeedsPermission = r.NeedsPermission,
                });
            }
            return new NodeLinkStatus(_radio.IsLinked, _radio.IsLinked ? carrying : null, radios)
            {
                Permissions = _permissions.Current,
            };
        }
    }

    private void Look()
    {
        if (Interlocked.Exchange(ref _looking, 1) == 1) return;   // the last look is still bringing a radio up
        try
        {
            if (_permissions.Look())
            {
                var now = string.Join(", ", _permissions.Current.Select(p => $"{p.Name} {(p.Allowed ? "allowed" : "not allowed")}"));
                _logger?.LogInformation("permissions changed: {Permissions}", now);

                if (_permissions.NewlyAllowed && _nearby is not { On: false })
                {
                    _logger?.LogInformation("a permission was allowed — bringing up the radios it held back");
                    _radio.Link();
                }

                Changed?.Invoke();
            }

            if (!_permissions.Waiting)
            {
                _look.Change(Timeout.Infinite, Timeout.Infinite);
                _logger?.LogInformation("every permission is allowed — no longer looking");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "looking at AetherNetService's permissions failed");
        }
        finally
        {
            Volatile.Write(ref _looking, 0);
        }
    }
}
#endif

// SPDX-License-Identifier: MIT
#if ANDROID
using AetherNet.Mesh;
using AetherNetNodeService;        // NodeLinkStatus, RadioStatus
using AetherNetNodeService.Host;

namespace AetherNetService;

/// <summary>
/// Projects the node's <see cref="IRadioMesh"/> onto the node contract's <see cref="NodeLinkStatus"/> — the
/// presence seam the host reads, replacing <c>OfflineNodeLinkSource</c>. A report of what is linked right now
/// and over which radio, never a picker; the widest linked radio carries and this only says so.
/// </summary>
internal sealed class MeshNodeLinkSource : INodeLinkSource
{
    private readonly IRadioMesh _radio;

    public MeshNodeLinkSource(IRadioMesh radio)
    {
        _radio = radio ?? throw new ArgumentNullException(nameof(radio));
        _radio.Changed += () => Changed?.Invoke();
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
                });
            }
            return new NodeLinkStatus(_radio.IsLinked, _radio.IsLinked ? carrying : null, radios);
        }
    }
}
#endif

// SPDX-License-Identifier: MIT

using AetherNet.Node;
using AetherNet.Node.Host;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// Projects the app's <see cref="IRadioMesh"/> onto the node contract's <see cref="NodeLinkStatus"/> — the
/// presence seam the in-process node host reads. A report of what is linked right now and over which radio,
/// never a picker; the widest linked radio carries and this only says so.
/// </summary>
public sealed class SampleNodeLinkSource : INodeLinkSource
{
    private readonly IRadioMesh _radio;

    public SampleNodeLinkSource(IRadioMesh radio)
    {
        _radio = radio;
        _radio.Changed += () => Changed?.Invoke();
    }

    public event Action? Changed;

    public NodeLinkStatus Current
    {
        get
        {
            var carrying = _radio.LinkRadio;
            // Fully qualified: the sample has its own RadioStatus in this namespace; this is the node's.
            var radios = new List<AetherNet.Node.RadioStatus>();
            foreach (var r in _radio.Radios)
            {
                var linked = _radio.IsLinked && string.Equals(r.Name, carrying, StringComparison.Ordinal);
                radios.Add(new AetherNet.Node.RadioStatus(r.Name, r.Available, linked, linked ? _radio.LinkBandwidthBps : 0));
            }
            return new NodeLinkStatus(_radio.IsLinked, _radio.IsLinked ? carrying : null, radios);
        }
    }
}

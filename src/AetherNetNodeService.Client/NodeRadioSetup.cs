// SPDX-License-Identifier: MIT

using Mesh = AetherNet.Mesh;

namespace AetherNetNodeService.Client;

/// <summary>
/// The radios as AetherNetService has them, for an app that shows them.
/// </summary>
/// <remarks>
/// <para>
/// An app that connects to AetherNetService runs no radios of its own — they belong to the service. An app that asks
/// <i>itself</i> what radios it has is therefore answered "none", and a wizard built on that tells somebody holding a
/// phone with both Wi-Fi Direct and Bluetooth that radios only exist on a phone. Seen on a Pixel 7a, 2026-10-04. This
/// asks the node instead, and turns what it says into what a person is shown and what they may press.
/// </para>
/// <para>
/// Pressing it opens AetherNetService's own page in the phone's settings, because the phone keeps a permission per app
/// and one app cannot grant another's — and the service has no screen to ask from
/// (<see cref="IAetherNetServiceSettings"/>).
/// </para>
/// </remarks>
public sealed class NodeRadioSetup(IAetherNodeClient node, IAetherNetServiceSettings? settings = null) : Mesh.IRadioSetup
{
    private bool _sawRadios;

    /// <summary>
    /// True on a device whose radios belong to AetherNetService — which is every phone it runs on. Known for certain
    /// once the node has answered; until then, having the service's settings page is what says so.
    /// </summary>
    public bool IsPhone => _sawRadios || settings is not null;

    /// <inheritdoc />
    public async Task<IReadOnlyList<Mesh.RadioStatus>> CheckAsync()
    {
        NodeLinkStatus link;
        try
        {
            link = await node.GetLinkAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The service is not answering yet. Say nothing rather than saying there are no radios, which is the
            // mistake this class exists to undo.
            return [];
        }

        _sawRadios |= link.Radios.Count > 0;
        var list = new List<Mesh.RadioStatus>(link.Radios.Count);
        foreach (var radio in link.Radios)
        {
            list.Add(Show(radio));
        }

        return list;
    }

    /// <inheritdoc />
    public async Task<Mesh.RadioStatus> RequestAsync(string radioName)
    {
        // Only AetherNetService's own page can grant what its radios need, so this is the whole of the asking.
        settings?.Open(PermissionPage.AppInfo);

        foreach (var radio in await CheckAsync().ConfigureAwait(false))
        {
            if (string.Equals(radio.Name, radioName, StringComparison.OrdinalIgnoreCase))
            {
                return radio;
            }
        }

        return new Mesh.RadioStatus(radioName, Mesh.RadioState.Unknown, "This phone did not say.", null, Required: false);
    }

    /// <summary>One radio as a person is shown it: where it stands, why, and what the button says.</summary>
    private static Mesh.RadioStatus Show(RadioStatus radio)
    {
        var state = radio switch
        {
            { Available: true } => Mesh.RadioState.Ready,

            // Switched off by the person, which is theirs to undo in this app — not something to press here.
            { On: false } => Mesh.RadioState.Unsupported,

            { NeedsPermission: true } => Mesh.RadioState.NeedsPermission,
            { Fixable: true } => Mesh.RadioState.NeedsSystemToggle,
            _ => Mesh.RadioState.Unsupported,
        };

        var detail = radio.Reason is { Length: > 0 } reason
            ? reason
            : state == Mesh.RadioState.Ready ? "Working." : "Not on this phone.";

        var action = state switch
        {
            Mesh.RadioState.NeedsPermission => "Allow",
            Mesh.RadioState.NeedsSystemToggle => "Switch on",
            _ => null,
        };

        return new Mesh.RadioStatus(radio.Name, state, detail, action, Required: false);
    }
}

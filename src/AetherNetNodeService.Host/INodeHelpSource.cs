// SPDX-License-Identifier: MIT

namespace AetherNetNodeService.Host;

/// <summary>
/// Quiet help inside the node: the one session this person may have running, the guardians they chose, the options
/// they set, and the people they are a guardian for. The node host adapts it to the Quiet help calls on
/// <see cref="IAetherNodeClient"/>; a platform supplies it over the radios and the mesh.
///
/// <para>
/// Only <see cref="Start"/> begins a session, and it is only ever called for the person's own action on their own
/// phone — the node never starts one because of something it received, and no guardian can start one for them.
/// <see cref="MarkSafe"/> is the only thing that ends one.
/// </para>
/// </summary>
public interface INodeHelpSource
{
    /// <summary>What is running now, what the person chose, and who they are watching over.</summary>
    HelpReport Current { get; }

    /// <summary>
    /// Start a session, as the person's own action. False when the node cannot send it — no guardians chosen, or no
    /// radio — and <see cref="HelpState.Why"/> then says so. <see cref="HelpKind.Safe"/> starts nothing.
    /// </summary>
    bool Start(HelpKind kind);

    /// <summary>The person is safe. Nothing happens when no session is running.</summary>
    void MarkSafe();

    /// <summary>The guardians this person chooses, replacing the set they chose before.</summary>
    void SetGuardians(IReadOnlyList<HelpGuardian> guardians);

    /// <summary>
    /// Which triggers start a session, and which Bluetooth container carries the message. A container this device
    /// cannot use is not taken; <see cref="HelpState.Adverts"/> says which it can.
    /// </summary>
    void SetOptions(HelpTriggers triggers, HelpAdvertForm advert);

    /// <summary>Raised when <see cref="Current"/> changed and a connected app should redraw.</summary>
    event Action? Changed;
}

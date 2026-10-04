// SPDX-License-Identifier: MIT

using AetherNet.Identity;

namespace AetherNetNodeService;

/// <summary>
/// The surface a consumer application sees once it has bound to the device's one Aether node.
///
/// <para>
/// This is the cross-process face of <see cref="INodeIdentity"/> plus a messaging and presence slice —
/// a <b>subset</b>, deliberately. A bound app can ask the node to sign, to address and send, to read its
/// inbox, and to report whether it is linked. It cannot obtain the private key or a derived key. The one
/// thing that does cross is the recovery phrase, and only through <see cref="GetRecoveryPhraseAsync"/>: a
/// person has to be able to write it down, and AetherNetService has no screen to show it on. Security is
/// upstream — the phone's own lock — so the app asking confirms the owner with the phone before it asks.
/// Every member is <see cref="Task"/>-based and callbacks arrive through
/// <see cref="IAetherNodeEvents"/> rather than C# events, because delegates and <c>ValueTask</c> do not
/// survive a process boundary — the same interface serves an in-process host and a remote AIDL binding.
/// </para>
/// </summary>
public interface IAetherNodeClient
{
    /// <summary>This device's AetherTag — the same value every bound app on the device sees.</summary>
    /// <exception cref="AetherNodeException">
    /// <see cref="AetherNodeErrorCode.NodeUnavailable"/> when the node is present but locked, or
    /// <see cref="AetherNodeErrorCode.GrantRequired"/> when this app has not been linked. Never collapse
    /// "unavailable" into "absent": a caller told the identity is absent will try to mint a new one.
    /// </exception>
    Task<AetherNetTag> GetTagAsync(CancellationToken cancellationToken = default);

    /// <summary>The public half of the node's identity — publishable, and what the tag derives from.</summary>
    Task<byte[]> GetPublicKeyAsync(CancellationToken cancellationToken = default);

    /// <summary>Sign bytes as this device. Bytes in, signature out — the private key stays in the node.</summary>
    Task<byte[]> SignAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>Send a payload to another node, addressed by its AetherTag. The node resolves the wire
    /// address and owns the Signal session, so one pair keeps one ratchet across every app. When there is no
    /// session or no path yet the node holds the message and sends it when it can (<see cref="OutboundResult"/>
    /// says which). <paramref name="messageId"/> is the app's own id for the message: the node reports it back
    /// through <see cref="IAetherNodeEvents.OnDelivered"/> once the other side confirms receipt.</summary>
    Task<OutboundResult> SendAsync(AetherNetTag to, ReadOnlyMemory<byte> payload, Guid messageId, CancellationToken cancellationToken = default);

    /// <summary>Send without tracking delivery — the message gets an id the caller never sees.</summary>
    Task<OutboundResult> SendAsync(AetherNetTag to, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        => SendAsync(to, payload, Guid.NewGuid(), cancellationToken);

    /// <summary>
    /// The people this app wants the node to keep reachable — the radios connect to them. Replaces the set the
    /// app gave before. A node is a network cable: it keeps no address book of its own, so an app hands over
    /// the contacts it has (one app today; a set per app when a second binds).
    /// </summary>
    Task MeetAsync(IReadOnlyList<NodeContact> contacts, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <summary>The most recent inbound messages addressed to this device.</summary>
    Task<IReadOnlyList<InboundMessage>> GetInboxAsync(int limit = 50, CancellationToken cancellationToken = default);

    /// <summary>Whether the node is reaching anyone right now, and over which radios. A report, not a picker.</summary>
    Task<NodeLinkStatus> GetLinkAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// This device's identity as its 24-word recovery phrase, for a person to write down — the only way back
    /// to the same AetherTag on a new phone.
    /// </summary>
    /// <remarks>
    /// The one request that hands out key material. AetherNetService keeps no gate of its own; access is
    /// gated upstream, by the phone. So the app asking must have the phone confirm its owner first — the
    /// phone's own fingerprint, PIN or pattern — and must show the words and keep nothing.
    /// </remarks>
    /// <exception cref="AetherNodeException">
    /// <see cref="AetherNodeErrorCode.NodeUnavailable"/> when the identity is there but locked;
    /// <see cref="AetherNodeErrorCode.IdentityAbsent"/> when this device has none yet.
    /// </exception>
    Task<string> GetRecoveryPhraseAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This service does not hand out the recovery phrase.");

    /// <summary>
    /// Switch AetherNet's nearby radios — Bluetooth, Wi-Fi Direct, Wi-Fi Aware, the meeting on the Wi-Fi the phone is
    /// on — on or off for this whole device. Off, only the internet leg runs: every app on the phone still reaches
    /// people over ordinary data, and no nearby radio wakes. The state is <see cref="NodeLinkStatus.NearbyOn"/>.
    /// </summary>
    /// <remarks>
    /// A setting of the device, like the network cable the service is: whichever app the person changes it in
    /// changes it for all of them. AetherNetService applies it by restarting, so connected apps lose it for a moment
    /// and reconnect by themselves.
    /// </remarks>
    Task SetNearbyAsync(bool on, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This service cannot switch its nearby radios.");

    /// <summary>
    /// Switch one of the device's radios — by its name in <see cref="NodeLinkStatus.Radios"/> — on or off, for this
    /// whole device. Every radio is on until the person switches it off; the state is <see cref="RadioStatus.On"/>.
    /// </summary>
    /// <remarks>
    /// Like the nearby switch, a setting of the device: changed in one app, changed for all of them. AetherNetService
    /// applies it by restarting, so connected apps lose it for a moment and reconnect by themselves.
    /// </remarks>
    Task SetRadioAsync(string radio, bool on, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This service cannot switch one radio at a time.");

    /// <summary>
    /// Quiet help as it stands: what this person is sending, what they chose, and the people they are a guardian
    /// for who are asking for help now.
    /// </summary>
    Task<HelpReport> GetHelpAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(HelpReport.None);

    /// <summary>
    /// Start Quiet help on this device, as the person's own action: <see cref="HelpKind.Help"/> asks the guardians
    /// they chose for help, <see cref="HelpKind.Walk"/> only shares the way. The phone stays quiet either way. False
    /// when the node cannot send it — no guardians chosen yet, or no radio — and <see cref="HelpState.Why"/> says so.
    /// </summary>
    /// <remarks>
    /// Only the person starts this, on their own phone. Nothing that arrives from the mesh or the air can, and a
    /// guardian cannot start it for them.
    /// </remarks>
    Task<bool> StartHelpAsync(HelpKind kind, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This service does not carry Quiet help.");

    /// <summary>
    /// The person says they are safe, or has arrived. The only thing that stops a help session: the message says
    /// "safe" for a minute so guardians nearby hear it, and then it is over.
    /// </summary>
    Task MarkSafeAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This service does not carry Quiet help.");

    /// <summary>
    /// The guardians this person chooses to ask for help, replacing the set they chose before. Changing the set makes
    /// a new help key, so a guardian taken off reads nothing sent afterwards.
    /// </summary>
    Task SetHelpGuardiansAsync(IReadOnlyList<HelpGuardian> guardians, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This service does not carry Quiet help.");

    /// <summary>
    /// Which of their own actions start Quiet help, and which Bluetooth container carries it. A container this
    /// device cannot use is refused, and <see cref="HelpState.Adverts"/> says why.
    /// </summary>
    Task SetHelpOptionsAsync(HelpTriggers triggers, HelpAdvertForm advert, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This service does not carry Quiet help.");

    /// <summary>
    /// Subscribe to inbound messages, link changes, and grant changes. Dispose the returned handle to stop.
    /// A callback interface rather than a C# event so the subscription proxies across a process boundary.
    /// </summary>
    IDisposable Subscribe(IAetherNodeEvents listener);
}

/// <summary>
/// The callbacks a bound consumer receives from the node. Delivered on an unspecified thread; an
/// implementation that touches UI must marshal. Modelled as an interface, not C# events, so it can be
/// mirrored by an AIDL <c>oneway</c> callback across processes.
/// </summary>
public interface IAetherNodeEvents
{
    /// <summary>A message addressed to this device has arrived.</summary>
    void OnInbound(InboundMessage message);

    /// <summary>The node's link state changed — a radio came up or down, a peer linked or dropped.</summary>
    void OnLinkChanged(NodeLinkStatus status);

    /// <summary>This app's grant changed — granted, revoked, or reset to awaiting.</summary>
    void OnGrantChanged(GrantState state);

    /// <summary>The other side confirmed receipt of a message this app sent, by the id it was sent with.</summary>
    void OnDelivered(Guid messageId) { }

    /// <summary>
    /// Quiet help changed: this person started or ended a session, or somebody they are a guardian for is asking for
    /// help, has moved, or is safe.
    /// </summary>
    void OnHelpChanged(HelpReport report) { }
}

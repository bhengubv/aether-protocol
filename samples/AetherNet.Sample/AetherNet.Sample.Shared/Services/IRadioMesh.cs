// SPDX-License-Identifier: MIT

using AetherNet.Rendezvous;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// One physical radio the app can link over, and whether it's usable on this device.
/// <paramref name="Reason"/> says why not, so the UI can explain instead of just greying a chip out.
/// </summary>
/// <summary>
/// A radio as the picker sees it. <paramref name="Fixable"/> separates "you can switch this on" from
/// "this phone does not have one" — both are unavailable, but only one is worth offering a tap for.
/// </summary>
public sealed record RadioInfo(string Name, bool Available, string? Reason = null, bool Fixable = false);

/// <summary>
/// The app's over-the-air link across ALL of AetherNet's radios — Wi-Fi Direct, BLE, NFC,
/// NearLink, LoRa — each native inside this one APK. The UI picks a radio and links over it.
/// On non-phone hosts this is a no-op (radios are physical).
/// </summary>
public interface IRadioMesh
{
    /// <summary>This device's AetherTag — one identity shared across every radio.</summary>
    string LocalTag { get; }

    /// <summary>Every radio the app carries, with per-device availability.</summary>
    IReadOnlyList<RadioInfo> Radios { get; }

    /// <summary>The currently-selected radio's name — what the picker shows.</summary>
    string SelectedRadio { get; }

    /// <summary>
    /// The radio a packet actually leaves on right now.
    ///
    /// <para>
    /// Not the same as <see cref="SelectedRadio"/>, and the difference matters to anyone reading the
    /// screen. Sending takes the widest linked radio, so the moment Wi-Fi Direct comes up alongside
    /// BLE every byte moves to Wi-Fi Direct while the picker still says BLE — and a banner reading
    /// "connected over BLE" is then simply false. Say what is carrying the traffic.
    /// </para>
    /// </summary>
    string LinkRadio { get; }

    /// <summary>
    /// How hard the carrying link is working right now, 0 (comfortable) to 1 (failing).
    /// </summary>
    /// <remarks>
    /// The honest signal for sizing media. Unlike a bandwidth figure it needs no capacity to be known
    /// — it rises when sends start queueing, which happens before anything is lost, and every
    /// capacity figure this app has trusted turned out to be fiction.
    /// </remarks>
    double LinkStrain => 0;

    /// <summary>
    /// Roughly what the radio currently carrying traffic can move, in bits per second — 0 when
    /// nothing is linked or the radio will not say.
    ///
    /// <para>
    /// Exposed so media can size itself to the link rather than assume one. A codec that always asks
    /// for the same bitrate is fine right up until the link changes underneath it, which is exactly
    /// what automatic radio handover does on purpose.
    /// </para>
    /// </summary>
    long LinkBandwidthBps { get; }

    /// <summary>Whether the selected radio is usable on this device.</summary>
    bool IsSupported { get; }

    /// <summary>True once a peer has linked over the selected radio.</summary>
    bool IsLinked { get; }

    /// <summary>
    /// Who is on the other end of the link — their AetherTag once it is known, and until then the
    /// rotating wire address the radio saw in the handshake. Null when nothing is linked.
    /// </summary>
    string? PeerTag { get; }

    /// <summary>
    /// Name the peer on the current link.
    ///
    /// <para>
    /// A radio never learns who it is talking to. The long-term identity deliberately does not travel
    /// in clear — the handshake carries a rotating address — so the radio can say "someone is here"
    /// and nothing more. The result was a chat screen insisting you were <b>"connected to someone else"</b>
    /// while delivering your messages to exactly the right person.
    /// </para>
    ///
    /// <para>
    /// The identity arrives inside the session, so only the layer that opens the session can supply it.
    /// It must call this <b>after a message from that peer has actually decrypted</b>, never on the
    /// strength of the claim in a packet header: a header is a claim anyone can make, whereas ciphertext
    /// that opens under a peer's ratchet could only have come from them.
    /// </para>
    /// </summary>
    void IdentifyPeer(string aetherTag);

    /// <summary>Raised whenever the log or link state changes; the UI re-renders on it.</summary>
    event Action? Changed;

    /// <summary>A point-in-time snapshot of the radio log, oldest first.</summary>
    IReadOnlyList<string> Log { get; }

    /// <summary>Choose which radio to link over.</summary>
    void SelectRadio(string name);

    /// <summary>Bring the selected radio up and link to the other phone.</summary>
    void Link();

    /// <summary>
    /// Bring every radio up to meet one particular person.
    /// </summary>
    /// <remarks>
    /// <para>
    /// All of them at once, and none waiting on another. Which one ends up carrying the traffic is not
    /// decided here and is never decided by the person holding the phone — the widest linked radio
    /// takes it, and hands it on when a wider one appears.
    /// </para>
    /// <para>
    /// Deliberately without a default body. One was tried, and every call landed on it rather than on
    /// the mesh that had a real implementation — so the radios came up for everybody, nothing was
    /// told who it was meeting, and the whole thing looked like it was working. A member with no
    /// default cannot be quietly not implemented.
    /// </para>
    /// </remarks>
    void Link(Meeting meeting);

    /// <summary>Send one real MeshPacket carrying <paramref name="text"/> to the linked peer.</summary>
    Task SendTestAsync(string text);

    /// <summary>
    /// Send one raw, already-serialized <c>MeshPacket</c> to the linked peer over the selected radio.
    /// This is the pipe the mesh-web rides — the directory + content planes push their packets through
    /// it. Returns false if no peer is linked / the send failed.
    /// </summary>
    Task<bool> SendPacketAsync(byte[] packetBytes);

    /// <summary>
    /// Send in a named lane. Real-time overtakes everything; bulk waits for a gap.
    /// </summary>
    /// <remarks>
    /// Hosts with one queue send in order and are no worse than they were. Where lanes exist, this is
    /// what stops a 36KB attachment chunk holding the wire while voice frames expire behind it.
    /// </remarks>
    Task<bool> SendPacketAsync(byte[] packetBytes, SendLane lane) => SendPacketAsync(packetBytes);

    /// <summary>Raised with a raw, serialized <c>MeshPacket</c> that arrived over a radio.</summary>
    event Action<byte[]>? PacketReceived;

    /// <summary>Tear the radios down.</summary>
    void Stop();
}

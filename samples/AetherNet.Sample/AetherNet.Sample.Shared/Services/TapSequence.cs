// SPDX-License-Identifier: MIT

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// Two answers in one touch.
///
/// <para>
/// Android reads the first NDEF record of a tap and ignores everything after it, so one tap cannot
/// both put a phone on a network <i>and</i> tell it where to go once it is there. That has been the
/// wall all along: the credentials work — measured, repeatedly, a stock handset joins with nobody
/// typing anything — and then their phone sits on the network with nothing to fetch, because the
/// sign-in sheet needs a port Android will not give an application.
/// </para>
///
/// <para>
/// <b>But a tag is a conversation, not a file.</b> We know the exact instant a reader finishes taking
/// a message, because the tag raises an event on the last byte. So the message can change between
/// reads: the first read hands over the network, and the moment it completes the tag is holding the
/// destination instead. If the reader looks again while the phones are still together, it gets the
/// second thing — one unbroken touch, two answers, and nobody reads or types an address.
/// </para>
///
/// <para>
/// This holds that sequence. It lives here rather than in the Android service so the whole progression
/// can be played out in a test, including the parts that only happen when somebody keeps two phones
/// pressed together for a few seconds longer than usual.
/// </para>
/// </summary>
public sealed class TapSequence
{
    /// <summary>What the tap is offering at any moment.</summary>
    public enum Step
    {
        /// <summary>Nothing is being handed over.</summary>
        Idle,

        /// <summary>The network, so their phone can reach us at all.</summary>
        Network,

        /// <summary>Where to go now that it can.</summary>
        Destination,

        /// <summary>Both have been taken. Nothing more to give this tap.</summary>
        Done,
    }

    private byte[]? _network;
    private byte[]? _destination;

    /// <summary>Where the sequence currently stands.</summary>
    public Step At { get; private set; } = Step.Idle;

    /// <summary>Raised whenever what the tag is holding changes, so it can be said out loud.</summary>
    public event Action<Step>? Moved;

    /// <summary>
    /// Load the sequence.
    /// </summary>
    /// <param name="network">Credentials in the format stock Android acts on.</param>
    /// <param name="destination">
    ///   Where to send them afterwards, or null to offer only the network — which is the honest thing
    ///   to do when there is nowhere useful to send them yet.
    /// </param>
    public void Arm(byte[]? network, byte[]? destination)
    {
        _network = network is { Length: > 0 } ? network : null;
        _destination = destination is { Length: > 0 } ? destination : null;

        At = _network is not null ? Step.Network
            : _destination is not null ? Step.Destination
            : Step.Idle;

        Moved?.Invoke(At);
    }

    /// <summary>Stop offering anything.</summary>
    public void Disarm()
    {
        _network = null;
        _destination = null;
        At = Step.Idle;
        Moved?.Invoke(At);
    }

    /// <summary>What a reader touching us right now would take.</summary>
    public byte[]? Offer => At switch
    {
        Step.Network => _network,
        Step.Destination => _destination,
        _ => null,
    };

    /// <summary>
    /// A reader has taken the whole of whatever was being offered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the hinge. Called on the last byte of a completed read, it advances what the tag is
    /// holding so the very next read — possibly milliseconds later, in the same touch — gets the next
    /// thing rather than the same thing again.
    /// </para>
    /// <para>
    /// Advancing only on a COMPLETED read matters. A reader that starts and gives up halfway must get
    /// the same message when it tries again, or a phone that has not joined a network is handed an
    /// address on a network it cannot reach.
    /// </para>
    /// </remarks>
    public void Taken()
    {
        var was = At;

        At = At switch
        {
            Step.Network when _destination is not null => Step.Destination,
            Step.Network => Step.Done,
            Step.Destination => Step.Done,
            _ => At,
        };

        if (At != was) Moved?.Invoke(At);
    }

    /// <summary>
    /// The phones came apart.
    /// </summary>
    /// <remarks>
    /// Deliberately does NOT reset the sequence. Two phones held by hand separate and touch again
    /// constantly, and a reader that has already taken the credentials should get the destination on
    /// its second approach rather than the credentials over and over. The sequence is a property of
    /// the handover, not of one continuous field.
    /// </remarks>
    public void Parted() { }

    /// <summary>Whether there is anything left to hand over.</summary>
    public bool HasMore => Offer is not null;

    /// <summary>What is happening, in words for the person holding the phone.</summary>
    public string Describe() => At switch
    {
        Step.Network => "touch — their phone joins your network",
        Step.Destination => "joined. Touch again — their phone opens the app",
        Step.Done => "done — it is on its way to them",
        _ => "nothing to hand over",
    };
}

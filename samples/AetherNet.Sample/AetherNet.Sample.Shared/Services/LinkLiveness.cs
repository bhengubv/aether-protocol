// SPDX-License-Identifier: MIT

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// Deciding whether the phone on the other end of a radio link is still there.
///
/// <para>
/// Silence is not death — most of what a mesh sends needs no reply, so a quiet link is asked outright
/// rather than assumed gone. Only an answer counts as an answer: a frame that the peer's <i>app</i>
/// composed and sent back.
/// </para>
///
/// <para>
/// It is tempting to also count a write the peer's Bluetooth stack acknowledged, since it seems to say
/// the same thing. It does not, and the difference matters. Two phones were watched holding a link
/// where every write completed successfully on both sides while nothing whatsoever reached either app:
/// the radios were talking and the software behind them was not. Counting those completions as proof of
/// life keeps that link propped up forever. Tearing it down and building a new one takes five seconds
/// and actually works.
/// </para>
///
/// <para>
/// The opposite mistake is real too. Tearing down a working link loses everything already handed to it,
/// receipts included, so the sender is told a message failed while the other phone is reading it — hence
/// a window wide enough that an ordinary payload in flight is never what runs the clock out.
/// </para>
/// </summary>
public sealed class LinkLiveness
{
    /// <summary>Quiet this long and we ask outright. Asking also keeps Android from reaping an idle link.</summary>
    public static readonly TimeSpan PingAfter = TimeSpan.FromSeconds(8);

    /// <summary>
    /// No answer within this long and the link is gone. Wide enough to outlast an ordinary message —
    /// a full-sized BLE attribute write measured just over a second on a P30 Lite, GATT operations
    /// serialise, and a message is several frames, so a payload in flight must never be what runs the
    /// clock out. Narrow enough that a link which has quietly stopped carrying anything is rebuilt in
    /// seconds rather than sat on.
    /// </summary>
    public static readonly TimeSpan PongWithin = TimeSpan.FromSeconds(15);

    private DateTime _lastProofUtc = DateTime.MinValue;
    private DateTime _pingSentUtc = DateTime.MinValue;

    /// <summary>Is a question out that nothing has answered yet?</summary>
    public bool PingOutstanding { get; private set; }

    /// <summary>
    /// A frame arrived from the peer's app. The only thing that counts — see the note above on why a
    /// write the peer's radio acknowledged does not.
    /// </summary>
    public void RecordInbound(DateTime nowUtc)
    {
        _lastProofUtc = nowUtc;
        PingOutstanding = false;
    }

    /// <summary>Note that a ping has gone out, so the answer can be timed.</summary>
    public void NotePingSent(DateTime nowUtc)
    {
        PingOutstanding = true;
        _pingSentUtc = nowUtc;
    }

    /// <summary>Time to ask? Only when the link has gone quiet and nothing is already outstanding.</summary>
    public bool ShouldPing(DateTime nowUtc) =>
        !PingOutstanding && nowUtc - _lastProofUtc >= PingAfter;

    /// <summary>Has this link gone? Only if we asked and the peer's app never answered.</summary>
    public bool IsLost(DateTime nowUtc) =>
        PingOutstanding && nowUtc - _pingSentUtc >= PongWithin;

    /// <summary>Forget this link entirely; the next one starts with a clean slate.</summary>
    public void Reset()
    {
        PingOutstanding = false;
        _lastProofUtc = DateTime.MinValue;
        _pingSentUtc = DateTime.MinValue;
    }
}

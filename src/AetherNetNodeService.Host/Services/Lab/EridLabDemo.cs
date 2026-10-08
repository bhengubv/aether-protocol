// SPDX-License-Identifier: MIT

using AetherNet.Identity;
using AetherNet.Security.Services;

namespace AetherNetNodeService.Host.Lab;

/// <summary>
/// The rotating address: the ERID page's demo, run here with the real EridDirectory while the page shows it.
/// </summary>
/// <remarks>
/// The page's own code, moved here as it was. Two real identities: Nomsa presents the rotating address; Sipho was handed
/// her secret routing key inside an established session (RememberPeer), which is the only thing that lets him keep up.
/// </remarks>
public sealed class EridLabDemo
{
    private const int Epoch = EphemeralRoutingId.DefaultEpochSeconds; // 900s = 15 min

    private readonly EridDirectory _nomsa;
    private readonly EridDirectory _sipho;

    public EridLabDemo()
    {
        // Nomsa's routing key is derived from her identity SECRET — never her public key, or the whole
        // rotation schedule would be computable by anyone watching.
        var (nomsaSecret, _) = Ed25519SigningService.GenerateKeyPair();
        var nomsaKey = EphemeralRoutingId.DeriveRoutingKey(nomsaSecret);
        _nomsa = new EridDirectory(nomsaKey);

        // Sipho has his own identity, and has learned Nomsa's routing key in-session.
        var (siphoSecret, _) = Ed25519SigningService.GenerateKeyPair();
        _sipho = new EridDirectory(EphemeralRoutingId.DeriveRoutingKey(siphoSecret));
        _sipho.RememberPeer("Nomsa", nomsaKey);

        EpochNow = EphemeralRoutingId.EpochFor(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Recompute();
    }

    /// <summary>Raised when what the page shows has changed.</summary>
    public event Action? Changed;

    /// <summary>The window being looked at.</summary>
    public long EpochNow { get; private set; }
    public string? ResolveNow { get; private set; }
    public string? ResolvePrev { get; private set; }
    public string? PrevErid { get; private set; }

    // Anchor the demo clock to real "now", then let the buttons walk it forward and back a window at a time.
    private long NowSeconds => EpochNow * Epoch;

    public string AliceErid => _nomsa.MyErid(NowSeconds);
    public string WindowLabel => $"{Fmt(NowSeconds)} – {Fmt(NowSeconds + Epoch)} UTC";
    public string Clock => Fmt(NowSeconds) + " UTC";
    public string RotatesIn => "15 min";

    /// <summary>The last six windows, newest first.</summary>
    public IReadOnlyList<Window> History
    {
        get
        {
            var list = new List<Window>();
            for (var k = 0; k < 6; k++)
            {
                var e = EpochNow - k;
                if (e < 0) break;
                list.Add(new Window(e, _nomsa.MyErid(e * Epoch), e == EpochNow));
            }

            return list;
        }
    }

    /// <summary>One window: its number, Nomsa's address in it, and whether it is the one being looked at.</summary>
    public sealed record Window(long Epoch, string Erid, bool IsNow);

    public void Forward()
    {
        EpochNow++;
        Recompute();
        Changed?.Invoke();
    }

    public void Back()
    {
        if (EpochNow > 0)
        {
            EpochNow--;
            Recompute();
        }

        Changed?.Invoke();
    }

    private void Recompute()
    {
        var now = NowSeconds;
        var prev = now - Epoch;
        PrevErid = _nomsa.MyErid(prev);
        // Sipho reverse-resolves the wire value back to the peer behind it, at each window's own time.
        ResolveNow = _sipho.ResolvePeer(_nomsa.MyErid(now), now);
        ResolvePrev = _sipho.ResolvePeer(PrevErid, prev);
    }

    private static string Fmt(long unixSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToString("HH:mm");
}

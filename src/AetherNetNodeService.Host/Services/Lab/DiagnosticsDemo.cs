// SPDX-License-Identifier: MIT

using AetherNet.Identity;
using AetherNet.Mesh;
using AetherNet.Rendezvous;
using AetherNet.Security.Identity;

namespace AetherNetNodeService.Host.Lab;

/// <summary>
/// A developer diagnostics surface for exercising protocol primitives across two real devices: the Diagnostics page's
/// demo, run here with this phone's real identity while the page shows it.
/// </summary>
/// <remarks>
/// Not a shipping feature — it is driven over the devtools bridge to test the two-node primitives with each phone's real
/// keystore identity. The page's own code, moved here as it was.
/// </remarks>
public sealed class DiagnosticsDemo(IIdentityService me, INodeIdentity node)
{
    // Held so the verifier checks a proof against the exact challenge it issued (stateless-caller model).
    private OwnershipChallenge? _issued;

    /// <summary>Raised when what the page shows has changed.</summary>
    public event Action? Changed;

    public string PeerTag { get; set; } = "";
    public string Rv { get; private set; } = "";
    public string IStart { get; private set; } = "";
    public string ChallengeOut { get; private set; } = "";
    public string ChallengeIn { get; set; } = "";
    public string ProofOut { get; private set; } = "";
    public string ProofIn { get; set; } = "";
    public string ExpectTag { get; set; } = "";
    public string VerifyOut { get; private set; } = "";

    public void Derive()
    {
        var m = Meeting.With(me.AetherTag, PeerTag.Trim());
        if (m is { } meeting) { Rv = meeting.Rendezvous; IStart = meeting.IStart.ToString().ToLowerInvariant(); }
        else { Rv = "(none)"; IStart = ""; }
        Changed?.Invoke();
    }

    public void Issue()
    {
        _issued = OwnershipChallenge.Issue("diag", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        ChallengeOut = Enc(_issued);
        Changed?.Invoke();
    }

    public async Task Prove()
    {
        var challenge = DecChallenge(ChallengeIn.Trim());
        if (challenge is null)
        {
            ProofOut = "(bad challenge)";
            Changed?.Invoke();
            return;
        }

        var proof = await OwnershipValidator.ProveAsync(node, challenge);
        ProofOut = Enc(proof);
        Changed?.Invoke();
    }

    public void Verify()
    {
        var proof = DecProof(ProofIn.Trim());
        if (_issued is null || proof is null || !AetherNetTag.TryParse(ExpectTag.Trim(), out var tag))
        {
            VerifyOut = "(bad input)";
            Changed?.Invoke();
            return;
        }

        var ok = OwnershipValidator.Verify(_issued, proof, tag, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        VerifyOut = ok ? "VALID — owns the tag" : "INVALID";
        Changed?.Invoke();
    }

    // ── tiny hex/JSON codec so the challenge/proof can be shuttled between the two phones ──
    private static string Hex(byte[] b) => Convert.ToHexString(b);
    private static byte[] Unhex(string s) => Convert.FromHexString(s);

    private static string Enc(OwnershipChallenge c) =>
        $"{{\"n\":\"{Hex(c.Nonce)}\",\"t\":{c.IssuedAtMs},\"p\":\"{c.Purpose}\"}}";
    private static string Enc(OwnershipProof p) =>
        $"{{\"n\":\"{Hex(p.Nonce)}\",\"k\":\"{Hex(p.PublicKey)}\",\"s\":\"{Hex(p.Signature)}\"}}";

    private static OwnershipChallenge? DecChallenge(string s)
    {
        try
        {
            using var d = System.Text.Json.JsonDocument.Parse(s);
            var r = d.RootElement;
            return new OwnershipChallenge(Unhex(r.GetProperty("n").GetString()!), r.GetProperty("t").GetInt64(), r.GetProperty("p").GetString()!);
        }
        catch { return null; }
    }

    private static OwnershipProof? DecProof(string s)
    {
        try
        {
            using var d = System.Text.Json.JsonDocument.Parse(s);
            var r = d.RootElement;
            return new OwnershipProof(Unhex(r.GetProperty("n").GetString()!), Unhex(r.GetProperty("k").GetString()!), Unhex(r.GetProperty("s").GetString()!));
        }
        catch { return null; }
    }
}

// SPDX-License-Identifier: MIT

using AetherNet.Identity;
using AetherNet.Security.Services;

namespace AetherNetNodeService.Host.Lab;

/// <summary>
/// Memorable names, owned locally, answerable to no registry: the Petnames page's demo, run here with the real
/// PetnameRegistry while the page shows it.
/// </summary>
/// <remarks>
/// The page's own code, moved here as it was: two registries ("me" and "the peer"), four real tags to name, pins that
/// nothing gossiped in can override, and proposals kept until pinned or rejected.
/// </remarks>
public sealed class PetnamesLabDemo
{
    private readonly PetnameRegistry _me = new();
    private readonly PetnameRegistry _peer = new();
    private readonly string[] _people;

    public PetnamesLabDemo()
    {
        // Four real tags to name, plus identities for "me" and "the peer" (a proposal carries who made it).
        _people = Enumerable.Range(0, 4).Select(_ => RandomTag()).ToArray();
        MyTag = RandomTag();
        PeerTag = RandomTag();
        FormTag = _people[0];

        // A little starting state: two names I have pinned, and one a peer has proposed to me.
        _me.Pin(_people[0], "Nomsa");
        _me.Pin(_people[1], "Sipho");
        _me.Propose(_people[2], "Thabo", PeerTag);

        // The peer already has its OWN name pinned for people[0] — the collision that proves pins win
        // over anything gossiped in.
        _peer.Pin(_people[0], "Mama");
    }

    /// <summary>Raised when what the page shows has changed.</summary>
    public event Action? Changed;

    public IReadOnlyList<string> People => _people;
    public string MyTag { get; }
    public string PeerTag { get; }
    public string FormTag { get; set; }
    public string FormName { get; set; } = "";
    public string ResolveName { get; set; } = "";
    public string ResolveOut { get; private set; } = null!;
    public string? Note { get; private set; }
    public string? GossipOut { get; private set; }

    /// <summary>My names, as the page lists them.</summary>
    public Registry Me => new(View(_me));

    /// <summary>The peer's names, as the page lists them.</summary>
    public Registry Peer => new(View(_peer));

    /// <summary>One registry's bindings.</summary>
    public sealed record Registry(IReadOnlyList<Binding> All);

    /// <summary>One binding: the tag, the name, whether it is the person's own, and what kind it is.</summary>
    public sealed record Binding(string Tag, string Name, bool Pinned, string Badge);

    public void Pin()
    {
        var ok = _me.Pin(FormTag, FormName);
        Note = ok ? $"Pinned “{FormName.Trim()}”. Yours now — nothing overrides it."
                  : "Refused — not a valid tag, or the name is blank/too long.";
        FormName = "";
        Changed?.Invoke();
    }

    public void ProposeAsPeer()
    {
        var ok = _me.Propose(FormTag, FormName, PeerTag);
        Note = ok ? $"Proposal “{FormName.Trim()}” stored — a suggestion, kept until you pin or reject it."
                  : "Refused — a proposal can't override a name you have already pinned. Your choice stands.";
        FormName = "";
        Changed?.Invoke();
    }

    public void Reject(string tag)
    {
        _me.Reject(tag);
        Note = "Dropped that binding.";
        Changed?.Invoke();
    }

    public void Resolve()
    {
        var tag = _me.ResolveName(ResolveName);
        ResolveOut = tag is null
            ? $"“{ResolveName.Trim()}” → no unambiguous match (unknown, or two bindings share it)."
            : $"“{ResolveName.Trim()}” → {tag}";
        Changed?.Invoke();
    }

    public void Gossip()
    {
        // Offer my pins as proposals; the peer decides what to keep. Its own pin for the shared tag is
        // untouched, because ImportProposals routes each entry through Propose.
        var bundle = _me.ExportProposals(MyTag);
        var stored = _peer.ImportProposals(bundle);
        var kept = _peer.NameFor(_people[0]);
        GossipOut = $"Offered {bundle.Count} name(s); the peer stored {stored} as proposals and kept its own “{kept}” for the shared tag.";
        Changed?.Invoke();
    }

    private static List<Binding> View(PetnameRegistry registry)
        => registry.All.Select(p => new Binding(p.Tag, p.Name, IsPin(p), Badge(p))).ToList();

    private static bool IsPin(Petname p) => p.Source is PetnameSource.Pinned or PetnameSource.Seed;

    private static string Badge(Petname p) => p.Source switch
    {
        PetnameSource.Pinned => "pinned",
        PetnameSource.Seed => "seed",
        _ => "proposed",
    };

    private static string RandomTag()
    {
        var (_, pub) = Ed25519SigningService.GenerateKeyPair();
        return AetherNetTag.FromPublicKey(pub).Value;
    }
}

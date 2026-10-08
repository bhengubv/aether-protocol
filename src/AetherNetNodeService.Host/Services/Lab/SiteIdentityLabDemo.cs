// SPDX-License-Identifier: MIT

using AetherNet.Identity;
using AetherNet.Security.Services;

namespace AetherNetNodeService.Host.Lab;

/// <summary>
/// One identity, a different handle for every site: the Site identity page's demo, run here with the real
/// SiteIdentityDerivation while the page shows it.
/// </summary>
/// <remarks>
/// The page's own code, moved here as it was. A real reference NodeIdentity, its key minted into an in-memory store on
/// first ask — the same interface the phone's keystore-backed identity implements, so ForSiteAsync runs exactly as it
/// does on a device; only where the key rests is swapped for the demo.
/// </remarks>
public sealed class SiteIdentityLabDemo
{
    private readonly INodeIdentity _node = new NodeIdentity(new MemoryIdentityStore());
    private readonly List<Site> _sites = new();
    private int _autoSite = 1;

    public SiteIdentityLabDemo()
    {
        // What the page did as it opened, done here before the page is answered.
        Task.Run(async () =>
        {
            MasterTag = (await _node.GetOrMintAsync()).Value;

            // A few card origins to start with, each a real AetherTag of some author.
            await AddSite("Blue Market", RandomTag());
            await AddSite("Ward 7 notices", RandomTag());
            await AddSite("Clinic booking", RandomTag());
        }).GetAwaiter().GetResult();
    }

    /// <summary>Raised when what the page shows has changed.</summary>
    public event Action? Changed;

    public string MasterTag { get; private set; } = "…";
    public string NewSite { get; set; } = "";
    public string? AddNote { get; private set; }
    public bool CanAddTyped => AetherNetTag.TryParse(NewSite, out _);

    /// <summary>The sites visited, as the page lists them.</summary>
    public IReadOnlyList<SiteView> Sites => _sites.Select(s => new SiteView(s.Name, s.SiteTag, s.Pseudonym, s.Recognised)).ToList();

    /// <summary>One site: its name, its author's tag, this reader's handle there, and whether it knew them again.</summary>
    public sealed record SiteView(string Name, string SiteTag, string Pseudonym, bool Recognised);

    private sealed class Site
    {
        public required string Name { get; init; }
        public required string SiteTag { get; init; }
        public required string Pseudonym { get; set; }
        public bool Recognised { get; set; }
    }

    public async Task AddTyped()
    {
        if (!AetherNetTag.TryParse(NewSite, out var tag)) return;
        var canonical = tag.Value;
        if (_sites.Any(s => s.SiteTag == canonical))
        {
            AddNote = $"Already visited {canonical} — its handle is the same one shown below.";
            Changed?.Invoke();
            return;
        }

        await AddSite("Site " + canonical[..5], canonical);
        NewSite = "";
        AddNote = null;
        Changed?.Invoke();
    }

    public async Task AddRandom()
    {
        AddNote = null;
        await AddSite($"Random site {_autoSite++}", RandomTag());
        Changed?.Invoke();
    }

    private async Task AddSite(string name, string siteTag)
    {
        // The real derivation: (this device's identity, that site) → a stable, unlinkable per-site handle.
        var identity = await SiteIdentityDerivation.ForSiteAsync(_node, siteTag);
        _sites.Add(new Site { Name = name, SiteTag = siteTag, Pseudonym = identity.Pseudonym });
    }

    public async Task RevisitAll()
    {
        // Return to each site and re-derive. A site recognises a returning reader precisely because the
        // handle it derives is the same one as last time — proven here rather than asserted.
        foreach (var s in _sites)
        {
            var again = await SiteIdentityDerivation.ForSiteAsync(_node, s.SiteTag);
            s.Recognised = again.Pseudonym == s.Pseudonym;
        }

        Changed?.Invoke();
    }

    private static string RandomTag()
    {
        var (_, pub) = Ed25519SigningService.GenerateKeyPair();
        return AetherNetTag.FromPublicKey(pub).Value;
    }

    /// <summary>An in-memory INodeIdentityStore — the demo's stand-in for the phone's keystore.</summary>
    private sealed class MemoryIdentityStore : INodeIdentityStore
    {
        private byte[]? _key;
        public bool Exists => _key is not null;
        public byte[]? Load() => _key;
        public void Save(byte[] privateKey) => _key = privateKey;
    }
}

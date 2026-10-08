// SPDX-License-Identifier: MIT

using AetherNet.Security.Privacy;

namespace AetherNetNodeService.Host.Lab;

/// <summary>
/// The duress PIN that turns the keys to noise: the Panic page's demo, run here with the real PanicWipe core while
/// the page shows it.
/// </summary>
/// <remarks>
/// A SIMULATION. The key material is random demo bytes; nothing on the device is touched, armed, or erased. The code is
/// the page's own, moved here as it was: the hash, the verify, the secure-erase, and the manifest of every key-store
/// entry a wipe must destroy.
/// </remarks>
public sealed class PanicLabDemo
{
    private byte[]? _duressHash;
    private readonly List<Entry> _store = new();

    public PanicLabDemo() => Reset();

    /// <summary>Raised when what the page shows has changed.</summary>
    public event Action? Changed;

    public string DuressPin { get; set; } = "";
    public string HashHex { get; private set; } = "";
    public bool Armed { get; private set; }
    public string UnlockPin { get; set; } = "";
    public bool Wiped { get; private set; }
    public string? Outcome { get; private set; }
    public int ManifestCount { get; private set; }
    public string ErasedName { get; private set; } = "";
    public string EraseBefore { get; private set; } = "";
    public string EraseAfter { get; private set; } = "";
    public int MaxPreKeys => PanicWipe.MaxPreKeys;
    public int IdentityKeyCount => PanicWipe.IdentityKeyNames.Count;
    public string ExampleNames => $"{PanicWipe.PreKeyName(0)}, {PanicWipe.SignedPreKeyName(0)}, …";

    /// <summary>This node's key material, as the page lists it: each entry's name, whether it is alive, and a peek.</summary>
    public IReadOnlyList<EntryView> Store => _store.Select(e => new EntryView(e.Name, e.Alive, Convert.ToHexString(e.Material.AsSpan(0, 4)))).ToList();

    /// <summary>One key-store entry, as the page shows it.</summary>
    public sealed record EntryView(string Name, bool Alive, string Preview);

    private sealed class Entry
    {
        public required string Name { get; init; }
        public required byte[] Material { get; init; }
        public bool Alive { get; set; } = true;
    }

    public void Reset()
    {
        DuressPin = ""; _duressHash = null; HashHex = ""; Armed = false;
        UnlockPin = ""; Wiped = false; Outcome = null;
        _store.Clear();
        // The real identity manifest, each entry filled with random demo bytes standing in for a key.
        foreach (var name in PanicWipe.IdentityKeyNames)
            _store.Add(new Entry { Name = name, Material = Rand(32) });
        Changed?.Invoke();
    }

    public void Arm()
    {
        if (string.IsNullOrWhiteSpace(DuressPin)) return;
        // Kept only as its SHA-256 — the PIN never lands on disk.
        _duressHash = PanicWipe.DuressPinHash(DuressPin);
        HashHex = Convert.ToHexString(_duressHash);
        Armed = true;
        Outcome = null;
        Changed?.Invoke();
    }

    public void Unlock()
    {
        if (_duressHash is null) return;
        // Constant-time compare of the entered PIN against the stored duress hash.
        if (PanicWipe.VerifyDuressPin(UnlockPin, _duressHash))
            Wipe();
        else
            Outcome = "Unlocked normally. This was not the duress PIN, so the keys are intact.";
        Changed?.Invoke();
    }

    private void Wipe()
    {
        // Prove the erase on the first entry: snapshot it, erase it, snapshot again.
        var first = _store[0];
        ErasedName = first.Name;
        EraseBefore = Convert.ToHexString(first.Material.AsSpan(0, 8));

        foreach (var e in _store)
        {
            // Real secure-erase: overwrite with random, then zero. Best-effort, defence in depth.
            PanicWipe.SecureErase(e.Material);
            e.Alive = false;
        }

        EraseAfter = Convert.ToHexString(first.Material.AsSpan(0, 8));
        ManifestCount = PanicWipe.IdentityKeyNames.Count + PanicWipe.MaxPreKeys * 2;
        Wiped = true;
        Outcome = "Duress PIN entered — the identity has been wiped.";
    }

    private static byte[] Rand(int n)
    {
        var b = new byte[n];
        Random.Shared.NextBytes(b);
        return b;
    }
}

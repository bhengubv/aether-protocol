// SPDX-License-Identifier: MIT

using AetherNet.Identity;
using AetherNet.Security.Backup;
using AetherNet.Security.Models;
using AetherNet.Security.Services;

namespace AetherNetNodeService.Host.Lab;

/// <summary>
/// The whole backup is 24 words: the Recovery page's demo, run here with the real IdentityBackup while the page shows
/// it. The private key stays here; the page only ever sees the words and the tags.
/// </summary>
public sealed class RecoveryLabDemo
{
    private byte[] _priv = Array.Empty<byte>();
    private string _phrase = "";
    private string _entered = "";

    public RecoveryLabDemo() => Generate();

    /// <summary>Raised when what the page shows has changed.</summary>
    public event Action? Changed;

    public string TagA { get; private set; } = "…";
    public IReadOnlyList<string> Words { get; private set; } = Array.Empty<string>();
    public bool Valid { get; private set; }
    public string? Result { get; private set; }
    public string? TagB { get; private set; }
    public bool Match { get; private set; }

    /// <summary>The phrase as typed. Checked against its checksum as it changes.</summary>
    public string Entered
    {
        get => _entered;
        set
        {
            _entered = value ?? "";
            Valid = Bip39Mnemonic.IsValid(_entered);
        }
    }

    public void Generate()
    {
        var (priv, pub) = Ed25519SigningService.GenerateKeyPair();
        _priv = priv;
        TagA = AetherNetTag.FromPublicKey(pub).Value;

        // The identity → its 24 words. This is the entire backup.
        _phrase = IdentityBackup.ToRecoveryPhrase(_priv);
        Words = _phrase.Split(' ');

        ResetPhrase();
        Result = null;
        TagB = null;
        Changed?.Invoke();
    }

    public void ResetPhrase()
    {
        Entered = _phrase;
        Result = null;
        TagB = null;
        Changed?.Invoke();
    }

    // A plausible transcription slip: one letter off in one word. The checksum will not forgive it.
    public void CorruptOne()
    {
        var w = _phrase.Split(' ');
        var last = w[^1];
        var ch = last[^1] == 'z' ? 'a' : (char)(last[^1] + 1);
        w[^1] = last[..^1] + ch;
        Entered = string.Join(' ', w);
        Changed?.Invoke();
    }

    public void Recover()
    {
        Valid = Bip39Mnemonic.IsValid(_entered);
        try
        {
            // The whole restore: 24 words back into a full key pair on a device that stored nothing.
            KeyPair kp = IdentityBackup.FromRecoveryPhrase(_entered);
            TagB = AetherNetTag.FromPublicKey(kp.PublicKey).Value;
            Match = string.Equals(TagA, TagB, StringComparison.Ordinal);
            Result = Match
                ? "Same tag. No server and no custodian were involved — the words alone rebuilt the identity."
                : "The phrase was valid but encodes a different identity — a different tag entirely.";
        }
        catch (FormatException ex)
        {
            TagB = null;
            Match = false;
            Result = $"Rejected: {ex.Message} A wrong word is caught, not silently turned into someone else.";
        }

        Changed?.Invoke();
    }
}

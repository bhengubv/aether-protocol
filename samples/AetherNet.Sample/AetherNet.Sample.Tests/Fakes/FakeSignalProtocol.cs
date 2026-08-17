// SPDX-License-Identifier: MIT

using AetherNet.PreKeys;
using AetherNet.Security.Models;
using AetherNet.Security.Services;

namespace AetherNet.Sample.Tests.Fakes;

/// <summary>
/// Stands in for the Signal ratchet. A session can be granted or withheld, which is the only
/// property the chat layer reasons about; the "encryption" is reversible so a test can still read
/// what was carried. It proves delivery, never secrecy.
/// </summary>
public sealed class FakeSignalProtocol : ISignalProtocolService
{
    private readonly HashSet<string> _sessions = new(StringComparer.Ordinal);

    /// <summary>Grant a session with this peer, as a completed handshake would.</summary>
    public void OpenSessionWith(string peerUhid) => _sessions.Add(peerUhid);

    public bool HasSession(string peerUhid) => _sessions.Contains(peerUhid);

    /// <summary>Peers whose session was thrown away, in order — a repair leaves its trace here.</summary>
    public List<string> Dropped { get; } = [];

    /// <summary>
    /// When true, decrypting throws the way a diverged ratchet does — the authentication tag on a
    /// payload the peer really did encrypt for us simply will not match.
    /// </summary>
    public bool RatchetBroken { get; set; }

    /// <summary>
    /// When true, <b>encrypting</b> throws — the failure a caller must treat as "do not send this",
    /// as distinct from <see cref="RatchetBroken"/>, which is the receiving side's problem.
    /// </summary>
    public bool EncryptFails { get; set; }

    /// <summary>Everything this fake was asked to seal, and for whom — proof a caller went through the ratchet.</summary>
    public List<(string Peer, byte[] Plaintext)> Encrypted { get; } = [];

    public bool DropSession(string peerUhid)
    {
        Dropped.Add(peerUhid);
        return _sessions.Remove(peerUhid);
    }

    public Task<EncryptedPayload> EncryptAsync(string peerUhid, byte[] plaintext, CancellationToken ct = default)
    {
        if (!HasSession(peerUhid))
            throw new InvalidOperationException($"No session with {peerUhid} — nothing may be sent.");
        if (EncryptFails)
            throw new System.Security.Cryptography.CryptographicException(
                $"Cannot encrypt for {peerUhid} — the session is unusable.");

        Encrypted.Add((peerUhid, plaintext));
        return Task.FromResult(new EncryptedPayload(
            Ciphertext: plaintext, Nonce: [], MessageType: 0, SenderUhid: "", Counter: 0));
    }

    public Task<byte[]> DecryptAsync(string peerUhid, EncryptedPayload payload, CancellationToken ct = default)
    {
        if (!HasSession(peerUhid))
            throw new InvalidOperationException($"No session with {peerUhid} — nothing may be read.");

        if (RatchetBroken)
            throw new System.Security.Cryptography.CryptographicException(
                "The computed authentication tag did not match the input authentication tag.");

        return Task.FromResult(payload.Ciphertext);
    }

    /// <summary>How many bundles this node has generated — a fresh one per handshake, not per run.</summary>
    public int BundlesGenerated { get; private set; }

    public Task<PreKeyBundle> GeneratePreKeyBundleAsync(string localUhid, CancellationToken ct = default)
    {
        // Each bundle carries a distinct one-time pre-key id, the way a real one does: the responder
        // consumes it establishing a session and will refuse a second message that names it again.
        BundlesGenerated++;
        return Task.FromResult(new PreKeyBundle(localUhid, [], [], 0, [], BundlesGenerated, [], []));
    }

    public Task ProcessPreKeyBundleAsync(PreKeyBundle bundle, CancellationToken ct = default)
    {
        _sessions.Add(bundle.Uhid);
        return Task.CompletedTask;
    }

    public Task<byte[]> SignDataAsync(byte[] data, CancellationToken ct = default) => Task.FromResult(data);

    public bool VerifySignature(byte[] publicKey, byte[] data, byte[] signature) => true;

    public byte[] DeriveEridRoutingKey() => new byte[32];
}

/// <summary>
/// Stands in for the pre-key exchange. Records what was asked for, and can hand a peer's bundle over
/// on demand — the moment a session becomes possible.
/// </summary>
public sealed class FakePreKeyExchange : IPreKeyExchangeService
{
    private readonly Dictionary<string, PreKeyBundle> _received = new(StringComparer.Ordinal);
    private PreKeyBundle? _local;

    /// <summary>Peers we were asked to fetch a bundle for, in order.</summary>
    public List<string> Requested { get; } = [];

    public event EventHandler<PreKeyBundleReceivedEventArgs>? BundleReceived;

    /// <summary>Make a peer's bundle available, as a reply arriving over the radio would.</summary>
    public void PublishBundleFor(string peerUhid) =>
        _received[peerUhid] = new PreKeyBundle(peerUhid, [], [], 0, [], 0, [], []);

    public void SetLocalBundle(PreKeyBundle bundle) => _local = bundle;
    public PreKeyBundle? GetLocalBundle() => _local;
    public PreKeyBundle? GetReceivedBundle(string uhid) => _received.GetValueOrDefault(uhid);

    public Task<Guid> RequestBundleAsync(string peerUhid, CancellationToken cancellationToken = default)
    {
        Requested.Add(peerUhid);
        return Task.FromResult(Guid.NewGuid());
    }

    public Task<bool> HandleAsync(AetherNet.Protocol.MeshPacket packet, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    /// <summary>Raise the arrival of a peer's bundle, as the radio path does.</summary>
    public void RaiseBundleReceived(string peerUhid)
    {
        PublishBundleFor(peerUhid);
        BundleReceived?.Invoke(this, new PreKeyBundleReceivedEventArgs
        {
            Bundle = _received[peerUhid],
            FromUhid = peerUhid,
            RequestId = Guid.Empty,
        });
    }
}

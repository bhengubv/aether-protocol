// SPDX-License-Identifier: MIT

using AetherNet.Identity;
using AetherNet.Security.Services;
using AetherNet.Security.Sync;

namespace AetherNetNodeService.Host.Lab;

/// <summary>
/// One person, several devices, one identity key: the Devices page's demo, run here with the real link, revocation and
/// sync codecs while the page shows it. The identity keys stay here; the page sees only what each step proved.
/// </summary>
public sealed class DevicesLabDemo
{
    private byte[] _idPriv = Array.Empty<byte>();
    private byte[] _idPub = Array.Empty<byte>();
    private byte[] _bPub = Array.Empty<byte>();
    private DeviceLink? _link;
    private RevocationSet _peerRevs = default!;   // a contact's view — no directory, just verified records
    private long _linkedAtMs, _revokedAtMs;

    public DevicesLabDemo() => Reset();

    /// <summary>Raised when what the page shows has changed.</summary>
    public event Action? Changed;

    /// <summary>Where phone-B stands.</summary>
    public enum DeviceState { Unlinked, Linked, Revoked }

    /// <summary>One device, as the page lists it.</summary>
    public sealed record DeviceView(string Name, DeviceState State);

    public string UserTag { get; private set; } = "…";
    public DeviceState BState { get; private set; } = DeviceState.Unlinked;
    public string? LinkInfo { get; private set; }
    public string? ForgeLinkInfo { get; private set; }
    public string? RevInfo { get; private set; }
    public string? ForgeRevInfo { get; private set; }
    public string? ReconInfo { get; private set; }

    public IReadOnlyList<DeviceView> Devices => new[]
    {
        new DeviceView("phone-A", DeviceState.Linked),   // the device you are on — the primary
        new DeviceView("phone-B", BState),
    };

    public void Reset()
    {
        var (idPriv, idPub) = Ed25519SigningService.GenerateKeyPair();
        _idPriv = idPriv; _idPub = idPub;
        UserTag = AetherNetTag.FromPublicKey(idPub).Value;

        var (_, bPub) = Ed25519SigningService.GenerateKeyPair();
        _bPub = bPub;

        // The contact only trusts what THIS identity key signed — that is all a RevocationSet admits.
        _peerRevs = new RevocationSet(_idPub);

        BState = DeviceState.Unlinked;
        _link = null;
        LinkInfo = ForgeLinkInfo = RevInfo = ForgeRevInfo = ReconInfo = null;
        Changed?.Invoke();
    }

    public void Link()
    {
        _linkedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // The user's identity key signs phone-B's public key.
        _link = DeviceLinkCodec.Create("phone-B", _bPub, _linkedAtMs, _idPriv);

        // A different device verifies that signature before admitting the newcomer.
        var admitted = DeviceLinkCodec.Verify(_link, _idPub);
        var bytes = DeviceLinkCodec.Serialize(_link).Length;
        BState = admitted ? DeviceState.Linked : DeviceState.Unlinked;
        LinkInfo = admitted
            ? $"Signed & serialized ({bytes} bytes, sig {Hex(_link.Signature)}…). Another device verifies it against your identity key: admitted to the self set."
            : "Verification failed.";
        Changed?.Invoke();
    }

    public void ForgeLink()
    {
        // An attacker signs phone-B's key with their OWN identity key, hoping to add a device.
        var (forgePriv, _) = Ed25519SigningService.GenerateKeyPair();
        var forged = DeviceLinkCodec.Create("phone-B", _bPub, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), forgePriv);
        var admitted = DeviceLinkCodec.Verify(forged, _idPub);
        ForgeLinkInfo = admitted
            ? "Forged link verified (BAD!)"
            : "A link signed by any other key is rejected — only your identity key can admit a device.";
        Changed?.Invoke();
    }

    public void Revoke()
    {
        if (_link is null) return;
        _revokedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // The same identity key signs a revocation of phone-B.
        var rev = DeviceRevocationCodec.Create("phone-B", _bPub, _revokedAtMs, "lost", _idPriv);

        // Gossip it to the contact, who verifies the signature itself and drops the device.
        var accepted = _peerRevs.Ingest(rev);
        var revoked = _peerRevs.IsRevoked(_bPub);
        BState = revoked ? DeviceState.Revoked : BState;
        RevInfo = accepted && revoked
            ? $"Contact ingested the revocation (verified locally) — phone-B is now revoked ({_peerRevs.Count} in its set). Any session keyed to it is refused."
            : "Revocation not accepted.";
        Changed?.Invoke();
    }

    public void ForgeRevoke()
    {
        // A forwarder tries to revoke phone-B with a key that is not the identity key.
        var (attackerPriv, _) = Ed25519SigningService.GenerateKeyPair();
        var forged = DeviceRevocationCodec.Create("phone-B", _bPub, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "spite", attackerPriv);
        var accepted = _peerRevs.Ingest(forged);
        ForgeRevInfo = accepted
            ? "Forged revocation accepted (BAD!)"
            : "A revocation signed by anyone but you is ignored — a forwarder can't revoke someone else's device.";
        Changed?.Invoke();
    }

    public void Reconcile()
    {
        // Membership as last-write-wins sync records for one item: a link, then a later revocation.
        var t1 = _linkedAtMs != 0 ? _linkedAtMs : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1000;
        var t2 = _revokedAtMs != 0 ? _revokedAtMs : t1 + 1000;
        var linkRec = new SyncRecord(Guid.NewGuid(), "phone-A", SyncOp.Upsert, "device:phone-B", 1, t1, Array.Empty<byte>());
        var revRec = new SyncRecord(Guid.NewGuid(), "phone-A", SyncOp.Delete, "device:phone-B", 2, t2, Array.Empty<byte>());

        var forward = SyncReconciler.Winner(new[] { linkRec, revRec }).Op;
        var backward = SyncReconciler.Winner(new[] { revRec, linkRec }).Op;
        ReconInfo = forward == backward
            ? $"Fed in either order, the winner is the same record — {forward} — so every device converges on \"phone-B revoked\" with no coordinator."
            : "Divergent — should never happen.";
        Changed?.Invoke();
    }

    private static string Hex(byte[] b) => Convert.ToHexString(b.AsSpan(0, 8));
}

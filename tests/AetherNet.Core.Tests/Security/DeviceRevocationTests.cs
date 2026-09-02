// SPDX-License-Identifier: MIT

using AetherNet.Security.Services;
using AetherNet.Security.Sync;
using Xunit;

namespace AetherNet.Core.Tests.Security;

public class DeviceRevocationTests
{
    private const long RevokedAt = 1_700_000_000_000L;

    [Fact]
    public void Verify_TrueForTheIdentityThatSignedIt_FalseForAnother()
    {
        var (idPriv, idPub) = Ed25519SigningService.GenerateKeyPair();
        var (_, devicePub) = Ed25519SigningService.GenerateKeyPair();
        var (_, strangerPub) = Ed25519SigningService.GenerateKeyPair();

        var rev = DeviceRevocationCodec.Create("phone-2", devicePub, RevokedAt, "lost", idPriv);

        Assert.True(DeviceRevocationCodec.Verify(rev, idPub));
        Assert.False(DeviceRevocationCodec.Verify(rev, strangerPub));
    }

    [Theory]
    [InlineData("device")]
    [InlineData("time")]
    [InlineData("reason")]
    public void Verify_FalseWhenTheSignedBodyIsTampered(string field)
    {
        var (idPriv, idPub) = Ed25519SigningService.GenerateKeyPair();
        var (_, devicePub) = Ed25519SigningService.GenerateKeyPair();
        var rev = DeviceRevocationCodec.Create("phone-2", devicePub, RevokedAt, "lost", idPriv);

        var tampered = field switch
        {
            "device" => rev with { DevicePublicKey = Ed25519SigningService.GenerateKeyPair().PublicKey },
            "time" => rev with { RevokedAtMs = RevokedAt + 1 },
            "reason" => rev with { Reason = "retired" },
            _ => rev,
        };

        Assert.False(DeviceRevocationCodec.Verify(tampered, idPub));
    }

    [Fact]
    public void Serialize_RoundTrips_AndStillVerifies()
    {
        var (idPriv, idPub) = Ed25519SigningService.GenerateKeyPair();
        var (_, devicePub) = Ed25519SigningService.GenerateKeyPair();
        var rev = DeviceRevocationCodec.Create("phone-2", devicePub, RevokedAt, "stolen", idPriv);

        var back = DeviceRevocationCodec.Deserialize(DeviceRevocationCodec.Serialize(rev));

        Assert.Equal(rev.DeviceId, back.DeviceId);
        Assert.Equal(rev.DevicePublicKey, back.DevicePublicKey);
        Assert.Equal(rev.RevokedAtMs, back.RevokedAtMs);
        Assert.Equal(rev.Reason, back.Reason);
        Assert.Equal(rev.Signature, back.Signature);
        Assert.True(DeviceRevocationCodec.Verify(back, idPub));
    }

    [Fact]
    public void ALinkCannotBeReplayedAsARevocation_DomainSeparation()
    {
        var (idPriv, _) = Ed25519SigningService.GenerateKeyPair();
        var (_, devicePub) = Ed25519SigningService.GenerateKeyPair();

        var link = DeviceLinkCodec.Create("phone-2", devicePub, RevokedAt, idPriv);

        // A serialized DeviceLink is not a DeviceRevocation — the domain tag guards against reinterpreting
        // an "admit" as an "eject".
        Assert.Throws<FormatException>(() => DeviceRevocationCodec.Deserialize(DeviceLinkCodec.Serialize(link)));
    }

    // ── RevocationSet ─────────────────────────────────────────────────────────

    [Fact]
    public void RevocationSet_AdmitsOnlyValidlySignedRevocations()
    {
        var (idPriv, idPub) = Ed25519SigningService.GenerateKeyPair();
        var (strangerPriv, _) = Ed25519SigningService.GenerateKeyPair();
        var (_, devicePub) = Ed25519SigningService.GenerateKeyPair();

        var set = new RevocationSet(idPub);

        var genuine = DeviceRevocationCodec.Create("phone-2", devicePub, RevokedAt, "lost", idPriv);
        Assert.True(set.Ingest(genuine));
        Assert.True(set.IsRevoked(devicePub));
        Assert.Equal(1, set.Count);

        // A forwarder cannot revoke someone else's device: a revocation signed by a stranger is ignored.
        var (_, otherDevice) = Ed25519SigningService.GenerateKeyPair();
        var forged = DeviceRevocationCodec.Create("phone-3", otherDevice, RevokedAt, "malice", strangerPriv);
        Assert.False(set.Ingest(forged));
        Assert.False(set.IsRevoked(otherDevice));
        Assert.Equal(1, set.Count);
    }

    [Fact]
    public void RevocationSet_IsRevoked_FalseForAnUnrevokedDevice()
    {
        var (_, idPub) = Ed25519SigningService.GenerateKeyPair();
        var (_, someDevice) = Ed25519SigningService.GenerateKeyPair();
        Assert.False(new RevocationSet(idPub).IsRevoked(someDevice));
    }
}

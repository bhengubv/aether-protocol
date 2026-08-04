// SPDX-License-Identifier: MIT
using AetherNet.Cartography;
using AetherNet.Cartography.Models;
using AetherNet.Security.Services;
using Xunit;

namespace AetherNet.Cartography.Tests;

public class PoLAttestationCodecTests
{
    [Fact]
    public void TimeBucket_QuantizesToFiveMinutes()
    {
        Assert.Equal(0, PoLAttestationCodec.TimeBucketFor(0));
        Assert.Equal(0, PoLAttestationCodec.TimeBucketFor(299_999));
        Assert.Equal(1, PoLAttestationCodec.TimeBucketFor(300_000));
        // Two timestamps 1 minute apart fall in the same bucket → witnesses agree.
        long t = 1_700_000_000_000;
        Assert.Equal(PoLAttestationCodec.TimeBucketFor(t), PoLAttestationCodec.TimeBucketFor(t + 60_000));
    }

    [Fact]
    public void SignableBody_IsDeterministic_AndBindsEveryField()
    {
        var b = PoLAttestationCodec.BuildSignableData("subj", "u4pruy", "place-1", 5, PoLTransport.Ble);
        Assert.Equal(b, PoLAttestationCodec.BuildSignableData("subj", "u4pruy", "place-1", 5, PoLTransport.Ble));

        Assert.NotEqual(b, PoLAttestationCodec.BuildSignableData("subj2", "u4pruy", "place-1", 5, PoLTransport.Ble));
        Assert.NotEqual(b, PoLAttestationCodec.BuildSignableData("subj", "gcpvj0", "place-1", 5, PoLTransport.Ble));
        Assert.NotEqual(b, PoLAttestationCodec.BuildSignableData("subj", "u4pruy", "place-2", 5, PoLTransport.Ble));
        Assert.NotEqual(b, PoLAttestationCodec.BuildSignableData("subj", "u4pruy", "place-1", 6, PoLTransport.Ble));
        Assert.NotEqual(b, PoLAttestationCodec.BuildSignableData("subj", "u4pruy", "place-1", 5, PoLTransport.Nfc));
    }

    [Fact]
    public void WitnessAndSubject_CoSignIdenticalBody_BothVerify()
    {
        var (witPriv, witPub) = Ed25519SigningService.GenerateKeyPair();
        var (subPriv, subPub) = Ed25519SigningService.GenerateKeyPair();

        var att = new PoLWitnessAttestation
        {
            SubjectUhid = "subj",
            WitnessUhid = "wit",
            Geohash = "u4pruy",
            PlaceId = "place-1",
            TimeBucket = PoLAttestationCodec.TimeBucketFor(1_700_000_000_000),
            Transport = PoLTransport.Ble,
        };
        var body = PoLAttestationCodec.BuildSignableData(att);
        att.WitnessSignature = Ed25519SigningService.Sign(witPriv, body);
        att.SubjectSignature = Ed25519SigningService.Sign(subPriv, body);

        Assert.True(Ed25519SigningService.Verify(witPub, body, att.WitnessSignature));
        Assert.True(Ed25519SigningService.Verify(subPub, body, att.SubjectSignature));
        // A witness signature must not verify under the subject's key (independent identities).
        Assert.False(Ed25519SigningService.Verify(subPub, body, att.WitnessSignature));
    }

    [Fact]
    public void MovingTheClaimedCell_BreaksTheSignature()
    {
        var (witPriv, witPub) = Ed25519SigningService.GenerateKeyPair();
        var att = new PoLWitnessAttestation
        {
            SubjectUhid = "subj",
            Geohash = "u4pruy",
            PlaceId = "p",
            TimeBucket = 5,
            Transport = PoLTransport.Ble,
        };
        att.WitnessSignature = Ed25519SigningService.Sign(witPriv, PoLAttestationCodec.BuildSignableData(att));

        // A GPS spoofer edits the claimed cell after the witness signed — signature no longer verifies.
        att.Geohash = "gcpvj0";
        Assert.False(Ed25519SigningService.Verify(witPub, PoLAttestationCodec.BuildSignableData(att), att.WitnessSignature));
    }
}

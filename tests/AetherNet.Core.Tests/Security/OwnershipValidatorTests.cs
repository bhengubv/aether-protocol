// SPDX-License-Identifier: MIT

using AetherNet.Identity;
using AetherNet.Security.Identity;
using AetherNet.Security.Services;
using Xunit;

namespace AetherNet.Core.Tests.Security;

public class OwnershipValidatorTests
{
    private const long Now = 1_700_000_000_000L;

    // An INodeIdentity backed by a real Ed25519 keypair, so proofs actually verify.
    private sealed class SigningIdentity : INodeIdentity
    {
        private readonly byte[] _priv;
        private readonly byte[] _pub;
        public SigningIdentity() => (_priv, _pub) = Ed25519SigningService.GenerateKeyPair();
        public AetherNetTag Tag => AetherNetTag.FromPublicKey(_pub);
        public ValueTask<AetherNetTag> GetOrMintAsync(CancellationToken ct = default) => new(Tag);
        public ValueTask<byte[]> GetPublicKeyAsync(CancellationToken ct = default) => new(_pub);
        public ValueTask<byte[]> SignAsync(byte[] data, CancellationToken ct = default) => new(Ed25519SigningService.Sign(_priv, data));
        public ValueTask<byte[]> DeriveKeyAsync(string purpose, CancellationToken ct = default) => new(new byte[32]);
    }

    [Fact]
    public async Task Prove_ThenVerify_SucceedsForTheRealOwner()
    {
        var me = new SigningIdentity();
        var challenge = OwnershipChallenge.Issue("device-admit", Now);

        var proof = await OwnershipValidator.ProveAsync(me, challenge);

        Assert.True(OwnershipValidator.Verify(challenge, proof, me.Tag, Now));
    }

    [Fact]
    public async Task Verify_FailsWhenTheTagIsSomeoneElses()
    {
        var me = new SigningIdentity();
        var someoneElse = new SigningIdentity();
        var challenge = OwnershipChallenge.Issue("device-admit", Now);
        var proof = await OwnershipValidator.ProveAsync(me, challenge);

        // My proof does not prove I own someone else's tag.
        Assert.False(OwnershipValidator.Verify(challenge, proof, someoneElse.Tag, Now));
    }

    [Fact]
    public async Task Verify_FailsForAStaleChallenge()
    {
        var me = new SigningIdentity();
        var challenge = OwnershipChallenge.Issue("relay-auth", Now);
        var proof = await OwnershipValidator.ProveAsync(me, challenge);

        var wayLater = Now + OwnershipValidator.DefaultMaxAgeMs + 1;
        Assert.False(OwnershipValidator.Verify(challenge, proof, me.Tag, wayLater));
    }

    [Fact]
    public async Task Verify_FailsForAChallengeFromTheFuture()
    {
        var me = new SigningIdentity();
        var challenge = OwnershipChallenge.Issue("relay-auth", Now);
        var proof = await OwnershipValidator.ProveAsync(me, challenge);

        Assert.False(OwnershipValidator.Verify(challenge, proof, me.Tag, Now - 1));
    }

    [Fact]
    public async Task Verify_FailsWhenTheProofAnswersADifferentNonce()
    {
        var me = new SigningIdentity();
        var issued = OwnershipChallenge.Issue("device-admit", Now);
        var proof = await OwnershipValidator.ProveAsync(me, issued);

        // A verifier checking a DIFFERENT challenge must reject a proof for the old nonce (replay guard).
        var different = OwnershipChallenge.Issue("device-admit", Now);
        Assert.False(OwnershipValidator.Verify(different, proof, me.Tag, Now));
    }

    [Fact]
    public void IsFresh_BoundsBothWays()
    {
        var c = new OwnershipChallenge([1, 2, 3], Now, "x");
        Assert.True(c.IsFresh(Now, 1000));
        Assert.True(c.IsFresh(Now + 1000, 1000));
        Assert.False(c.IsFresh(Now + 1001, 1000));
        Assert.False(c.IsFresh(Now - 1, 1000)); // from the future
    }

    [Fact]
    public void ChallengeBody_IsDeterministic()
    {
        var c = new OwnershipChallenge([9, 8, 7, 6], Now, "device-admit");
        Assert.Equal(OwnershipValidator.ChallengeBody(c), OwnershipValidator.ChallengeBody(c));
    }
}

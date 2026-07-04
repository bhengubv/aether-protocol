// SPDX-License-Identifier: MIT

using AetherNet.Constants;
using AetherNet.Core.Tests.Fakes;
using AetherNet.Protocol;
using AetherNet.Routing;
using AetherNet.Security.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Security acceptance tests for fail-closed RREP verification (Gap 3).
///
/// Proves the three properties of the hardened routing layer:
///   (a) a RoutingService with NO verifier supplied REJECTS an RREP — no forward route installed;
///   (b) an <see cref="Ed25519RouteReplyVerifier"/> whose resolver returns the correct public key
///       ACCEPTS a validly-signed RREP — forward route installed;
///   (c) a forged RREP (signed by a DIFFERENT key) AND an unsigned RREP are BOTH rejected.
///
/// Signed RREPs are built with a real Ed25519 keypair via the production signing path
/// (<see cref="PacketSigningService.SignPacketAsync"/>), so this exercises the actual signature
/// verification, not a stub. Assertions are on the observable side effect: presence/absence of the
/// forward route in the store.
/// </summary>
public class RouteReplyVerificationTests
{
    private const string Local = "local-uhid";
    private const string Source = "carol";

    private static MeshPacket NewRrep(string source = Source, string destination = Local,
        int ttl = ProtocolConstants.DefaultTtl)
        => new()
        {
            Id = Guid.NewGuid(),
            Type = PacketType.RouteReply,
            SourceUhid = source,
            DestinationUhid = destination,
            Ttl = ttl,
        };

    /// <summary>Fresh Signal service — auto-generates a distinct Ed25519 identity keypair.</summary>
    private static SignalProtocolService NewSignal()
        => new(NullLogger<SignalProtocolService>.Instance);

    private static PacketSigningService NewSigner(SignalProtocolService signal)
        => new(signal, NullLogger<PacketSigningService>.Instance);

    /// <summary>Signs an RREP with the given identity, filling Signature/Nonce/Timestamp.</summary>
    private static async Task<MeshPacket> SignRrepAsync(MeshPacket rrep, SignalProtocolService signer)
        => await NewSigner(signer).SignPacketAsync(rrep);

    // ─── (a) No verifier ⇒ fail-closed reject ────────────────────────────────

    [Fact]
    public async Task NoVerifier_RejectsRrep_NoRouteInstalled()
    {
        var sender = new FakeMeshSender(Local);
        var store = new InMemoryRouteStore();
        // No verifier argument at all — the fail-closed default (RejectAll) must apply.
        var svc = new RoutingService(sender, store);

        await svc.HandleRouteReplyAsync(NewRrep());

        Assert.Null(await store.GetAsync(Source)); // route rejected — not installed
        Assert.Null(svc.GetCachedRoute(Source));
    }

    // ─── (b) Ed25519 verifier + correct key + valid signature ⇒ accept ───────

    [Fact]
    public async Task Ed25519Verifier_ValidlySignedRrep_InstallsForwardRoute()
    {
        var sender = new FakeMeshSender(Local);
        var store = new InMemoryRouteStore();

        // The source node's real identity. Its public key is registered with the resolver.
        var sourceIdentity = NewSignal();
        var resolver = new StubKeyResolver(Source, sourceIdentity.GetPublicKey());

        // The verifier uses a (possibly different) node's Signal service purely for the
        // VerifySignature primitive — Ed25519 verify only needs the public key + data + sig.
        var verifier = new Ed25519RouteReplyVerifier(resolver, NewSignal());
        var svc = new RoutingService(sender, store, verifier);

        var signedRrep = await SignRrepAsync(NewRrep(), sourceIdentity);
        await svc.HandleRouteReplyAsync(signedRrep);

        var route = await store.GetAsync(Source);
        Assert.NotNull(route);
        Assert.Equal(Source, route!.NextHopUhid);
    }

    // ─── (c) Forged (wrong-key) signature ⇒ reject ───────────────────────────

    [Fact]
    public async Task Ed25519Verifier_ForgedRrep_SignedByDifferentKey_IsRejected()
    {
        var sender = new FakeMeshSender(Local);
        var store = new InMemoryRouteStore();

        // Resolver knows the LEGITIMATE source key...
        var legitimateSource = NewSignal();
        var resolver = new StubKeyResolver(Source, legitimateSource.GetPublicKey());
        var verifier = new Ed25519RouteReplyVerifier(resolver, NewSignal());
        var svc = new RoutingService(sender, store, verifier);

        // ...but the attacker signs the RREP (claiming to be "carol") with a DIFFERENT key.
        var attacker = NewSignal();
        var forgedRrep = await SignRrepAsync(NewRrep(), attacker);

        await svc.HandleRouteReplyAsync(forgedRrep);

        Assert.Null(await store.GetAsync(Source)); // forged signature rejected — no route
    }

    // ─── (c) Unsigned RREP ⇒ reject ──────────────────────────────────────────

    [Fact]
    public async Task Ed25519Verifier_UnsignedRrep_IsRejected()
    {
        var sender = new FakeMeshSender(Local);
        var store = new InMemoryRouteStore();

        var sourceIdentity = NewSignal();
        var resolver = new StubKeyResolver(Source, sourceIdentity.GetPublicKey());
        var verifier = new Ed25519RouteReplyVerifier(resolver, NewSignal());
        var svc = new RoutingService(sender, store, verifier);

        // RREP with an empty Signature (the MeshPacket default) — must be rejected.
        await svc.HandleRouteReplyAsync(NewRrep());

        Assert.Null(await store.GetAsync(Source));
    }

    // ─── (c') Unknown signer (resolver returns null) ⇒ reject ────────────────

    [Fact]
    public async Task Ed25519Verifier_UnknownSource_IsRejected()
    {
        var sender = new FakeMeshSender(Local);
        var store = new InMemoryRouteStore();

        // Resolver knows nobody — even a validly self-signed RREP is rejected (unknown signer).
        var resolver = new StubKeyResolver(); // empty
        var verifier = new Ed25519RouteReplyVerifier(resolver, NewSignal());
        var svc = new RoutingService(sender, store, verifier);

        var sourceIdentity = NewSignal();
        var signedRrep = await SignRrepAsync(NewRrep(), sourceIdentity);

        await svc.HandleRouteReplyAsync(signedRrep);

        Assert.Null(await store.GetAsync(Source));
    }

    /// <summary>Minimal in-test UHID→public-key map for the routing verifier.</summary>
    private sealed class StubKeyResolver : IRouteReplyKeyResolver
    {
        private readonly Dictionary<string, byte[]> _keys = new(StringComparer.Ordinal);

        public StubKeyResolver() { }

        public StubKeyResolver(string uhid, byte[] publicKey) => _keys[uhid] = publicKey;

        public byte[]? ResolvePublicKey(string sourceUhid)
            => _keys.TryGetValue(sourceUhid, out var key) ? key : null;
    }
}

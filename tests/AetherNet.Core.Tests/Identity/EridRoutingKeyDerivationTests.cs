// SPDX-License-Identifier: MIT

using System.Threading.Tasks;
using AetherNet.Identity;
using AetherNet.Security.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Verifies the identity→routingKey seam: <see cref="SignalProtocolService.DeriveEridRoutingKey"/>
/// turns a node's identity secret into its secret ERID routing key, without the secret leaving the
/// service — the wiring that lets a host build an <see cref="EridDirectory"/> for the node.
/// </summary>
public class EridRoutingKeyDerivationTests
{
    private static async Task<SignalProtocolService> NewInitializedNodeAsync(string uhid)
    {
        var svc = new SignalProtocolService(NullLogger<SignalProtocolService>.Instance);
        await svc.GeneratePreKeyBundleAsync(uhid); // generates/loads the identity key
        return svc;
    }

    [Fact]
    public async Task DeriveEridRoutingKey_IsStablePerIdentity_AndDiffersAcrossNodes()
    {
        var alice = await NewInitializedNodeAsync("alice");
        var bob = await NewInitializedNodeAsync("bob");

        var aKey1 = alice.DeriveEridRoutingKey();
        var aKey2 = alice.DeriveEridRoutingKey();
        var bKey = bob.DeriveEridRoutingKey();

        Assert.Equal(32, aKey1.Length);   // HKDF-SHA256 → 32-byte routing key
        Assert.Equal(aKey1, aKey2);       // stable for a given identity (rotation survives restarts)
        Assert.NotEqual(aKey1, bKey);     // distinct identities → distinct, uncorrelated schedules
    }

    [Fact]
    public async Task DerivedKey_DrivesAWorkingEridDirectory()
    {
        const long t = 1_700_000_000;
        var alice = await NewInitializedNodeAsync("alice");

        // The seam's output is exactly what EridDirectory consumes — end-to-end, a host can stand up
        // the node's rotating-address directory straight from its Signal identity.
        var dir = new EridDirectory(alice.DeriveEridRoutingKey());
        var erid = dir.MyErid(t);

        Assert.Equal(EphemeralRoutingId.DefaultLength, erid.Length);
        Assert.Equal(dir.MyErid(t), dir.MyErid(t + 1)); // stable within the window
    }
}

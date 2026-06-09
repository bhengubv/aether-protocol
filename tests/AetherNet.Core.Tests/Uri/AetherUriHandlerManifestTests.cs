// SPDX-License-Identifier: MIT

using System.Threading;
using System.Threading.Tasks;
using AetherNet.Uri;
using Xunit;

namespace AetherNet.Core.Tests.Uri;

public class AetherUriHandlerManifestTests
{
    private static AetherUriHandlerManifest BuildSampleManifest() => new(
        "aether.media",
        new[]
        {
            new AetherUriHandlerDescriptor("profile", description: "Get the profile."),
            new AetherUriHandlerDescriptor("profile", "avatar", description: "Get the avatar."),
            new AetherUriHandlerDescriptor("content", "{hash}", description: "Fetch content."),
            new AetherUriHandlerDescriptor("watch", "{sessionId}/join", description: "Join watch party."),
        });

    [Fact]
    public void Manifest_ExactMatch_Resolves()
    {
        var m = BuildSampleManifest();
        var u = AetherUri.Parse("aether://KXJB7-MN2P4/profile");
        var r = m.Resolve(u);
        Assert.NotNull(r);
        Assert.Equal("profile", r!.Value.Handler.HandlerName);
        Assert.Equal(string.Empty, r.Value.Handler.PathTemplate);
        Assert.Empty(r.Value.Captures);
    }

    [Fact]
    public void Manifest_NestedExactMatch_Resolves()
    {
        var m = BuildSampleManifest();
        var u = AetherUri.Parse("aether://KXJB7-MN2P4/profile/avatar");
        var r = m.Resolve(u);
        Assert.NotNull(r);
        Assert.Equal("avatar", r!.Value.Handler.PathTemplate);
    }

    [Fact]
    public void Manifest_RouteCapture_PopulatesParameter()
    {
        var m = BuildSampleManifest();
        var u = AetherUri.Parse("aether://KXJB7-MN2P4/content/sha256-abc");
        var r = m.Resolve(u);
        Assert.NotNull(r);
        Assert.Equal("sha256-abc", r!.Value.Captures["hash"]);
    }

    [Fact]
    public void Manifest_MultiSegmentCapture_PopulatesParameter()
    {
        var m = BuildSampleManifest();
        var u = AetherUri.Parse("aether://KXJB7-MN2P4/watch/sess-99/join");
        var r = m.Resolve(u);
        Assert.NotNull(r);
        Assert.Equal("sess-99", r!.Value.Captures["sessionId"]);
    }

    [Fact]
    public void Manifest_UnknownHandler_ReturnsNull()
    {
        var m = BuildSampleManifest();
        var u = AetherUri.Parse("aether://KXJB7-MN2P4/unknown");
        Assert.Null(m.Resolve(u));
    }

    [Fact]
    public void Manifest_WrongPathLength_ReturnsNull()
    {
        var m = BuildSampleManifest();
        // /watch needs {sessionId}/join — too short.
        var u = AetherUri.Parse("aether://KXJB7-MN2P4/watch/sess-99");
        Assert.Null(m.Resolve(u));
    }

    [Fact]
    public void Manifest_AppId_RequiredOnConstruction()
    {
        Assert.Throws<AetherUriException>(() =>
            new AetherUriHandlerManifest(string.Empty, Array.Empty<AetherUriHandlerDescriptor>()));
    }

    [Fact]
    public void HandlerDescriptor_HandlerName_RequiredOnConstruction()
    {
        Assert.Throws<AetherUriException>(() =>
            new AetherUriHandlerDescriptor(string.Empty));
    }

    // ── Router ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Router_Dispatch_InvokesRegisteredCallback()
    {
        var m = BuildSampleManifest();
        var router = new AetherUriRouter(m);
        var profileHandler = m.Handlers[0]; // /profile
        var invoked = false;
        router.RegisterHandler(profileHandler, (_, _) => { invoked = true; return Task.CompletedTask; });

        var ok = await router.DispatchAsync("aether://KXJB7-MN2P4/profile");
        Assert.True(ok);
        Assert.True(invoked);
    }

    [Fact]
    public async Task Router_Dispatch_NoMatch_ReturnsFalse()
    {
        var m = BuildSampleManifest();
        var router = new AetherUriRouter(m);
        var ok = await router.DispatchAsync("aether://KXJB7-MN2P4/nope");
        Assert.False(ok);
    }

    [Fact]
    public async Task Router_Dispatch_ContextHasRouteParameters()
    {
        var m = BuildSampleManifest();
        var router = new AetherUriRouter(m);
        var contentHandler = m.Handlers[2]; // /content/{hash}
        AetherUriDispatchContext? seen = null;
        router.RegisterHandler(contentHandler, (ctx, _) => { seen = ctx; return Task.CompletedTask; });

        await router.DispatchAsync("aether://KXJB7-MN2P4/content/sha256-xyz");
        Assert.NotNull(seen);
        Assert.Equal("sha256-xyz", seen!.RouteParameters["hash"]);
    }

    [Fact]
    public void Router_RegisterHandler_NotInManifest_Throws()
    {
        var m = BuildSampleManifest();
        var router = new AetherUriRouter(m);
        var alien = new AetherUriHandlerDescriptor("stranger");
        Assert.Throws<AetherUriException>(() =>
            router.RegisterHandler(alien, (_, _) => Task.CompletedTask));
    }

    [Fact]
    public async Task Router_Dispatch_NoCallbackForRegisteredHandler_ReturnsFalse()
    {
        var m = BuildSampleManifest();
        var router = new AetherUriRouter(m);
        // /profile is in the manifest but no callback registered.
        var ok = await router.DispatchAsync("aether://KXJB7-MN2P4/profile");
        Assert.False(ok);
    }

    [Fact]
    public async Task Router_Dispatch_PropagatesHandlerException()
    {
        var m = BuildSampleManifest();
        var router = new AetherUriRouter(m);
        var h = m.Handlers[0];
        router.RegisterHandler(h, (_, _) => throw new InvalidOperationException("boom"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            router.DispatchAsync("aether://KXJB7-MN2P4/profile"));
    }
}

// SPDX-License-Identifier: MIT

using Aether.Shared;
using Aether.Shared.Cache;
using Aether.Shared.Data;
using Aether.Shared.Services;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aether.Tests;

/// <summary>
/// The app's root, before anything is drawn. Everything below asks AetherNetService what to show, so while it does not
/// answer the root says so — in a person's words, with the way to try again.
/// </summary>
public sealed class RoutesTests : IDisposable
{
    private readonly Bunit.TestContext _ctx = new();

    public RoutesTests() => _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

    public void Dispose() => _ctx.Dispose();

    /// <summary>
    /// Shown as it came, the exception put a release build's resource key on the screen — "Exception_WasThrown,
    /// Java.Lang.Exception" — when the P30 stopped AetherNetService for memory as Aether reached it (2026-10-24).
    /// </summary>
    [Fact]
    public void When_AetherNetService_does_not_answer_the_person_is_told_in_plain_words()
    {
        var line = new SilentLine("Exception_WasThrown, Java.Lang.Exception");
        var menu = new ServiceMenu(line);
        _ctx.Services.AddSingleton<IServiceCall>(line);
        _ctx.Services.AddSingleton(menu);
        _ctx.Services.AddSingleton(new WarmUpService(menu));
        _ctx.Services.AddSingleton(new AetherStore(menu));

        var cut = _ctx.RenderComponent<Routes>();

        cut.WaitForAssertion(() => Assert.Equal(Routes.Unreached, cut.Find("p").TextContent));
        Assert.DoesNotContain("Exception_WasThrown", cut.Markup);
        Assert.Equal("Try again", cut.Find("button").TextContent);
    }

    /// <summary>A line to AetherNetService that never gets an answer.</summary>
    private sealed class SilentLine(string why) : IServiceCall
    {
        public bool IsConnected => false;

        public event Action? Connected { add { } remove { } }

        public event Action<int, byte[]>? Told { add { } remove { } }

        public Task ConnectAsync(CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException(why));

        public Task<byte[]> CallAsync(int code, byte[]? args = null, CancellationToken cancellationToken = default) =>
            Task.FromException<byte[]>(new InvalidOperationException(why));

        public byte[] Call(int code, byte[]? args = null) => throw new InvalidOperationException(why);
    }
}

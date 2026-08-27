// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AetherNet.Browser;

/// <summary>
/// Wiring the mesh browser into a host, in one line.
/// </summary>
/// <example>
/// The whole of it, for a host that already has a node identity and a content store:
/// <code>
/// builder.Services.AddAetherBrowser();
/// </code>
/// and then, anywhere in the UI:
/// <code>
/// &lt;AetherBrowser /&gt;
/// </code>
/// That gives a working browser immediately — the owner's pages, the deck, the editor — with
/// everything kept in memory and no radio. Both of those are seams, and a real host fills them:
/// <code>
/// builder.Services.AddSingleton&lt;ICardStore, MyDeviceCardStore&gt;();
/// builder.Services.AddSingleton&lt;IMeshLink, MyRadioLink&gt;();
/// </code>
/// Nothing else changes. That is the point of the seams being this narrow.
/// </example>
public static class AetherBrowserServiceCollectionExtensions
{
    /// <summary>
    /// Register the mesh browser.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every registration is <c>TryAdd</c>, so a host that has already said what it wants keeps it.
    /// Register your own <see cref="ICardStore"/> and <see cref="IMeshLink"/> before or after this
    /// call — either way, yours wins.
    /// </para>
    /// <para>
    /// What the host still owes: an <c>INodeIdentity</c> and an <c>IContentStore</c>. Those come from
    /// the protocol rather than from the browser, because they are the device's, not this component's
    /// — the same identity signs your messages, and the same content store holds everything else the
    /// device carries.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddAetherBrowser(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // A store that forgets, so the browser works out of the box and never has a null. Wrong for a
        // person, right for a first run and for a test — see ICardStore.
        services.TryAddSingleton<ICardStore, InMemoryCardStore>();

        services.TryAddSingleton<MyPages>();
        services.TryAddSingleton<Deck>();
        services.TryAddSingleton<MeshWebService>();

        return services;
    }
}

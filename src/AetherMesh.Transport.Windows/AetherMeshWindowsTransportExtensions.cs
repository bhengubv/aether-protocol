// SPDX-License-Identifier: MIT

using AetherMesh.DependencyInjection;
using AetherMesh.Transport.Abstractions;
using AetherMesh.Transport.NearLink;
using AetherMesh.Transport.Services;
using AetherMesh.Transport.Windows.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AetherMesh.Transport.Windows;

/// <summary>
/// Wires all Windows-native transport backends into the Aether protocol DI stack.
///
/// <para>
/// Usage:
/// </para>
/// <code>
/// services.AddAetherMeshProtocol(opts =>
///         {
///             opts.LocalUhid = "aether:alice:01";
///         })
///         .AddSignalProtocol()
///         .AddRouting()
///         .AddWindowsTransports();        // BLE GATT + Wi-Fi Direct + stubs
///
/// // Or with HTTP relay fallback:
///         .AddWindowsTransports(httpRelayBaseUrl: "https://relay.example.com");
/// </code>
///
/// <para>
/// Transports registered:
/// </para>
/// <list type="table">
///   <item>
///     <term><see cref="WinBleGattTransportService"/></term>
///     <description>
///       Aether Red (BLE) — Windows BLE GATT central. Always registered as
///       <see cref="IBleTransportService"/>. Requires Bluetooth adapter.
///     </description>
///   </item>
///   <item>
///     <term><see cref="WinWifiDirectTransportService"/></term>
///     <description>
///       Aether Green (Wi-Fi Direct) — Windows WiFiDirect API. Always registered as
///       <see cref="IWifiDirectService"/>. Requires Wi-Fi adapter.
///     </description>
///   </item>
///   <item>
///     <term><see cref="WinNearLinkStubTransportService"/></term>
///     <description>
///       Aether Teal (NearLink) — BLE-approximation stub. Registered as
///       <see cref="INearLinkTransportService"/>. <see cref="ITransportService.IsAvailable"/>
///       returns <c>false</c> until a Windows NearLink SDK is available; placeholder
///       only. Real NearLink nodes use the HarmonyOS <c>@kit.NearLinkKit</c> SDK.
///     </description>
///   </item>
///   <item>
///     <term><see cref="WinNfcStubTransportService"/></term>
///     <description>
///       Aether White (NFC) — stub. <see cref="ITransportService.IsAvailable"/> is
///       always <c>false</c> (<c>Windows.Networking.Proximity</c> was removed in
///       Windows 11). Added to the additional-transports list so future activation
///       requires only a driver swap.
///     </description>
///   </item>
///   <item>
///     <term><see cref="HttpRelayTransportService"/></term>
///     <description>
///       Aether Purple (HTTP relay) — cellular / internet fallback. Only registered
///       when <paramref name="httpRelayBaseUrl"/> is non-null.
///     </description>
///   </item>
///   <item>
///     <term><see cref="TransportManager"/></term>
///     <description>
///       Aggregates all registered transports. Registered as
///       <see cref="ITransportManager"/> if not already in the container.
///     </description>
///   </item>
/// </list>
/// </summary>
public static class AetherMeshWindowsTransportExtensions
{
    /// <summary>
    /// Register all Windows-native Aether transport services and the
    /// <see cref="TransportManager"/> that selects among them.
    /// </summary>
    /// <param name="builder">The Aether protocol builder.</param>
    /// <param name="httpRelayBaseUrl">
    /// Optional base URL for the HTTP relay fallback transport, e.g.
    /// <c>"https://relay.circleos.co.za"</c>. When <c>null</c> (default),
    /// <see cref="HttpRelayTransportService"/> is not registered.
    /// </param>
    /// <returns>The same builder, for chaining.</returns>
    public static IAetherMeshProtocolBuilder AddWindowsTransports(
        this IAetherMeshProtocolBuilder builder,
        string? httpRelayBaseUrl = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var services = builder.Services;

        // ── BLE GATT (Aether Red) ─────────────────────────────────────────────
        services.TryAddSingleton<IBleTransportService>(sp =>
        {
            var localUhid = sp.GetRequiredService<IOptions<AetherMeshOptions>>().Value.LocalUhid;
            var logger    = sp.GetService<ILogger<WinBleGattTransportService>>()
                            ?? NullLogger<WinBleGattTransportService>.Instance;
            return new WinBleGattTransportService(localUhid, logger);
        });

        // ── Wi-Fi Direct (Aether Green) ───────────────────────────────────────
        services.TryAddSingleton<IWifiDirectService>(sp =>
        {
            var localUhid = sp.GetRequiredService<IOptions<AetherMeshOptions>>().Value.LocalUhid;
            var logger    = sp.GetService<ILogger<WinWifiDirectTransportService>>();
            return new WinWifiDirectTransportService(localUhid, logger);
        });

        // ── NearLink stub (Aether Teal) ───────────────────────────────────────
        // IsAvailable == false until a Windows NearLink SDK ships.
        services.TryAddSingleton<INearLinkTransportService, WinNearLinkStubTransportService>();

        // ── NFC stub (Aether White) ───────────────────────────────────────────
        // IsAvailable == false (Windows.Networking.Proximity removed in Win 11).
        // Registered as ITransportService so it lands in TransportManager.additionalTransports.
        services.TryAddSingleton<WinNfcStubTransportService>();

        // ── HTTP relay (Aether Purple) ────────────────────────────────────────
        if (httpRelayBaseUrl is not null)
        {
            var capturedUrl = httpRelayBaseUrl; // capture for lambda
            services.TryAddSingleton<HttpRelayTransportService>(sp =>
            {
                var localUhid = sp.GetRequiredService<IOptions<AetherMeshOptions>>().Value.LocalUhid;
                var logger    = sp.GetService<ILogger<HttpRelayTransportService>>();
                return new HttpRelayTransportService(capturedUrl, localUhid, logger);
            });
        }

        // ── TransportManager ─────────────────────────────────────────────────
        services.TryAddSingleton<ITransportManager>(sp =>
        {
            var logger     = sp.GetService<ILogger<TransportManager>>()
                             ?? NullLogger<TransportManager>.Instance;
            var ble        = sp.GetService<IBleTransportService>();
            var wifiDirect = sp.GetService<IWifiDirectService>();
            var nearLink   = sp.GetService<INearLinkTransportService>();
            var circleLink = sp.GetService<ICircleLinkTransportService>();

            // Collect additional transports: NFC stub + optional HTTP relay.
            var additional = new List<ITransportService>();
            var nfc = sp.GetService<WinNfcStubTransportService>();
            if (nfc is not null) additional.Add(nfc);
            var relay = sp.GetService<HttpRelayTransportService>();
            if (relay is not null) additional.Add(relay);

            return new TransportManager(
                logger,
                ble:                ble,
                circleLink:         circleLink,
                wifiDirect:         wifiDirect,
                nearLink:           nearLink,
                additionalTransports: additional.Count > 0 ? additional : null);
        });

        return builder;
    }
}

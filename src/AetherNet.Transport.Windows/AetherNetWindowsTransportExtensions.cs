// SPDX-License-Identifier: MIT

using AetherNet.DependencyInjection;
using AetherNet.Transport.Abstractions;
using AetherNet.Transport.NearLink;
using AetherNet.Transport.Services;
using AetherNet.Transport.Windows.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AetherNet.Transport.Windows;

/// <summary>
/// Wires all Windows-native transport backends into the Aether protocol DI stack.
///
/// <para>
/// Usage:
/// </para>
/// <code>
/// services.AddAetherNetProtocol(opts =>
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
///     <term><see cref="WinNearLinkBleTransportService"/></term>
///     <description>
///       Aether Teal (NearLink) — real SSAP-over-BLE-GATT central. Registered as
///       <see cref="INearLinkTransportService"/>. Participates in the Aether Teal mesh over BLE
///       using the canonical Aether SLE UUIDs and reports NearLink's nominal selection profile.
///       Real NearLink hardware uses the HarmonyOS <c>@kit.NearLinkKit</c> SDK.
///     </description>
///   </item>
///   <item>
///     <term><see cref="WinNfcBleTransportService"/></term>
///     <description>
///       Aether White (NFC) — real BLE-GATT central with an RSSI −40 dBm proximity gate that
///       reproduces NFC's tap-to-connect model (<c>Windows.Networking.Proximity</c> was removed
///       in Windows 11). Added to the additional-transports list.
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
public static class AetherNetWindowsTransportExtensions
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
    public static IAetherNetProtocolBuilder AddWindowsTransports(
        this IAetherNetProtocolBuilder builder,
        string? httpRelayBaseUrl = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var services = builder.Services;

        // ── BLE GATT (Aether Red) ─────────────────────────────────────────────
        services.TryAddSingleton<IBleTransportService>(sp =>
        {
            var localUhid = sp.GetRequiredService<IOptions<AetherNetOptions>>().Value.LocalUhid;
            var logger    = sp.GetService<ILogger<WinBleGattTransportService>>()
                            ?? NullLogger<WinBleGattTransportService>.Instance;
            return new WinBleGattTransportService(localUhid, logger);
        });

        // ── Wi-Fi Direct (Aether Green) ───────────────────────────────────────
        services.TryAddSingleton<IWifiDirectService>(sp =>
        {
            var localUhid = sp.GetRequiredService<IOptions<AetherNetOptions>>().Value.LocalUhid;
            var logger    = sp.GetService<ILogger<WinWifiDirectTransportService>>();
            return new WinWifiDirectTransportService(localUhid, logger);
        });

        // ── NearLink (Aether Teal) — real SSAP-over-BLE-GATT central ──────────
        services.TryAddSingleton<INearLinkTransportService>(sp =>
        {
            var localUhid = sp.GetRequiredService<IOptions<AetherNetOptions>>().Value.LocalUhid;
            var logger    = sp.GetService<ILogger<WinNearLinkBleTransportService>>()
                            ?? NullLogger<WinNearLinkBleTransportService>.Instance;
            return new WinNearLinkBleTransportService(localUhid, logger);
        });

        // ── NFC (Aether White) — real BLE-GATT proximity central ──────────────
        // Registered as a concrete type so it lands in TransportManager.additionalTransports.
        services.TryAddSingleton<WinNfcBleTransportService>(sp =>
        {
            var localUhid = sp.GetRequiredService<IOptions<AetherNetOptions>>().Value.LocalUhid;
            var logger    = sp.GetService<ILogger<WinNfcBleTransportService>>()
                            ?? NullLogger<WinNfcBleTransportService>.Instance;
            return new WinNfcBleTransportService(localUhid, logger);
        });

        // ── HTTP relay (Aether Purple) ────────────────────────────────────────
        if (httpRelayBaseUrl is not null)
        {
            var capturedUrl = httpRelayBaseUrl; // capture for lambda
            services.TryAddSingleton<HttpRelayTransportService>(sp =>
            {
                var localUhid = sp.GetRequiredService<IOptions<AetherNetOptions>>().Value.LocalUhid;
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
            var nfc = sp.GetService<WinNfcBleTransportService>();
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

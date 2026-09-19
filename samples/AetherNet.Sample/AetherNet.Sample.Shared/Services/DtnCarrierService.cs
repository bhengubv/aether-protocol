// SPDX-License-Identifier: MIT

using AetherNet.Dtn;
using AetherNet.Messaging;
using AetherNet.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// Makes delay-tolerant delivery a real feature instead of a Lab demo: a message to someone who is not
/// reachable right now is no longer stuck on your phone forever. The reliable core already hands such a
/// message to the DTN layer as a sealed bundle (its store-and-forward fallback); this service gives that
/// layer a heartbeat and closes the loop on the receiving end.
///
/// <para>
/// It does three small things and owns no protocol of its own:
/// <list type="bullet">
///   <item>runs the delivery + expiry sweep on a gentle cadence, and again the moment a peer appears, so
///     a bundle is delivered to a recipient who has come back or handed to a carrier who can get nearer;</item>
///   <item>bridges an <b>inbound</b> bundle — one addressed to us that a carrier just delivered — straight
///     into the reliable core, which decrypts it with the same ratchet as a message off the radio and
///     surfaces it in chat as if it had arrived live;</item>
///   <item>leaves the sealing and the receipts to the core and the DTN service — it never opens a payload.</item>
/// </list>
/// </para>
/// </summary>
public sealed class DtnCarrierService : IDisposable
{
    /// <summary>How often to re-attempt delivery and sweep expired bundles. Gentle — reachability, not battery, is the cost that matters, and a bundle's life is measured in hours.</summary>
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(45);

    private readonly IDtnService _dtn;
    private readonly IMessagingService _messaging;
    private readonly IIdentityService _me;
    private readonly IRadioMesh? _radio;
    private readonly ILogger<DtnCarrierService> _log;

    private CancellationTokenSource? _loop;
    private bool _primed;

    public DtnCarrierService(
        IDtnService dtn,
        IMessagingService messaging,
        IIdentityService me,
        IRadioMesh? radio = null,
        ILogger<DtnCarrierService>? log = null)
    {
        _dtn = dtn ?? throw new ArgumentNullException(nameof(dtn));
        _messaging = messaging ?? throw new ArgumentNullException(nameof(messaging));
        _me = me ?? throw new ArgumentNullException(nameof(me));
        _radio = radio;
        _log = log ?? NullLogger<DtnCarrierService>.Instance;

        // A bundle that lands here for us is a message someone left while we were unreachable. Its bytes
        // are still sealed — the DTN layer never opens them — so we feed them to the very same reliable
        // core that decrypts a message off the radio, and it surfaces in chat exactly as a live one would.
        _dtn.BundleReceived += OnBundleReceived;

        // A peer appearing is the best moment to sweep: whatever we are carrying for them can be delivered
        // now, and whatever we are holding for someone else can be handed to them to carry onward.
        if (_radio is not null) _radio.PeerLinked += OnPeerLinked;
    }

    /// <summary>Start the carry loop. Called once, at warm-up.</summary>
    public void Prime()
    {
        if (_primed) return;
        _primed = true;
        _loop = new CancellationTokenSource();
        _ = RunLoopAsync(_loop.Token);
    }

    private async Task RunLoopAsync(CancellationToken token)
    {
        try
        {
            await ScanAsync().ConfigureAwait(false);   // one pass now — we may already be holding bundles from a previous run
            using var timer = new PeriodicTimer(ScanInterval);
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                // Expire first so a stale bundle's custody slot is freed before we try to fill it again.
                try { await _dtn.ExpireStaleAsync(token).ConfigureAwait(false); }
                catch (Exception ex) { _log.LogDebug(ex, "DTN expiry sweep failed"); }
                await ScanAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex) { _log.LogWarning(ex, "DTN carry loop stopped unexpectedly"); }
    }

    private void OnPeerLinked(string peer) => _ = ScanAsync();

    private async Task ScanAsync()
    {
        try { await _dtn.RunDeliveryScanAsync().ConfigureAwait(false); }
        catch (Exception ex) { _log.LogDebug(ex, "DTN delivery scan failed"); }
    }

    /// <summary>
    /// A carrier delivered a bundle addressed to us. Rebuild the Data packet the reliable core would have
    /// seen had it arrived over the radio, and let the core decrypt and surface it: addressed to us (the
    /// core drops anything not for the local node), from the original sender (its ratchet is keyed on
    /// them), carrying the still-sealed payload.
    /// </summary>
    private void OnBundleReceived(object? sender, DtnBundleReceivedEventArgs e)
    {
        try
        {
            var packet = new MeshPacket
            {
                Id = e.BundleId,
                Type = PacketType.Data,
                SourceUhid = e.SenderUhid,
                DestinationUhid = _me.AetherTag,
                Payload = e.EncryptedPayload,
            };
            _ = _messaging.HandleAsync(packet);
            _log.LogDebug("DTN bundle {Id} from {From} handed to the reliable core to decrypt", e.BundleId, e.SenderUhid);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not hand a delivered DTN bundle to the reliable core");
        }
    }

    public void Dispose()
    {
        _dtn.BundleReceived -= OnBundleReceived;
        if (_radio is not null) _radio.PeerLinked -= OnPeerLinked;
        _loop?.Cancel();
        _loop?.Dispose();
    }
}

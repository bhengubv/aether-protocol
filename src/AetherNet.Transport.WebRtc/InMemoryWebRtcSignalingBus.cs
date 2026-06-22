// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Threading.Channels;

namespace AetherNet.Transport.WebRtc;

/// <summary>
/// In-process <see cref="IWebRtcSignaling"/> bus that routes signals between endpoints by UHID.
///
/// <para>The reference signalling implementation: it needs no network and no server, so it backs
/// same-process scenarios (multi-node simulations, a single device holding several identities) and
/// the test suite. Production cross-device signalling rides a real transport via
/// <c>RelayWebRtcSignaling</c> instead.</para>
///
/// <para>Each endpoint delivers inbound signals on its own single-reader queue, so signals arrive
/// in send order and never re-enter the sender's call stack — matching the ordered, reliable
/// delivery a real signalling channel provides.</para>
/// </summary>
public sealed class InMemoryWebRtcSignalingBus : IAsyncDisposable, IDisposable
{
    private readonly ConcurrentDictionary<string, Endpoint> _endpoints = new();

    /// <summary>Returns the signalling endpoint for <paramref name="uhid"/>, creating it once.</summary>
    public IWebRtcSignaling CreateEndpoint(string uhid) =>
        _endpoints.GetOrAdd(uhid, _ => new Endpoint(this));

    private bool Route(WebRtcSignal signal) =>
        _endpoints.TryGetValue(signal.ToUhid, out var target) && target.Deliver(signal);

    public void Dispose()
    {
        foreach (var endpoint in _endpoints.Values)
            endpoint.Dispose();
        _endpoints.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var endpoint in _endpoints.Values)
            await endpoint.DisposeAsync().ConfigureAwait(false);
        _endpoints.Clear();
    }

    private sealed class Endpoint : IWebRtcSignaling, IAsyncDisposable, IDisposable
    {
        private readonly InMemoryWebRtcSignalingBus _bus;
        private readonly Channel<WebRtcSignal> _inbox =
            Channel.CreateUnbounded<WebRtcSignal>(new UnboundedChannelOptions { SingleReader = true });
        private readonly Task _pump;

        public Endpoint(InMemoryWebRtcSignalingBus bus)
        {
            _bus = bus;
            _pump = Task.Run(PumpAsync);
        }

        public event Action<WebRtcSignal>? SignalReceived;

        public Task<bool> SendAsync(string peerUhid, WebRtcSignal signal, CancellationToken cancellationToken = default) =>
            Task.FromResult(_bus.Route(signal));

        public bool Deliver(WebRtcSignal signal) => _inbox.Writer.TryWrite(signal);

        private async Task PumpAsync()
        {
            await foreach (var signal in _inbox.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try { SignalReceived?.Invoke(signal); }
                catch { /* a misbehaving handler must not stop the queue */ }
            }
        }

        public void Dispose() => _inbox.Writer.TryComplete();

        public async ValueTask DisposeAsync()
        {
            _inbox.Writer.TryComplete();
            try { await _pump.ConfigureAwait(false); }
            catch { /* pump teardown is best-effort */ }
        }
    }
}

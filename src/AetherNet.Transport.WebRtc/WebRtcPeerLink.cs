// SPDX-License-Identifier: MIT

using AetherNet.Transport.WebRtc;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;

namespace AetherNet.Transport.WebRtc;

/// <summary>
/// One WebRTC connection to a single peer: an <see cref="RTCPeerConnection"/> plus its
/// <see cref="RTCDataChannel"/>, driving the offer/answer/ICE handshake over an
/// <see cref="IWebRtcSignaling"/> channel and surfacing received bytes.
/// </summary>
internal sealed class WebRtcPeerLink : IAsyncDisposable, IDisposable
{
    private const string DataChannelLabel = "aether";

    private readonly string _localUhid;
    private readonly string _peerUhid;
    private readonly IWebRtcSignaling _signaling;
    private readonly Action<string, byte[]> _onData;
    private readonly ILogger? _logger;
    private readonly RTCPeerConnection _pc;
    private readonly TaskCompletionSource<bool> _open =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private RTCDataChannel? _channel;
    private volatile bool _closed;

    /// <summary>Raised once when this link transitions to a terminal (closed/failed) state.</summary>
    public event Action? Closed;

    public bool IsOpen => _channel?.readyState == RTCDataChannelState.open;
    public bool IsClosed => _closed;

    public WebRtcPeerLink(
        string localUhid,
        string peerUhid,
        List<RTCIceServer> iceServers,
        IWebRtcSignaling signaling,
        Action<string, byte[]> onData,
        ILogger? logger)
    {
        _localUhid = localUhid;
        _peerUhid = peerUhid;
        _signaling = signaling;
        _onData = onData;
        _logger = logger;

        _pc = new RTCPeerConnection(new RTCConfiguration { iceServers = iceServers });
        _pc.onicecandidate += OnLocalIceCandidate;
        _pc.ondatachannel += AttachChannel;           // responder receives the channel
        _pc.onconnectionstatechange += OnConnectionStateChange;
    }

    /// <summary>Begins the handshake. The initiator creates the data channel + sends the offer.</summary>
    public async Task StartAsync(bool asInitiator)
    {
        if (!asInitiator) return; // responder waits for the inbound offer (see AcceptOfferAsync)

        var dc = await _pc.createDataChannel(DataChannelLabel).ConfigureAwait(false);
        AttachChannel(dc);

        var offer = _pc.createOffer();
        await _pc.setLocalDescription(offer).ConfigureAwait(false);
        await _signaling.SendAsync(_peerUhid, new WebRtcSignal
        {
            FromUhid = _localUhid,
            ToUhid = _peerUhid,
            Type = WebRtcSignalType.Offer,
            Sdp = offer.sdp,
        }).ConfigureAwait(false);
    }

    public async Task AcceptOfferAsync(string sdp)
    {
        var result = _pc.setRemoteDescription(new RTCSessionDescriptionInit
        {
            type = RTCSdpType.offer,
            sdp = sdp,
        });
        if (result != SetDescriptionResultEnum.OK)
        {
            _logger?.LogWarning("[WebRTC] rejected offer from {Peer}: {Result}", _peerUhid, result);
            return;
        }

        var answer = _pc.createAnswer();
        await _pc.setLocalDescription(answer).ConfigureAwait(false);
        await _signaling.SendAsync(_peerUhid, new WebRtcSignal
        {
            FromUhid = _localUhid,
            ToUhid = _peerUhid,
            Type = WebRtcSignalType.Answer,
            Sdp = answer.sdp,
        }).ConfigureAwait(false);
    }

    public void AcceptAnswer(string sdp)
    {
        var result = _pc.setRemoteDescription(new RTCSessionDescriptionInit
        {
            type = RTCSdpType.answer,
            sdp = sdp,
        });
        if (result != SetDescriptionResultEnum.OK)
            _logger?.LogWarning("[WebRTC] rejected answer from {Peer}: {Result}", _peerUhid, result);
    }

    public void AddRemoteCandidate(WebRtcSignal signal)
    {
        if (string.IsNullOrEmpty(signal.Candidate)) return;
        _pc.addIceCandidate(new RTCIceCandidateInit
        {
            candidate = signal.Candidate,
            sdpMid = signal.SdpMid,
            sdpMLineIndex = signal.SdpMLineIndex,
        });
    }

    private void OnLocalIceCandidate(RTCIceCandidate candidate)
    {
        if (candidate is null) return;
        _ = _signaling.SendAsync(_peerUhid, new WebRtcSignal
        {
            FromUhid = _localUhid,
            ToUhid = _peerUhid,
            Type = WebRtcSignalType.IceCandidate,
            Candidate = candidate.candidate,
            SdpMid = candidate.sdpMid,
            SdpMLineIndex = candidate.sdpMLineIndex,
        });
    }

    private void AttachChannel(RTCDataChannel dc)
    {
        _channel = dc;
        dc.onopen += () => _open.TrySetResult(true);
        dc.onclose += MarkClosed;
        dc.onerror += _ => MarkClosed();
        dc.onmessage += (_, _, data) => _onData(_peerUhid, data);
    }

    private void OnConnectionStateChange(RTCPeerConnectionState state)
    {
        if (state is RTCPeerConnectionState.failed
            or RTCPeerConnectionState.disconnected
            or RTCPeerConnectionState.closed)
            MarkClosed();
    }

    private void MarkClosed()
    {
        if (_closed) return;
        _closed = true;
        _open.TrySetResult(false);
        Closed?.Invoke();
    }

    public async Task<bool> WaitOpenAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (IsOpen) return true;
        if (_closed) return false;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return await _open.Task.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public async Task<bool> SendAsync(byte[] data, TimeSpan openTimeout, CancellationToken ct)
    {
        if (!await WaitOpenAsync(openTimeout, ct).ConfigureAwait(false)) return false;
        try
        {
            _channel!.send(data);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[WebRTC] send to {Peer} failed", _peerUhid);
            return false;
        }
    }

    public void Dispose()
    {
        if (!_closed)
        {
            _closed = true;
            _open.TrySetResult(false);
        }
        try { _channel?.close(); } catch { /* best effort */ }
        try { _pc.close(); } catch { /* best effort */ }
    }

    // Teardown is synchronous (SIPSorcery close() calls); the async shape is offered for
    // hosts that dispose their container asynchronously.
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

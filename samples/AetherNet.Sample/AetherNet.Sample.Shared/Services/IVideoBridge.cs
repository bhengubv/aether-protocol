// SPDX-License-Identifier: MIT

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// A way for video frames to reach the page that does not go through the JavaScript bridge.
///
/// <para>
/// Blazor's interop is one message channel, shared by every call in both directions and by the
/// renderer's own dispatcher. That is entirely fine for a button press and entirely wrong for a video
/// call: every frame crosses it twice, out as an encoded chunk and back in as somebody else's to draw.
/// Measured on a Redmi Note 9, it saturated at about four frames a second each way, and past that the
/// ANSWERS stopped coming back — capture correctly refused to add to the pile, and video stopped.
/// </para>
///
/// <para>
/// So frames get their own channel: a WebSocket to a server inside this same app, on loopback. It
/// never touches a network interface, it carries binary without base64, it has no dispatcher in front
/// of it, and it is full duplex — which is exactly the shape of a video call.
/// </para>
///
/// <para>
/// A head that has no such server returns null from <see cref="Endpoint"/> and video falls back to
/// interop. The web head does that today: its page and its server are already separate processes,
/// and a loopback socket on the server would reach the wrong machine entirely.
/// </para>
/// </summary>
public interface IVideoBridge
{
    /// <summary>
    /// Where the page should connect, or null when this head has no bridge and interop must be used.
    /// </summary>
    VideoBridgeEndpoint? Endpoint { get; }

    /// <summary>Start listening. Idempotent; returns the endpoint, or null if it could not.</summary>
    Task<VideoBridgeEndpoint?> StartAsync(CancellationToken cancellationToken = default);

    /// <summary>One encoded frame from this device's camera, as the page produced it.</summary>
    event Action<byte[]>? FrameFromPage;

    /// <summary>Whether the page is currently connected.</summary>
    bool PageConnected { get; }

    /// <summary>Give the page a frame to draw, tagged with whose it is.</summary>
    void SendToPage(string who, byte[] frame);
}

/// <summary>
/// Where the bridge is listening, and the secret that proves the page is the one allowed to use it.
/// </summary>
/// <param name="Port">
///   Chosen by the operating system rather than picked. A hardcoded port is a port that is already in
///   use on somebody's phone.
/// </param>
/// <param name="Token">
///   A random secret the page must present. Loopback is not private on Android — any app on the
///   handset can reach 127.0.0.1 — so without this, anything installed here could read one side of a
///   video call and inject frames into the other.
/// </param>
public sealed record VideoBridgeEndpoint(int Port, string Token);

/// <summary>
/// For heads with no loopback server. Video falls back to the JavaScript bridge.
/// </summary>
public sealed class NoVideoBridge : IVideoBridge
{
    public VideoBridgeEndpoint? Endpoint => null;

    public Task<VideoBridgeEndpoint?> StartAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<VideoBridgeEndpoint?>(null);

    public event Action<byte[]>? FrameFromPage { add { } remove { } }

    public bool PageConnected => false;

    public void SendToPage(string who, byte[] frame) { }
}

// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// A WebSocket server inside the app, on loopback, carrying video frames to and from the page.
///
/// <para>
/// Hand-rolled rather than taken from a package, and the reason is proportion: this speaks to exactly
/// one client, on 127.0.0.1, with binary frames and no extensions or subprotocols. The handshake is
/// twenty lines and the framing is thirty. Pulling in an HTTP server to get them would be a much
/// larger dependency than the problem.
/// </para>
///
/// <para>
/// Why it exists at all is in <see cref="IVideoBridge"/>: the JavaScript bridge is one shared message
/// channel and it saturates at about four frames a second each way on a mid-range handset.
/// </para>
/// </summary>
public sealed class LoopbackVideoBridge : IVideoBridge, IDisposable
{
    private const string HandshakeGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    /// <summary>
    /// The largest frame either side will accept.
    /// </summary>
    /// <remarks>
    /// A keyframe at this picture size runs to twenty or thirty kilobytes; anything approaching a
    /// megabyte is not a video frame and is refused rather than allocated.
    /// </remarks>
    private const int MaxFrame = 1 << 20;

    private readonly object _gate = new();
    private TcpListener? _listener;
    private NetworkStream? _page;
    private CancellationTokenSource? _life;
    private bool _disposed;

    /// <inheritdoc />
    public VideoBridgeEndpoint? Endpoint { get; private set; }

    /// <inheritdoc />
    public event Action<byte[]>? FrameFromPage;

    /// <inheritdoc />
    public bool PageConnected => _page is not null;

    /// <inheritdoc />
    public async Task<VideoBridgeEndpoint?> StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_disposed) return null;
            if (Endpoint is { } already) return already;

            try
            {
                // Loopback only. Binding to Any would put a video call on the local network.
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();

                // Port 0 means the operating system picks a free one. A hardcoded port is a port that
                // is already in use on somebody's phone.
                var port = ((IPEndPoint)_listener.LocalEndpoint).Port;

                // Loopback is not private on Android: any app on the handset can reach 127.0.0.1.
                // Without a secret, anything installed here could read one side of a video call and
                // inject frames into the other.
                var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

                _life = new CancellationTokenSource();
                Endpoint = new VideoBridgeEndpoint(port, token);
            }
            catch (Exception)
            {
                _listener = null;
                return null;
            }
        }

        _ = Task.Run(() => AcceptAsync(_life!.Token), CancellationToken.None);
        await Task.Yield();
        return Endpoint;
    }

    private async Task AcceptAsync(CancellationToken life)
    {
        var listener = _listener;
        if (listener is null) return;

        while (!life.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(life).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (Exception) { return; }

            // One page at a time. A second connection replaces the first rather than racing it — the
            // WebView is rebuilt on some lifecycle events and the old socket is simply stale.
            _ = Task.Run(() => ServeAsync(client, life), CancellationToken.None);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken life)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();

                if (!await ShakeHandsAsync(stream, life).ConfigureAwait(false)) return;

                lock (_gate) _page = stream;
                try { await ReadFramesAsync(stream, life).ConfigureAwait(false); }
                finally { lock (_gate) { if (ReferenceEquals(_page, stream)) _page = null; } }
            }
            catch (Exception) { /* the page went, or never arrived properly */ }
        }
    }

    /// <summary>
    /// The WebSocket opening handshake, and the token check.
    /// </summary>
    /// <remarks>
    /// The token travels in the path rather than a header because a browser cannot set headers on a
    /// WebSocket. It never leaves the handset — this connection does not reach a network interface.
    /// </remarks>
    private async Task<bool> ShakeHandsAsync(NetworkStream stream, CancellationToken life)
    {
        var request = new byte[4096];
        var got = 0;

        while (got < request.Length)
        {
            var n = await stream.ReadAsync(request.AsMemory(got), life).ConfigureAwait(false);
            if (n == 0) return false;
            got += n;

            var text = Encoding.ASCII.GetString(request, 0, got);
            if (!text.Contains("\r\n\r\n", StringComparison.Ordinal)) continue;

            var expected = Endpoint?.Token;
            if (string.IsNullOrEmpty(expected) || !text.Contains(expected, StringComparison.Ordinal))
                return false;

            var key = text.Split("\r\n")
                .FirstOrDefault(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                ?.Split(':', 2)[1].Trim();
            if (string.IsNullOrEmpty(key)) return false;

            var accept = Convert.ToBase64String(
                SHA1.HashData(Encoding.ASCII.GetBytes(key + HandshakeGuid)));

            var response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                $"Sec-WebSocket-Accept: {accept}\r\n\r\n");

            await stream.WriteAsync(response, life).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    /// <summary>Read frames the page sends: encoded video off this device's camera.</summary>
    private async Task ReadFramesAsync(NetworkStream stream, CancellationToken life)
    {
        var header = new byte[14];

        while (!life.IsCancellationRequested)
        {
            if (!await FillAsync(stream, header.AsMemory(0, 2), life).ConfigureAwait(false)) return;

            var opcode = header[0] & 0x0F;
            var masked = (header[1] & 0x80) != 0;
            long length = header[1] & 0x7F;

            if (length == 126)
            {
                if (!await FillAsync(stream, header.AsMemory(0, 2), life).ConfigureAwait(false)) return;
                length = BinaryPrimitives.ReadUInt16BigEndian(header);
            }
            else if (length == 127)
            {
                if (!await FillAsync(stream, header.AsMemory(0, 8), life).ConfigureAwait(false)) return;
                length = (long)BinaryPrimitives.ReadUInt64BigEndian(header);
            }

            if (length is < 0 or > MaxFrame) return;

            var mask = new byte[4];
            if (masked && !await FillAsync(stream, mask.AsMemory(0, 4), life).ConfigureAwait(false)) return;

            var payload = new byte[length];
            if (length > 0 && !await FillAsync(stream, payload, life).ConfigureAwait(false)) return;

            // Every frame a browser sends is masked. Unmasking is four XORs and is not optional.
            if (masked)
                for (var i = 0; i < payload.Length; i++) payload[i] ^= mask[i & 3];

            switch (opcode)
            {
                case 0x8: return;                                    // close
                case 0x9: await PongAsync(stream, payload, life).ConfigureAwait(false); break;
                case 0x2 when payload.Length > 0: FrameFromPage?.Invoke(payload); break;
                default: break;                                      // text and continuation are not used
            }
        }
    }

    private static async Task<bool> FillAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken life)
    {
        var got = 0;
        while (got < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[got..], life).ConfigureAwait(false);
            if (n == 0) return false;
            got += n;
        }
        return true;
    }

    private static Task PongAsync(NetworkStream stream, byte[] payload, CancellationToken life)
        => stream.WriteAsync(Frame(payload, 0xA), life).AsTask();

    /// <inheritdoc />
    /// <remarks>
    /// Tagged with whose picture it is: one byte of length, the tag, then the frame. A group call has
    /// several people on screen at once and the page has to know which canvas to draw on.
    /// </remarks>
    public void SendToPage(string who, byte[] frame)
    {
        if (_disposed || frame.Length == 0 || string.IsNullOrEmpty(who)) return;

        NetworkStream? page;
        lock (_gate) page = _page;
        if (page is null) return;

        var tag = Encoding.UTF8.GetBytes(who);
        if (tag.Length > 255) return;

        var body = new byte[1 + tag.Length + frame.Length];
        body[0] = (byte)tag.Length;
        tag.CopyTo(body, 1);
        frame.CopyTo(body, 1 + tag.Length);

        // Fire and forget, and never awaited: a frame is worthless a moment after it was captured, so
        // waiting for the socket would only make the next one later.
        _ = WriteAsync(page, Frame(body, 0x2));
    }

    private async Task WriteAsync(NetworkStream page, byte[] bytes)
    {
        try { await page.WriteAsync(bytes).ConfigureAwait(false); }
        catch (Exception)
        {
            lock (_gate) { if (ReferenceEquals(_page, page)) _page = null; }
        }
    }

    /// <summary>Wrap a payload as one unmasked WebSocket frame. A server never masks.</summary>
    private static byte[] Frame(byte[] payload, int opcode)
    {
        var n = payload.Length;
        byte[] framed;

        if (n < 126)
        {
            framed = new byte[2 + n];
            framed[1] = (byte)n;
            payload.CopyTo(framed, 2);
        }
        else if (n < 65536)
        {
            framed = new byte[4 + n];
            framed[1] = 126;
            BinaryPrimitives.WriteUInt16BigEndian(framed.AsSpan(2), (ushort)n);
            payload.CopyTo(framed, 4);
        }
        else
        {
            framed = new byte[10 + n];
            framed[1] = 127;
            BinaryPrimitives.WriteUInt64BigEndian(framed.AsSpan(2), (ulong)n);
            payload.CopyTo(framed, 10);
        }

        framed[0] = (byte)(0x80 | opcode);
        return framed;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        try { _life?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _page?.Dispose(); } catch { }

        _life?.Dispose();
        _page = null;
        _listener = null;
    }
}

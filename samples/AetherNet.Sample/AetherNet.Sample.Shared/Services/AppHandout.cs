// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// The giver's phone, serving the app to a phone that has nothing.
///
/// <para>
/// The taker has no Aether on their handset, so nothing clever can happen on their side. What they
/// have is a browser, and a browser knows how to fetch an address and hand what comes back to
/// Android's installer. So for a couple of minutes the giver's phone becomes the smallest possible
/// web server, serving exactly one file to exactly one secret, and then stops.
/// </para>
///
/// <para>
/// Hand-rolled for the same reason the video bridge is: this answers one path, with one method, to a
/// handful of callers. The handshake is a status line and three headers. Pulling in an HTTP stack to
/// get that would be a much larger dependency than the problem, on a phone, for a feature that runs
/// for two minutes at a time.
/// </para>
///
/// <para>
/// It lives in the platform-neutral half so it can be tested with a socket and no phone — this is the
/// piece where a mistake means somebody's mate stands there watching a browser spinner, which is the
/// exact moment the whole idea has to feel effortless.
/// </para>
/// </summary>
public sealed class AppHandout : IDisposable
{
    /// <summary>
    /// How long an invite stays good for.
    /// </summary>
    /// <remarks>
    /// Long enough to tap, join, fetch and think about it; short enough that a phone left in a pocket
    /// on a shared network is not quietly serving its own installer for the rest of the afternoon.
    /// </remarks>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How many phones one invite will serve.
    /// </summary>
    /// <remarks>
    /// Three, because the use case is three friends at a table and asking somebody to tap three times
    /// is asking them to do the same thing three times. It is not unlimited: a token that never spends
    /// is a token that leaks.
    /// </remarks>
    public const int MaxHandovers = 3;

    private readonly IAppShareService _app;
    private readonly TimeSpan _window;
    private readonly object _gate = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _life;
    private string _token = string.Empty;
    private string? _from;
    private DateTimeOffset _expires;
    private int _served;
    private bool _disposed;

    /// <param name="window">
    ///   How long an invite stays good for. Defaults to <see cref="Window"/>; named by tests, which
    ///   otherwise could only check that the door closes by waiting five minutes for it.
    /// </param>
    public AppHandout(IAppShareService app, TimeSpan? window = null)
    {
        _app = app ?? throw new ArgumentNullException(nameof(app));
        _window = window is { Ticks: > 0 } chosen ? chosen : Window;
    }

    /// <summary>The address to put on a tap or a QR, or null when nothing is being offered.</summary>
    public string? Invite { get; private set; }

    /// <summary>How many phones have taken it.</summary>
    public int Served => _served;

    /// <summary>How long this invite has left, or zero when it is not running.</summary>
    public TimeSpan Remaining =>
        Invite is null ? TimeSpan.Zero
        : _expires - DateTimeOffset.UtcNow is { Ticks: > 0 } left ? left
        : TimeSpan.Zero;

    /// <summary>Raised when a phone starts taking it, and again when it finishes. UI re-renders on it.</summary>
    public event Action? Changed;

    /// <summary>
    /// Begin offering the app. Returns the address to hand over, or null when there is nothing to
    /// hand over or nowhere to hand it from.
    /// </summary>
    /// <remarks>
    /// Calling it again while one is running returns the same invite rather than minting a second —
    /// two live tokens for one phone is two things to expire and one of them will be forgotten.
    /// </remarks>
    /// <param name="host">
    ///   The address to advertise. Normally left null, which uses this device's address on the
    ///   network it shares with the phone being handed to. Named explicitly by tests, and by any
    ///   caller that knows better than the first interface that answers.
    /// </param>
    /// <param name="from">
    ///   The giver's AetherTag, shown on the page so the person can see who is offering rather than
    ///   only an address and a hex string.
    /// </param>
    public string? Start(string? host = null, string? from = null)
    {
        lock (_gate)
        {
            if (_disposed) return null;
            if (Invite is not null && Remaining > TimeSpan.Zero) return Invite;

            StopLocked();

            if (!_app.IsSupported || _app.SizeBytes <= 0) return null;
            if ((host ?? LocalAddress()?.ToString()) is not { Length: > 0 } advertise) return null;

            try
            {
                // Bound to the shared network, not to loopback: the point is that another phone can
                // reach it. Port 0 so the operating system picks one that is free.
                _listener = new TcpListener(IPAddress.Any, 0);
                _listener.Start();
            }
            catch (SocketException)
            {
                _listener = null;
                return null;
            }

            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _token = ShareInvite.NewToken();
            _from = from;
            _expires = DateTimeOffset.UtcNow + _window;
            _served = 0;
            _life = new CancellationTokenSource();
            Invite = ShareInvite.Compose(advertise, port, _token);
        }

        _ = Task.Run(() => AcceptAsync(_life!.Token), CancellationToken.None);
        _ = Task.Run(() => ExpireAsync(_life!.Token), CancellationToken.None);
        Changed?.Invoke();
        return Invite;
    }

    /// <summary>Stop offering it, now.</summary>
    public void Stop()
    {
        lock (_gate) StopLocked();
        Changed?.Invoke();
    }

    private void StopLocked()
    {
        try { _life?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _life?.Dispose();
        _life = null;
        _listener = null;
        Invite = null;
        _token = string.Empty;
    }

    /// <summary>Close the door on time, whether or not anybody came through it.</summary>
    private async Task ExpireAsync(CancellationToken life)
    {
        try
        {
            var left = Remaining;
            if (left > TimeSpan.Zero) await Task.Delay(left, life).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }

        if (!life.IsCancellationRequested) Stop();
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
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { return; }

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

                if (await ReadRequestLineAsync(stream, life).ConfigureAwait(false) is not { } request)
                    return;

                // One token, two things behind it: the page that explains the offer, and the package
                // itself. Everything else is somebody scanning the network.
                var allowed = (request.Method is "GET" or "HEAD")
                              && Remaining > TimeSpan.Zero
                              && ShareInvite.PathCarries(request.Path, _token);

                if (!allowed)
                {
                    await NotFoundAsync(stream, life).ConfigureAwait(false);
                    return;
                }

                var wantsPackage = request.Path.EndsWith(ShareInvite.FileName, StringComparison.Ordinal);

                if (!wantsPackage)
                {
                    // The page is free — it is a few kilobytes of text, and a friend re-reading it
                    // before they press the button must not spend one of the handovers.
                    await SendCardAsync(stream, request.Method == "HEAD", life).ConfigureAwait(false);
                    return;
                }

                if (_served >= MaxHandovers)
                {
                    await NotFoundAsync(stream, life).ConfigureAwait(false);
                    return;
                }

                await SendInstallerAsync(stream, request.Method == "HEAD", life).ConfigureAwait(false);
            }
            catch (Exception) { /* the taker walked off, or the network did */ }
        }
    }

    private static Task NotFoundAsync(NetworkStream stream, CancellationToken life) =>
        WriteAsync(stream, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", life);

    /// <summary>
    /// The page a tap lands on: who is offering, what it is, and how big.
    /// </summary>
    private async Task SendCardAsync(NetworkStream stream, bool headOnly, CancellationToken life)
    {
        var body = Encoding.UTF8.GetBytes(
            ShareCard.Render(_from, _app.SizeBytes, ShareInvite.DownloadFrom(_token)));

        var head = new StringBuilder()
            .Append("HTTP/1.1 200 OK\r\n")
            .Append("Content-Type: text/html; charset=utf-8\r\n")
            .Append("Content-Length: ").Append(body.Length).Append("\r\n")
            // Nothing here is worth keeping: the token behind it expires in minutes, and a page held
            // in a browser cache is a page that outlives the offer it describes.
            .Append("Cache-Control: no-store\r\n")
            .Append("Connection: close\r\n\r\n")
            .ToString();

        await WriteAsync(stream, head, life).ConfigureAwait(false);
        if (headOnly) return;

        await stream.WriteAsync(body, life).ConfigureAwait(false);
        await stream.FlushAsync(life).ConfigureAwait(false);
    }

    private async Task SendInstallerAsync(NetworkStream stream, bool headOnly, CancellationToken life)
    {
        var size = _app.SizeBytes;

        var head = new StringBuilder()
            .Append("HTTP/1.1 200 OK\r\n")
            .Append("Content-Type: application/vnd.android.package-archive\r\n")
            .Append("Content-Length: ").Append(size).Append("\r\n")
            // So the browser saves it as something Android will offer to install rather than as a
            // file with no extension that nothing on the phone will open.
            .Append("Content-Disposition: attachment; filename=\"").Append(ShareInvite.FileName).Append("\"\r\n")
            .Append("Connection: close\r\n\r\n")
            .ToString();

        await WriteAsync(stream, head, life).ConfigureAwait(false);
        if (headOnly) return;

        Interlocked.Increment(ref _served);
        Changed?.Invoke();

        try
        {
            await using var installer = await _app.OpenInstallerAsync(life).ConfigureAwait(false);
            if (installer is null) return;

            // Copied in chunks rather than read whole. The package is tens of megabytes and the phones
            // this is for have three gigabytes in them — holding the entire thing in memory to hand it
            // to a socket that takes it a few kilobytes at a time is the kind of allocation that gets
            // an app killed midway through the one moment it needed to work.
            await installer.CopyToAsync(stream, 64 * 1024, life).ConfigureAwait(false);
            await stream.FlushAsync(life).ConfigureAwait(false);
        }
        finally { Changed?.Invoke(); }
    }

    private sealed record Request(string Method, string Path);

    /// <summary>
    /// Read just the request line, and only as much as one could possibly be.
    /// </summary>
    /// <remarks>
    /// This is open on a network, so it is fed whatever anybody feels like sending. It reads a bounded
    /// number of bytes, stops at the first newline, and never grows a buffer on a caller's say-so.
    /// </remarks>
    private static async Task<Request?> ReadRequestLineAsync(NetworkStream stream, CancellationToken life)
    {
        var buffer = new byte[2048];
        var got = 0;

        while (got < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(got), life).ConfigureAwait(false);
            if (n <= 0) return null;
            got += n;

            var end = Array.IndexOf(buffer, (byte)'\n', 0, got);
            if (end < 0) continue;

            var line = Encoding.ASCII.GetString(buffer, 0, end).TrimEnd('\r');
            var parts = line.Split(' ');
            return parts.Length >= 2 ? new Request(parts[0], parts[1]) : null;
        }

        return null;   // 2KB with no newline in it is not a request line
    }

    private static Task WriteAsync(NetworkStream stream, string text, CancellationToken life) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes(text), life).AsTask();

    /// <summary>This device's address on the network it shares with the phone being handed to.</summary>
    public static IPAddress? LocalAddress()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(ua.Address))
                        return ua.Address;
            }
        }
        catch (NetworkInformationException) { }

        return null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            StopLocked();
        }
    }
}

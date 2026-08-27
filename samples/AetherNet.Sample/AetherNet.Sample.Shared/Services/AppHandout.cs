// SPDX-License-Identifier: MIT

using AetherNet.Browser;
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
    /// <para>
    /// Ninety seconds. Long enough to tap, join and fetch — the package itself moves in about nine —
    /// and short enough that the cost of being wrong is small.
    /// </para>
    /// <para>
    /// <b>It was five minutes, and that was the wrong unit entirely.</b> While this offer is open the
    /// guest may be off their own Wi-Fi, and the close-early path only fires once somebody has
    /// actually finished taking the app. Measured: a Redmi joined the group, never fetched, and was
    /// held off its own network for the full five minutes with nothing happening at all. A guest who
    /// changes their mind should cost them seconds, not the rest of the afternoon.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(90);

    /// <summary>
    /// How many phones one invite will serve.
    /// </summary>
    /// <remarks>
    /// Three, because the use case is three friends at a table and asking somebody to tap three times
    /// is asking them to do the same thing three times. It is not unlimited: a token that never spends
    /// is a token that leaks.
    /// </remarks>
    public const int MaxHandovers = 3;

    /// <summary>
    /// What this is doing, in words.
    /// </summary>
    /// <remarks>
    /// This class had no voice at all, and the cost was hours: a guest could join the network and
    /// either fetch the app, fetch nothing, or be refused, and all three looked identical from here.
    /// Every branch below says which one happened.
    /// </remarks>
    public event Action<string>? Say;

    private void S(string message) => Say?.Invoke(message);

    private readonly IAppShareService _app;
    private readonly TimeSpan _window;
    private readonly object _gate = new();

    private TcpListener? _listener;
    private CaptivePortal? _portal;
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

    /// <summary>
    /// Where the installer itself sits — an address no human being is ever shown.
    /// </summary>
    /// <remarks>
    /// The objection to the earlier design was never the browser, it was the raw address a stranger
    /// was asked to read and trust. This one is read by an operating system, which has no opinion
    /// about how it looks, and is never rendered on any screen.
    /// </remarks>
    public string? Package =>
        Invite is { Length: > 0 } invite ? string.Concat(invite, "/", ShareInvite.FileName) : null;

    /// <summary>
    /// The giver's own card, shown to whoever is being handed the app.
    /// </summary>
    /// <remarks>
    /// The page a stranger sees before deciding to trust any of this used to be a generic block of
    /// text. It should be the person's own — their name, their words, their colours — because a card
    /// that looks like somebody is a different proposition from a card that looks like an installer.
    /// Null is fine and still produces a page.
    /// </remarks>
    public CardDocument? Card { get; set; }

    /// <summary>
    /// The pictures that card names, already fetched, by content hash.
    /// </summary>
    /// <remarks>
    /// Resolved before the group goes up rather than while a stranger's browser is waiting: this
    /// server answers one request with one page, and reaching into the mesh mid-response to assemble a
    /// photograph is a page that hangs at the only moment it has to be instant. Empty is fine — the
    /// page then draws its generated masthead instead.
    /// </remarks>
    public IReadOnlyDictionary<string, string> Pictures { get; set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>What the far end will call what it just installed.</summary>
    public string PackageName => _app.PackageName;

    /// <summary>Which component in it a receiving system hands authority to.</summary>
    public string AdminComponent => _app.AdminComponent;

    /// <summary>
    /// The fingerprint of what is being handed over.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the part that makes the handover a claim rather than a link. A tap that names a place
    /// can be pointed anywhere; a tap that names the bytes cannot, because the far end refuses
    /// anything that hashes differently. So the bytes are then free to come from us, or from anyone
    /// else nearby already holding them, without weakening what was promised.
    /// </para>
    /// <para>
    /// Computed rather than cached: it is tens of megabytes read once at the moment of arming, which
    /// is a fair price on a phone with three gigabytes in it, and a stale fingerprint would fail in
    /// the most confusing way available — a tap that lands, downloads, and then silently refuses.
    /// </para>
    /// </remarks>
    public async Task<string?> FingerprintAsync(CancellationToken cancellationToken = default)
    {
        var installer = await _app.ReadInstallerAsync(cancellationToken).ConfigureAwait(false);
        return installer is { Length: > 0 } ? Provisioning.Fingerprint(installer) : null;
    }

    /// <summary>
    /// Whether the guest's phone will be sent to the card by its own operating system.
    /// </summary>
    /// <remarks>
    /// False means the DNS port could not be taken, and the difference is not cosmetic: without it a
    /// guest joins the network, their probe reaches nothing, and they are left looking at a Wi-Fi
    /// symbol wondering what was supposed to happen.
    /// </remarks>
    public bool PortalUp { get; private set; }

    /// <summary>How many lookups a guest has made — proof somebody actually joined.</summary>
    public int PortalAsked => _portal?.Asked ?? 0;

    /// <summary>
    /// Give the offer its full window back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For a deliberate act by the person holding the phone, not a way to keep a door open. Choosing
    /// what the tap hands over is such an act — and choosing the heavier one costs seconds of reading
    /// the installer, so leaving the clock running punishes exactly the choice that needs the most
    /// time.
    /// </para>
    /// <para>
    /// Measured: an offer opened at 19:11:53 with a five-minute window, switched to the install tap at
    /// 19:13:03, and expired at 19:16:52 with the tag still armed — silently taking the tap with it
    /// while somebody stood there holding two phones together.
    /// </para>
    /// </remarks>
    public void Extend()
    {
        if (Invite is null) return;

        _expires = DateTimeOffset.UtcNow + _window;
        Changed?.Invoke();
    }

    /// <summary>
    /// How long the offer stays open after somebody has actually taken it.
    /// </summary>
    /// <remarks>
    /// Long enough for a second friend at the same table to tap straight away, short enough that the
    /// person who just received it gets their own Wi-Fi back almost immediately.
    /// </remarks>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Bring the closing time forward, because the thing it was open for has happened.
    /// </summary>
    /// <remarks>
    /// <para>
    /// While this group is up, the phone that joined it may be off its own Wi-Fi entirely — one radio
    /// cannot always hold both, and if their network sits on a channel a group owner is barred from
    /// there is no channel we can share. Measured on a Redmi: internet gone, every time.
    /// </para>
    /// <para>
    /// The package moves in about nine seconds. Holding the door open for the remaining four and a
    /// half minutes turns a brief interruption into a long one, for nobody's benefit.
    /// </para>
    /// </remarks>
    public void CloseSoon()
    {
        if (Invite is null) return;
        if (Remaining <= Grace) return;

        _expires = DateTimeOffset.UtcNow + Grace;
        Changed?.Invoke();
    }

    /// <summary>How long this invite has left, or zero when it is not running.</summary>
    public TimeSpan Remaining =>
        Invite is null ? TimeSpan.Zero
        : _expires - DateTimeOffset.UtcNow is { Ticks: > 0 } left ? left
        : TimeSpan.Zero;

    /// <summary>Raised when a phone starts taking it, and again when it finishes. UI re-renders on it.</summary>
    public event Action? Changed;

    /// <summary>
    /// Raised the instant a package has finished going out the door.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists so the network can come down as soon as it is not needed. A phone has one radio,
    /// and while our group is up the friend who joined it may be off their own Wi-Fi entirely —
    /// measured, repeatedly, on a Redmi that lost its internet every time.
    /// </para>
    /// <para>
    /// We cannot always prevent that: the channel their phone is on may be one a group owner is barred
    /// from, and then there is no channel we can share. What we can do is make it brief. The package
    /// moves in about nine seconds on this link; holding the group open for five minutes afterwards
    /// turns nine seconds of no internet into five minutes of it, for nothing.
    /// </para>
    /// </remarks>
    public event Action? Delivered;

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

        // Every name the guest looks up resolves here for as long as the offer stands. Without it
        // their connectivity probe never reaches us and their phone decides the internet is fine.
        S($"serving from {new Uri(Invite!).Host}:{new Uri(Invite!).Port}");

        if (IPAddress.TryParse(new Uri(Invite!).Host, out var here))
        {
            _portal = new CaptivePortal(here);
            PortalUp = _portal.Start();

            // Worth saying either way. Without the portal a guest joins, their phone asks for a name,
            // nothing answers, and they are left looking at a Wi-Fi symbol wondering what happened —
            // which is indistinguishable, from here, from a guest who never joined at all.
            S(PortalUp
                ? "the sign-in sheet is armed"
                : "no sign-in sheet — this phone could not take the DNS port");
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
        try { _portal?.Dispose(); } catch { }
        _portal = null;
        PortalUp = false;

        try { _life?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _life?.Dispose();
        _life = null;
        _listener = null;
        Invite = null;
        _token = string.Empty;
    }

    /// <summary>Close the door on time, whether or not anybody came through it.</summary>
    /// <remarks>
    /// <b>Re-read each time round, not slept once.</b> A single delay for the whole window is a
    /// deadline nothing can move afterwards — <see cref="Extend"/> would push <c>_expires</c> out and
    /// the offer would still die at the original moment, because the sleep was already scheduled.
    /// That is exactly how a tap was lost: the tag was armed, the person was holding two phones
    /// together, and the door shut behind them on a clock they had already been given more of.
    /// </remarks>
    private async Task ExpireAsync(CancellationToken life)
    {
        try
        {
            while (Remaining is { Ticks: > 0 } left)
                await Task.Delay(left, life).ConfigureAwait(false);
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

                // The guest's phone checks whether the internet is behind this network before it
                // does anything else. Sending it somewhere is what makes Android raise its own
                // sign-in sheet — system-drawn, titled with the network's name — which is the whole
                // reason a stranger ever sees this without reading an address.
                if (CaptivePortal.IsProbe(request.Path) && Invite is { } offer)
                {
                    await WriteAsync(stream, CaptivePortal.RedirectTo(offer), life).ConfigureAwait(false);
                    return;
                }

                // One token, two things behind it: the page that explains the offer, and the package
                // itself. Everything else is somebody scanning the network.
                var allowed = (request.Method is "GET" or "HEAD")
                              && Remaining > TimeSpan.Zero
                              && ShareInvite.PathCarries(request.Path, _token);

                if (!allowed)
                {
                    S($"refused {request.Method} {request.Path} — wrong token, or the offer has closed");
                    await NotFoundAsync(stream, life).ConfigureAwait(false);
                    return;
                }

                var wantsPackage = request.Path.EndsWith(ShareInvite.FileName, StringComparison.Ordinal);

                if (!wantsPackage)
                {
                    S("a guest is reading the card");
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
        // The typefaces travel with it. This page is going to a phone that has joined a Wi-Fi Direct
        // group and has no route to the internet, so a linked font is a font that never arrives —
        // and the look collapsing to the handset's own faces is the first impression this network
        // gets to make. Over the group link the extra bytes are a few milliseconds.
        var body = Encoding.UTF8.GetBytes(
            CardPage.Render(
                Card, _from, _app.SizeBytes, ShareInvite.DownloadFrom(_token),
                assetPath: hash => Pictures.GetValueOrDefault(hash),
                fonts: PageAssets.Face));

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
        S($"handing over the app — {_served} of {MaxHandovers}");
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

            S("● the app has landed on their phone");
            Delivered?.Invoke();
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

// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// Making the other phone's own operating system ask the question.
///
/// <para>
/// A phone that joins a network immediately checks whether the internet is behind it, by fetching a
/// known URL and expecting an empty <c>204</c>. If something else comes back, Android concludes it is
/// behind a sign-in page and raises its own sheet — the one everybody has seen in a hotel or a café,
/// titled with the network's name.
/// </para>
///
/// <para>
/// <b>That sheet is the entire reason this class exists.</b> It is drawn by the receiving phone, in
/// its own voice, above a network called after a person. Everything else we tried ended with a
/// stranger reading a raw address under a browser's "not secure" warning and deciding, correctly, to
/// back away. Nothing we can put on a web page buys back that trust, because the browser is
/// simultaneously telling them not to trust us. So we stop arguing with the frame and use a different
/// one.
/// </para>
///
/// <para>
/// Two pieces are needed and neither is optional. The phone resolves a hostname before it can probe,
/// so we have to answer DNS; and the probe itself has to come back as something other than a silent
/// 204, or the phone decides the internet is fine and never asks anybody anything.
/// </para>
/// </summary>
public sealed class CaptivePortal : IDisposable
{
    /// <summary>The port a DNS server listens on. Not negotiable — the phone will not ask anywhere else.</summary>
    public const int DnsPort = 53;

    /// <summary>How long a client should believe our answer.</summary>
    /// <remarks>
    /// Sixty seconds. Long enough that a phone is not re-asking constantly, short enough that nothing
    /// is still pointing here after the handover is over and this network is gone.
    /// </remarks>
    public const int AnswerSeconds = 60;

    private readonly IPAddress _us;
    private UdpClient? _dns;
    private CancellationTokenSource? _life;
    private bool _disposed;

    /// <param name="us">This phone's address on the network it is hosting.</param>
    public CaptivePortal(IPAddress us) => _us = us ?? throw new ArgumentNullException(nameof(us));

    /// <summary>How many phones have asked us where something is — proof a guest actually joined.</summary>
    public int Asked { get; private set; }

    /// <summary>Start answering DNS. Returns false when the port could not be taken.</summary>
    /// <remarks>
    /// Binding 53 is unprivileged on Android, unlike a desktop, because the app owns the interface it
    /// is hosting on. It can still fail — something else on the handset may hold it — and a portal
    /// that cannot answer DNS is one the guest never sees, so the caller is told rather than left to
    /// wonder.
    /// </remarks>
    public bool Start()
    {
        if (_disposed) return false;
        if (_dns is not null) return true;

        try
        {
            _dns = new UdpClient(new IPEndPoint(IPAddress.Any, DnsPort));
        }
        catch (SocketException)
        {
            _dns = null;
            return false;
        }

        _life = new CancellationTokenSource();
        _ = Task.Run(() => AnswerAsync(_life.Token), CancellationToken.None);
        return true;
    }

    private async Task AnswerAsync(CancellationToken life)
    {
        var dns = _dns;
        if (dns is null) return;

        while (!life.IsCancellationRequested && !_disposed)
        {
            UdpReceiveResult query;
            try { query = await dns.ReceiveAsync(life).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }

            if (Answer(query.Buffer, _us) is not { } reply) continue;

            Asked++;
            try { await dns.SendAsync(reply, reply.Length, query.RemoteEndPoint).ConfigureAwait(false); }
            catch (SocketException) { /* the guest went */ }
        }
    }

    /// <summary>
    /// Answer any question with our own address.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every name resolves here, which is what a captive portal is: for as long as a phone is on this
    /// network, everything it looks for is us. That sounds heavy-handed and is exactly right — the
    /// network exists for one purpose, lasts a couple of minutes, and the alternative is the guest's
    /// connectivity probe reaching the real internet and their phone concluding all is well.
    /// </para>
    /// <para>
    /// Only A queries are answered. A phone asking for anything else gets no reply and moves on,
    /// which is better than a malformed one it has to decide what to do with.
    /// </para>
    /// </remarks>
    public static byte[]? Answer(byte[]? query, IPAddress us)
    {
        if (query is null || query.Length < 12) return null;
        if (us.AddressFamily != AddressFamily.InterNetwork) return null;

        // A response, not a question; and exactly one question in it.
        var flags = BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(2));
        if ((flags & 0x8000) != 0) return null;
        if (BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(4)) != 1) return null;

        // Walk the name to find where the question ends.
        var at = 12;
        while (at < query.Length && query[at] != 0)
        {
            var label = query[at];
            if ((label & 0xC0) != 0) return null;        // a pointer has no business in a question
            at += label + 1;
        }
        if (at >= query.Length) return null;
        at++;                                            // the root label

        if (at + 4 > query.Length) return null;
        var type = BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(at));
        if (type != 1) return null;                      // A records only

        var questionEnd = at + 4;
        var address = us.GetAddressBytes();

        var reply = new byte[questionEnd + 16];
        Array.Copy(query, reply, questionEnd);

        // Standard response, no error, recursion available.
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(2), 0x8180);
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(6), 1);   // one answer
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(8), 0);
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(10), 0);

        var w = questionEnd;
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(w), 0xC00C); w += 2;   // the name, by pointer
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(w), 1); w += 2;        // A
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(w), 1); w += 2;        // IN
        BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(w), AnswerSeconds); w += 4;
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(w), 4); w += 2;
        address.CopyTo(reply, w);

        return reply;
    }

    /// <summary>
    /// Is this the probe a phone uses to decide whether it is behind a sign-in page?
    /// </summary>
    /// <remarks>
    /// Android asks for <c>/generate_204</c>; other platforms have their own, and phones sold in
    /// different places are pointed at different hosts. Matching on the path rather than the host is
    /// what makes this work on a handset configured for somewhere we have never heard of.
    /// </remarks>
    public static bool IsProbe(string? path) =>
        !string.IsNullOrEmpty(path) &&
        (path.Contains("generate_204", StringComparison.OrdinalIgnoreCase) ||
         path.Contains("gen_204", StringComparison.OrdinalIgnoreCase) ||
         path.Contains("connecttest", StringComparison.OrdinalIgnoreCase) ||
         path.Contains("ncsi.txt", StringComparison.OrdinalIgnoreCase) ||
         path.Contains("hotspot-detect", StringComparison.OrdinalIgnoreCase) ||
         path.Contains("success.txt", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// What to send back so the phone raises its sign-in sheet.
    /// </summary>
    /// <remarks>
    /// A redirect, not the page itself. Android takes the <c>Location</c> and opens that in its portal
    /// window, and it is that window — system-drawn, titled with the network's name — that we are
    /// after. Answering with the page directly gets it rendered inside the probe and nobody sees it.
    /// </remarks>
    public static string RedirectTo(string url) =>
        "HTTP/1.1 302 Found\r\n" +
        $"Location: {url}\r\n" +
        "Cache-Control: no-store\r\n" +
        "Content-Length: 0\r\n" +
        "Connection: close\r\n\r\n";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _life?.Cancel(); } catch { }
        try { _dns?.Dispose(); } catch { }

        _life?.Dispose();
        _life = null;
        _dns = null;
    }
}

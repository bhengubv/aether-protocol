// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Security.Cryptography;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// What crosses when you hand somebody the app.
///
/// <para>
/// The person receiving it has nothing installed — no Aether, no account, no mesh. So whatever they
/// are handed has to be something a phone straight out of the box already knows what to do with, and
/// there are exactly two of those: a web address, and a web address. One arrives on a tap, one
/// arrives through the camera. Both are this.
/// </para>
///
/// <para>
/// It points at the giver's own phone. Nothing is fetched from a store, a CDN or a server — the
/// address is a handset on the same network, serving the very APK it is running from. That is the
/// whole point: the app spreads where there is no data to reach a store with.
/// </para>
///
/// <para>
/// The format lives here, in the platform-neutral half, because the tap composes it on Android and
/// the QR renders it everywhere, and the two must agree byte for byte or a phone will download
/// nothing and say nothing about why.
/// </para>
/// </summary>
public static class ShareInvite
{
    /// <summary>The path every invite hangs off, so the server can tell one from a stray request.</summary>
    public const string Path = "/tmb/";

    /// <summary>
    /// What the file is called when it lands in the taker's downloads.
    /// </summary>
    /// <remarks>
    /// It is in the URL rather than left to a header because the thing fetching this is somebody's
    /// stock browser, and a browser names a download after the last segment of the path. A file called
    /// <c>token</c> with no extension is one Android will not offer to install.
    /// </remarks>
    public const string FileName = "aether.apk";

    /// <summary>
    /// Where the package sits, relative to the page that offers it.
    /// </summary>
    /// <remarks>
    /// One press further on than the tap lands. An invite that pointed straight here dropped ninety
    /// megabytes into somebody's downloads with a raw address and a hex string to explain it, which is
    /// what a phishing link looks like — however sound the bytes were.
    /// </remarks>
    public static string DownloadFrom(string token) => string.Concat(Path, token, "/", FileName);

    /// <summary>How many characters of hex the one-time secret is.</summary>
    /// <remarks>
    /// 32 hex characters is 128 bits. This travels in a URL on a shared network, and it is the only
    /// thing standing between "my mate tapped my phone" and "anyone on the café Wi-Fi can pull a
    /// hundred megabytes off my handset for as long as it is switched on".
    /// </remarks>
    public const int TokenLength = 32;

    /// <summary>Mint a fresh secret for one handover.</summary>
    public static string NewToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(TokenLength / 2)).ToLowerInvariant();

    /// <summary>
    /// Compose the address a taker's phone will open.
    /// </summary>
    /// <param name="host">The giver's address on the network the two of them share.</param>
    /// <param name="port">Where the giver is serving.</param>
    /// <param name="token">The one-time secret from <see cref="NewToken"/>.</param>
    /// <remarks>
    /// Plain <c>http</c>, deliberately. There is no certificate authority reachable on a network with
    /// no internet, and a self-signed one would train people to tap through a browser's security
    /// warning to install software — which is a far worse habit to teach than the one it would be
    /// protecting against. What is actually being trusted here is that you watched your friend tap
    /// their phone against yours, and the token is what makes that tap mean something.
    /// </remarks>
    public static string Compose(string host, int port, string token)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("An invite needs somewhere to point.", nameof(host));
        if (port is <= 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), port, "Not a port.");
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("An invite without a secret is an open door.", nameof(token));
        if (!IsHex(token))
            throw new ArgumentException("A token must be hex — it travels in a URL path.", nameof(token));

        return string.Concat(
            "http://", host, ":", port.ToString(CultureInfo.InvariantCulture), Path, token);
    }

    /// <summary>
    /// Read an invite, or say plainly that this was not one.
    /// </summary>
    public static bool TryParse(string? url, out string host, out int port, out string token)
    {
        host = string.Empty;
        port = 0;
        token = string.Empty;

        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp) return false;
        if (uri.IsDefaultPort) return false;                 // an invite always names its port
        if (!uri.AbsolutePath.StartsWith(Path, StringComparison.Ordinal)) return false;

        var candidate = uri.AbsolutePath[Path.Length..].TrimEnd('/');
        if (candidate.Length != TokenLength || !IsHex(candidate)) return false;

        host = uri.Host;
        port = uri.Port;
        token = candidate;
        return true;
    }

    /// <summary>
    /// Does this request's path carry the secret we handed out?
    /// </summary>
    /// <remarks>
    /// Compared in fixed time. The comparison is against a value an unknown caller supplies, and a
    /// plain string equality tells that caller how many leading characters they guessed right — over
    /// a few thousand requests on a quiet network that is enough to walk the token out one character
    /// at a time.
    /// </remarks>
    public static bool PathCarries(string? requestPath, string expectedToken)
    {
        if (string.IsNullOrEmpty(requestPath) || string.IsNullOrEmpty(expectedToken)) return false;
        if (!requestPath.StartsWith(Path, StringComparison.Ordinal)) return false;

        var rest = requestPath[Path.Length..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        var offered = slash < 0 ? rest : rest[..slash];

        if (offered.Length != expectedToken.Length) return false;

        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(offered),
            System.Text.Encoding.ASCII.GetBytes(expectedToken));
    }

    private static bool IsHex(string s)
    {
        foreach (var c in s)
            if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F'))
                return false;
        return s.Length > 0;
    }
}

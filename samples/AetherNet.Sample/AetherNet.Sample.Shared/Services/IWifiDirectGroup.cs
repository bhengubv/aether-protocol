// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// The credentials for one Wi-Fi Direct group — everything a second phone needs to walk straight in.
///
/// <para>
/// These are a <b>secret</b>. Anyone holding them can join the group, so they only ever travel inside
/// an established Signal session; see <see cref="WifiDirectBroker"/>.
/// </para>
/// </summary>
public sealed record WifiDirectCredentials(
    [property: JsonPropertyName("ssid")] string NetworkName,
    [property: JsonPropertyName("pass")] string Passphrase)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>
    /// Read credentials off the wire. Anything malformed comes back null rather than being coerced —
    /// these arrive from another device and are used to join a network.
    /// </summary>
    public static WifiDirectCredentials? Parse(string json)
    {
        try
        {
            var c = JsonSerializer.Deserialize<WifiDirectCredentials>(json, Options);
            return IsUsable(c) ? c : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Could these actually be a Wi-Fi Direct group?
    ///
    /// <para>
    /// Android names every P2P group <c>DIRECT-xy</c>, and refuses a join for anything that is not, so
    /// a name of another shape can only be a mistake or someone trying to steer this phone onto a
    /// network of their choosing. WPA2 puts the passphrase between 8 and 63 characters.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Both fields are checked for null before anything else. They are declared non-nullable, but this
    /// is built from JSON that arrived over a radio — <c>{}</c> deserialises to a record with two null
    /// strings, and a NullReferenceException here would escape the caller's JSON guard entirely.
    /// </remarks>
    public static bool IsUsable(WifiDirectCredentials? c) =>
        c is not null &&
        !string.IsNullOrEmpty(c.NetworkName) &&
        !string.IsNullOrEmpty(c.Passphrase) &&
        c.NetworkName.StartsWith("DIRECT-", StringComparison.Ordinal) &&
        c.NetworkName.Length is > 7 and <= 32 &&
        c.Passphrase.Length is >= 8 and <= 63;
}

/// <summary>
/// Bringing a Wi-Fi Direct group up <b>without negotiating for it</b>.
///
/// <para>
/// The usual scheme has both phones call <c>connect()</c> and hopes the two land inside Android's
/// window. When they do not — which on these handsets is most of the time — the framework quietly
/// falls back to showing an <b>"Invitation to connect"</b> dialog on the other phone, which nobody is
/// looking at. The group then never forms, and the dialog steals window focus on top of that, so the
/// app appears to have stopped responding as well.
/// </para>
///
/// <para>
/// So: stop racing. One side creates the group outright and becomes its owner, hands the credentials
/// to the other over the link that already works, and the other joins by name. No discovery, no
/// negotiation, no dialog, and no dependence on two radios agreeing about timing.
/// </para>
/// </summary>
public interface IWifiDirectGroup
{
    /// <summary>Whether this host can do any of this — false everywhere but a phone.</summary>
    bool IsSupported { get; }

    /// <summary>
    /// Become the group owner and return what someone else needs to join. Null if the group could not
    /// be created.
    /// </summary>
    Task<WifiDirectCredentials?> HostAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Create the group under credentials the Circle can already derive, so nothing has to be sent.
    /// </summary>
    Task<WifiDirectCredentials?> HostAsync(WifiDirectCredentials? wanted,
        CancellationToken cancellationToken = default);

    /// <summary>Join a group by name and passphrase, with no discovery and no invitation.</summary>
    Task<bool> JoinAsync(WifiDirectCredentials credentials, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether this phone is in a group right now.
    /// </summary>
    /// <remarks>
    /// Asked rather than remembered. A join that was accepted and then quietly failed to form leaves
    /// the caller believing it succeeded — which is exactly how two phones ended up sitting four
    /// seconds out of step with nothing retrying.
    /// </remarks>
    bool IsInGroup => false;

    /// <summary>Leave whatever group this phone is in. Safe when it is in none.</summary>
    Task LeaveAsync();

    /// <summary>
    /// The group has gone — the other phone left, or the radio dropped it.
    /// </summary>
    /// <remarks>
    /// The radio deliberately does not put it back itself. It has no idea who ought to be in a group
    /// with this phone; that is the contact list's business, and it is what decides whether to host or
    /// to join. A radio that reconnected on its own would be guessing.
    /// </remarks>
    event Action? GroupLost;
}

/// <summary>Stands in where there are no radios — the Web head, desktop.</summary>
public sealed class NullWifiDirectGroup : IWifiDirectGroup
{
    public bool IsSupported => false;
    public Task<WifiDirectCredentials?> HostAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<WifiDirectCredentials?>(null);
    public Task<WifiDirectCredentials?> HostAsync(WifiDirectCredentials? wanted,
        CancellationToken cancellationToken = default)
        => Task.FromResult<WifiDirectCredentials?>(null);
    public Task<bool> JoinAsync(WifiDirectCredentials credentials, CancellationToken cancellationToken = default)
        => Task.FromResult(false);
    public Task LeaveAsync() => Task.CompletedTask;

    /// <summary>Never raised — there is no group here to lose.</summary>
    public event Action? GroupLost { add { } remove { } }
}

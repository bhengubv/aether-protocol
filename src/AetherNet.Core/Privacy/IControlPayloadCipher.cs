// SPDX-License-Identifier: MIT

using System.Threading;
using System.Threading.Tasks;

namespace AetherNet.Privacy;

/// <summary>
/// Session-scoped encryption seam for CONTROL payloads (profile, tip, …) that originate in
/// <c>AetherNet.Core</c>, which cannot see the Messaging-layer <c>IMessageEnvelopeCipher</c>. A host backs
/// this with the Signal Protocol (via the Messaging adapter), so a control service can seal its payload to
/// a specific peer without Core taking a dependency on the crypto stack.
///
/// <para><b>Security rule:</b> if <see cref="EncryptAsync"/> returns null there is no session — the caller
/// MUST NOT downgrade to cleartext. It skips (or queues) the send. Control PII never goes out in clear.</para>
/// </summary>
public interface IControlPayloadCipher
{
    /// <summary>Ciphertext of <paramref name="plaintext"/> sealed to <paramref name="recipientUhid"/>, or null if no session.</summary>
    Task<byte[]?> EncryptAsync(string recipientUhid, byte[] plaintext, CancellationToken cancellationToken = default)
        => Task.FromResult<byte[]?>(null);

    /// <summary>Plaintext of <paramref name="ciphertext"/> from <paramref name="senderUhid"/>, or null if it can't be opened.</summary>
    Task<byte[]?> DecryptAsync(string senderUhid, byte[] ciphertext, CancellationToken cancellationToken = default)
        => Task.FromResult<byte[]?>(null);

    /// <summary>True if a session with <paramref name="peerUhid"/> currently exists.</summary>
    bool HasSession(string peerUhid) => false;
}

/// <summary>
/// No-session stub — every call returns null/false, so a control service with no cipher configured simply
/// does not emit (secure by default) rather than falling back to cleartext.
/// </summary>
public sealed class NullControlPayloadCipher : IControlPayloadCipher
{
}

// SPDX-License-Identifier: MIT

using AetherNet.Privacy;

namespace AetherNet.Messaging;

/// <summary>
/// Bridges the Core-level <see cref="IControlPayloadCipher"/> to the Messaging-layer
/// <see cref="IMessageEnvelopeCipher"/> (Signal-backed), so control services in <c>AetherNet.Core</c> can
/// seal their payloads through the same real Signal session used for message bodies.
/// </summary>
public sealed class ControlPayloadCipherAdapter : IControlPayloadCipher
{
    private readonly IMessageEnvelopeCipher _inner;

    public ControlPayloadCipherAdapter(IMessageEnvelopeCipher inner)
        => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public Task<byte[]?> EncryptAsync(string recipientUhid, byte[] plaintext, CancellationToken cancellationToken = default)
        => _inner.EncryptAsync(recipientUhid, plaintext, cancellationToken);

    public Task<byte[]?> DecryptAsync(string senderUhid, byte[] ciphertext, CancellationToken cancellationToken = default)
        => _inner.DecryptAsync(senderUhid, ciphertext, cancellationToken);

    public bool HasSession(string peerUhid) => _inner.HasSession(peerUhid);
}

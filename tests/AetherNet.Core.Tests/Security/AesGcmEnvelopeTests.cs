// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;
using AetherNet.Security;
using Xunit;

namespace AetherNet.Core.Tests.Security;

public class AesGcmEnvelopeTests
{
    private static byte[] NewKey() => RandomNumberGenerator.GetBytes(AesGcmEnvelope.KeySize);

    [Fact]
    public void RoundTrip_ShortPayload_DecryptsToOriginal()
    {
        var key = NewKey();
        var plaintext = Encoding.UTF8.GetBytes("hello aether");

        var envelope = AesGcmEnvelope.Encrypt(key, plaintext);
        var decrypted = AesGcmEnvelope.Decrypt(key, envelope);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void RoundTrip_EmptyPayload_Succeeds()
    {
        var key = NewKey();
        var plaintext = Array.Empty<byte>();

        var envelope = AesGcmEnvelope.Encrypt(key, plaintext);
        var decrypted = AesGcmEnvelope.Decrypt(key, envelope);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void RoundTrip_OneMegabytePayload_Succeeds()
    {
        var key = NewKey();
        var plaintext = RandomNumberGenerator.GetBytes(1 << 20);

        var envelope = AesGcmEnvelope.Encrypt(key, plaintext);
        var decrypted = AesGcmEnvelope.Decrypt(key, envelope);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Envelope_HasNonceTagCipherLayout()
    {
        var key = NewKey();
        var plaintext = Encoding.UTF8.GetBytes("abc");

        var envelope = AesGcmEnvelope.Encrypt(key, plaintext);

        // [nonce(12)][tag(16)][cipher(N)]
        Assert.Equal(12 + 16 + plaintext.Length, envelope.Length);
    }

    [Fact]
    public void Encrypt_DifferentCalls_ProduceDifferentNonces()
    {
        var key = NewKey();
        var plaintext = Encoding.UTF8.GetBytes("same plaintext");

        var a = AesGcmEnvelope.Encrypt(key, plaintext);
        var b = AesGcmEnvelope.Encrypt(key, plaintext);

        // Same key + same plaintext but random nonces → distinct envelopes.
        Assert.NotEqual(a, b);
        // But both decrypt back to the same plaintext.
        Assert.Equal(plaintext, AesGcmEnvelope.Decrypt(key, a));
        Assert.Equal(plaintext, AesGcmEnvelope.Decrypt(key, b));
    }

    [Fact]
    public void Decrypt_WrongKey_Throws()
    {
        var k1 = NewKey();
        var k2 = NewKey();
        var envelope = AesGcmEnvelope.Encrypt(k1, Encoding.UTF8.GetBytes("secret"));

        Assert.Throws<AuthenticationTagMismatchException>(() => AesGcmEnvelope.Decrypt(k2, envelope));
    }

    [Fact]
    public void Decrypt_TamperedCiphertext_Throws()
    {
        var key = NewKey();
        var envelope = AesGcmEnvelope.Encrypt(key, Encoding.UTF8.GetBytes("legitimate"));
        envelope[envelope.Length - 1] ^= 0x01;

        Assert.Throws<AuthenticationTagMismatchException>(() => AesGcmEnvelope.Decrypt(key, envelope));
    }

    [Fact]
    public void Decrypt_TamperedTag_Throws()
    {
        var key = NewKey();
        var envelope = AesGcmEnvelope.Encrypt(key, Encoding.UTF8.GetBytes("legitimate"));
        // Tag lives at offset 12..28.
        envelope[15] ^= 0x80;

        Assert.Throws<AuthenticationTagMismatchException>(() => AesGcmEnvelope.Decrypt(key, envelope));
    }

    [Fact]
    public void Decrypt_TooShort_ThrowsFormatException()
    {
        var key = NewKey();
        // Only nonce, no tag, no cipher.
        var envelope = new byte[12];

        Assert.Throws<FormatException>(() => AesGcmEnvelope.Decrypt(key, envelope));
    }

    [Fact]
    public void Decrypt_Null_Throws()
    {
        var key = NewKey();
        Assert.Throws<FormatException>(() => AesGcmEnvelope.Decrypt(key, null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(64)]
    public void Encrypt_WrongKeySize_Throws(int keySize)
    {
        Assert.Throws<ArgumentException>(() =>
            AesGcmEnvelope.Encrypt(new byte[keySize], Encoding.UTF8.GetBytes("x")));
    }

    [Fact]
    public void Encrypt_NullKey_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            AesGcmEnvelope.Encrypt(null!, Encoding.UTF8.GetBytes("x")));
    }

    [Fact]
    public void KeySize_Is32Bytes()
    {
        Assert.Equal(32, AesGcmEnvelope.KeySize);
    }
}

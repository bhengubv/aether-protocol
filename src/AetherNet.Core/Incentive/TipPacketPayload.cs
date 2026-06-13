// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using AetherNet.Protocol;

namespace AetherNet.Incentive;

/// <summary>
/// Generic "value-earned" relay-tip envelope carried inside a
/// <see cref="PacketType.TipPacket"/> (24). Wire format: UTF-8 JSON with
/// snake_case property names (<c>tipper_uhid</c>, <c>recipient_uhid</c>,
/// <c>amount</c>, <c>traffic_type</c>, <c>reference_id</c>, <c>timestamp</c>,
/// <c>signature</c>).
///
/// <para>
/// This model is deliberately value-agnostic. <see cref="Amount"/> is a bare
/// number with NO units, NO policy, and NO settlement semantics attached at the
/// protocol layer. The protocol carries the signal that one node wishes to credit
/// another for some kind of relayed traffic; what (if anything) that signal is
/// worth is entirely the host's business. A bare node accepts and relays the
/// packet but settles nothing — only a host that has wired an
/// <c>IAetherNetIncentiveProvider</c> override decides how to interpret the value.
/// </para>
///
/// <para>
/// The payload is self-signed by the tipper: <see cref="Signature"/> is an
/// Ed25519 signature over the canonical byte layout produced by
/// <see cref="BuildCanonicalData"/>, computed via the existing identity-signing
/// service. The signature binds the tipper, recipient, amount, traffic type,
/// reference, and timestamp together so an intermediate relay cannot tamper with
/// any field without invalidating it.
/// </para>
/// </summary>
public sealed class TipPacketPayload
{
    /// <summary>UHID of the node offering the tip (the signer of this payload).</summary>
    public string TipperUhid { get; set; } = string.Empty;

    /// <summary>UHID of the node the tip is addressed to.</summary>
    public string RecipientUhid { get; set; } = string.Empty;

    /// <summary>
    /// Generic value being credited. A bare number — the protocol imposes NO unit,
    /// NO minimum, NO maximum, and NO policy. Interpretation is left entirely to the
    /// host's settlement provider.
    /// </summary>
    public decimal Amount { get; set; }

    /// <summary>
    /// Free-form tag describing the kind of relayed traffic this tip is for,
    /// e.g. <c>"message-relay"</c> or <c>"gateway-share"</c>. Opaque to the protocol.
    /// </summary>
    public string TrafficType { get; set; } = string.Empty;

    /// <summary>
    /// Optional correlation id linking this tip to some host-defined unit of work
    /// (e.g. a specific relayed item). Null when the tip stands alone.
    /// </summary>
    public Guid? ReferenceId { get; set; }

    /// <summary>When the tipper created this payload.</summary>
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Ed25519 signature over <see cref="BuildCanonicalData"/>, produced by the
    /// tipper's identity key. Null until the payload has been signed.
    /// </summary>
    public byte[]? Signature { get; set; }

    /// <summary>
    /// Builds the canonical byte array that is signed/verified for this payload.
    /// The <see cref="Signature"/> field itself is excluded from the canonical data.
    ///
    /// <para>
    /// Layout (little-endian lengths, matching the project's signable-data
    /// conventions in <c>PacketSigningService.BuildSignableData</c>):
    /// <code>
    ///   TipperLen(4 LE i32)    || Tipper(UTF-8)
    ///   RecipientLen(4 LE i32) || Recipient(UTF-8)
    ///   AmountLen(4 LE i32)    || Amount(UTF-8, invariant round-trip "G" form)
    ///   TrafficLen(4 LE i32)   || TrafficType(UTF-8)
    ///   ReferenceId(16, all-zero GUID when null)
    ///   TimestampUnixMs(8 LE i64)
    /// </code>
    /// The amount is encoded as its invariant-culture round-trip string so the
    /// signed bytes are stable across locales and decimal scales without baking in
    /// any unit or fixed-point assumption.
    /// </para>
    /// </summary>
    public byte[] BuildCanonicalData()
    {
        var tipperBytes = Encoding.UTF8.GetBytes(TipperUhid);
        var recipientBytes = Encoding.UTF8.GetBytes(RecipientUhid);
        var amountBytes = Encoding.UTF8.GetBytes(Amount.ToString(CultureInfo.InvariantCulture));
        var trafficBytes = Encoding.UTF8.GetBytes(TrafficType);

        var totalLength =
            4 + tipperBytes.Length
            + 4 + recipientBytes.Length
            + 4 + amountBytes.Length
            + 4 + trafficBytes.Length
            + 16 // ReferenceId GUID
            + 8; // Timestamp (i64 LE)

        var buffer = new byte[totalLength];
        var offset = 0;

        offset += WriteLengthPrefixed(buffer, offset, tipperBytes);
        offset += WriteLengthPrefixed(buffer, offset, recipientBytes);
        offset += WriteLengthPrefixed(buffer, offset, amountBytes);
        offset += WriteLengthPrefixed(buffer, offset, trafficBytes);

        // ReferenceId — 16 bytes, all-zero when null.
        (ReferenceId ?? Guid.Empty).TryWriteBytes(buffer.AsSpan(offset, 16));
        offset += 16;

        // Timestamp — Unix milliseconds, little-endian int64.
        BinaryPrimitives.WriteInt64LittleEndian(
            buffer.AsSpan(offset, 8),
            Timestamp.ToUnixTimeMilliseconds());

        return buffer;
    }

    private static int WriteLengthPrefixed(byte[] buffer, int offset, byte[] value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset, 4), value.Length);
        Buffer.BlockCopy(value, 0, buffer, offset + 4, value.Length);
        return 4 + value.Length;
    }
}

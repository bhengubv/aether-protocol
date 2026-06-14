// SPDX-License-Identifier: MIT
using System.Buffers.Binary;
using System.Text;
using AetherNet.Market.Models;

namespace AetherNet.Market;

/// <summary>
/// Canonical byte layout for the content of a <see cref="PoVToken"/> that BOTH the witness and the
/// subject sign with their real Ed25519 identity keys. The <see cref="PoVToken"/> documents the witness
/// signature as covering "(SubjectUhid + TimestampUtc.Ticks + Transport)"; both parties sign these exact
/// bytes so the two signatures are over the same content.
///
/// Wire layout (must stay byte-identical across every language implementation and the CircleAether
/// mirror so a token signed by one node verifies on any other):
///
///   SubjectLen(4 LE i32) || Subject(UTF-8) || TimestampTicks(8 LE i64) || Transport(1)
/// </summary>
internal static class PoVTokenCodec
{
    /// <summary>
    /// Builds the canonical signable bytes for a PoV token body. The same layout is signed by the
    /// witness (on issue) and counter-signed by the subject (on accept).
    /// </summary>
    public static byte[] BuildSignableTokenData(string subjectUhid, long timestampTicks, PoVTransportType transport)
    {
        var subjectBytes = Encoding.UTF8.GetBytes(subjectUhid);
        var data = new byte[4 + subjectBytes.Length + 8 + 1];
        var offset = 0;

        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset, 4), subjectBytes.Length);
        offset += 4;

        subjectBytes.CopyTo(data.AsSpan(offset, subjectBytes.Length));
        offset += subjectBytes.Length;

        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(offset, 8), timestampTicks);
        offset += 8;

        data[offset] = (byte)transport;

        return data;
    }

    /// <summary>Convenience overload taking the token directly.</summary>
    public static byte[] BuildSignableTokenData(PoVToken token)
        => BuildSignableTokenData(token.SubjectUhid, token.TimestampUtc.Ticks, token.TransportUsed);
}

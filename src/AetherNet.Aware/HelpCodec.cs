// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Security.Cryptography;

namespace AetherNet.Aware;

/// <summary>
/// The 23-byte help message, the same whether it goes out as a Bluetooth advert or over the mesh:
/// <c>[format 1][label 6][time 2][sealed 10][check 4]</c>.
/// <list type="bullet">
/// <item><description>The label changes every 15 minutes; only holders of the person's help key can work it out, so
/// only they recognise the message.</description></item>
/// <item><description>The time counts 20 ms steps from the start of the quarter-hour, so a guardian knows when the
/// message was made and ignores an older one heard later.</description></item>
/// <item><description>The sealed part — kind, position, accuracy, battery, the position's age — only key holders can
/// read.</description></item>
/// <item><description>The check refuses anything altered.</description></item>
/// </list>
/// 23 bytes fits a standard Bluetooth advert under a 16-bit service ID (3 + 4 + 23 of its 31 bytes).
/// </summary>
public static class HelpCodec
{
    public const int Length = 23;

    public const byte Format = 0x01;

    /// <summary>How long one label lasts.</summary>
    public const long QuarterHourMs = 15 * 60 * 1000L;

    /// <summary>The time field's step.</summary>
    public const int StepMs = 20;

    private const int LabelLength = 6;
    private const int SealedLength = 10;
    private const int CheckLength = 4;
    private const int HeaderLength = 1 + LabelLength + 2;
    private const int CheckedLength = HeaderLength + SealedLength;
    private const int NoPosition = 0xFFFFFF;
    private const double PositionSteps = 0xFFFFFE;
    private const byte Unknown = 0xFF;

    private static readonly byte[] StreamLabel = "aether-help-stream"u8.ToArray();

    /// <summary>The message sealed with the person's key, made at <paramref name="nowMs"/> (Unix ms).</summary>
    public static byte[] Encode(HelpKey key, HelpMessage message, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentOutOfRangeException.ThrowIfNegative(nowMs);
        var quarter = nowMs / QuarterHourMs;
        var step = (ushort)((nowMs - (quarter * QuarterHourMs)) / StepMs);
        var output = new byte[Length];
        output[0] = Format;
        Label(key, quarter).CopyTo(output.AsSpan(1, LabelLength));
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(1 + LabelLength, 2), step);
        Span<byte> plain = stackalloc byte[SealedLength];
        Pack(message, plain);
        var stream = KeyStream(key, quarter, step);
        for (var i = 0; i < SealedLength; i++)
        {
            output[HeaderLength + i] = (byte)(plain[i] ^ stream[i]);
        }
        Check(key, quarter, output.AsSpan(0, CheckedLength)).CopyTo(output.AsSpan(CheckedLength, CheckLength));
        return output;
    }

    /// <summary>
    /// Reads a message sealed with this key, heard at <paramref name="nowMs"/>. Null when it is not this key's, was
    /// altered, or was made more than a quarter-hour away from this phone's clock.
    /// </summary>
    public static HelpReading? TryRead(HelpKey key, ReadOnlySpan<byte> payload, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (payload.Length != Length || payload[0] != Format || nowMs < 0)
        {
            return null;
        }
        var thisQuarter = nowMs / QuarterHourMs;
        Span<byte> plain = stackalloc byte[SealedLength];
        foreach (var quarter in new[] { thisQuarter, thisQuarter - 1, thisQuarter + 1 })
        {
            if (quarter < 0 || !CryptographicOperations.FixedTimeEquals(Label(key, quarter), payload.Slice(1, LabelLength)))
            {
                continue;
            }
            if (!CryptographicOperations.FixedTimeEquals(Check(key, quarter, payload[..CheckedLength]), payload.Slice(CheckedLength, CheckLength)))
            {
                return null;
            }
            var step = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(1 + LabelLength, 2));
            if (step >= QuarterHourMs / StepMs)
            {
                return null;
            }
            var stream = KeyStream(key, quarter, step);
            for (var i = 0; i < SealedLength; i++)
            {
                plain[i] = (byte)(payload[HeaderLength + i] ^ stream[i]);
            }
            return Unpack(plain) is { } message ? new HelpReading(message, (quarter * QuarterHourMs) + (step * (long)StepMs)) : null;
        }
        return null;
    }

    private static byte[] Label(HelpKey key, long quarter)
    {
        Span<byte> input = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(input, quarter);
        return HMACSHA256.HashData(key.LabelKey, input)[..LabelLength];
    }

    private static byte[] Check(HelpKey key, long quarter, ReadOnlySpan<byte> checkedBytes)
    {
        Span<byte> input = stackalloc byte[CheckedLength + 8];
        checkedBytes.CopyTo(input);
        BinaryPrimitives.WriteInt64BigEndian(input[CheckedLength..], quarter);
        return HMACSHA256.HashData(key.CheckKey, input)[..CheckLength];
    }

    private static byte[] KeyStream(HelpKey key, long quarter, ushort step)
    {
        var input = new byte[StreamLabel.Length + 8 + 2];
        StreamLabel.CopyTo(input, 0);
        BinaryPrimitives.WriteInt64BigEndian(input.AsSpan(StreamLabel.Length, 8), quarter);
        BinaryPrimitives.WriteUInt16BigEndian(input.AsSpan(StreamLabel.Length + 8, 2), step);
        return HMACSHA256.HashData(key.SealKey, input)[..SealedLength];
    }

    private static void Pack(HelpMessage message, Span<byte> plain)
    {
        plain[0] = (byte)message.Kind;
        var hasPosition = message.HasPosition;
        Write24(plain[1..], hasPosition ? Scale(message.Lat!.Value, -90, 180) : NoPosition);
        Write24(plain[4..], hasPosition ? Scale(message.Lon!.Value, -180, 360) : NoPosition);
        plain[7] = message.AccuracyM is { } acc ? (byte)Math.Clamp((int)Math.Ceiling(Math.Max(acc, 0) / 2.0), 0, 254) : Unknown;
        plain[8] = message.BatteryPercent is { } battery ? (byte)Math.Clamp(battery, 0, 100) : Unknown;
        plain[9] = hasPosition && message.FixAgeSeconds is { } age ? (byte)Math.Clamp(age, 0, 254) : Unknown;
    }

    private static HelpMessage? Unpack(ReadOnlySpan<byte> plain)
    {
        if (plain[0] > (byte)HelpKind.Safe)
        {
            return null;
        }
        var lat = Read24(plain[1..]);
        var lon = Read24(plain[4..]);
        var hasPosition = lat != NoPosition && lon != NoPosition;
        return new HelpMessage
        {
            Kind = (HelpKind)plain[0],
            Lat = hasPosition ? Unscale(lat, -90, 180) : null,
            Lon = hasPosition ? Unscale(lon, -180, 360) : null,
            AccuracyM = plain[7] == Unknown ? null : plain[7] * 2,
            BatteryPercent = plain[8] == Unknown ? null : Math.Min((int)plain[8], 100),
            FixAgeSeconds = hasPosition && plain[9] != Unknown ? plain[9] : null,
        };
    }

    private static int Scale(double value, double min, double span) =>
        (int)Math.Round((value - min) / span * PositionSteps);

    private static double Unscale(int value, double min, double span) =>
        min + (Math.Min(value, (int)PositionSteps) / PositionSteps * span);

    private static void Write24(Span<byte> into, int value)
    {
        into[0] = (byte)(value >> 16);
        into[1] = (byte)(value >> 8);
        into[2] = (byte)value;
    }

    private static int Read24(ReadOnlySpan<byte> from) => (from[0] << 16) | (from[1] << 8) | from[2];
}

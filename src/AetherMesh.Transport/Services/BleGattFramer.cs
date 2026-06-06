// SPDX-License-Identifier: MIT

using System.Buffers.Binary;

namespace AetherMesh.Transport.Services;

/// <summary>
/// Handles GATT-layer framing for BLE transport.
///
/// BLE GATT has an MTU ceiling (default <c>1024</c> bytes per
/// <see cref="AetherMesh.Constants.ProtocolConstants.BleMaxPayloadBytes"/>). Large buffers must
/// be split into numbered frames before transmission and reassembled by the receiver.
///
/// Frame wire format (all integers little-endian):
///   [2 bytes] frame_count   — total number of frames in this sequence
///   [2 bytes] frame_index   — zero-based index of this frame (0 .. frame_count-1)
///   [N bytes] payload       — slice of the original data; N ≤ mtu - 4
/// </summary>
public static class BleGattFramer
{
    private const int HeaderSize = 4; // 2 bytes frame_count + 2 bytes frame_index

    /// <summary>
    /// Splits <paramref name="data"/> into one or more GATT frames.
    /// Each frame carries a 4-byte header followed by at most <c>mtu - 4</c> bytes of payload.
    /// </summary>
    /// <param name="data">Raw bytes to frame. May be empty.</param>
    /// <param name="mtu">Maximum bytes per frame (header included). Default: 1024.</param>
    /// <returns>Array of frames ready for transmission.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="mtu"/> is too small to hold even the header.
    /// </exception>
    public static byte[][] Frame(byte[] data, int mtu = 1024)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (mtu <= HeaderSize)
            throw new ArgumentOutOfRangeException(nameof(mtu),
                $"MTU must be greater than {HeaderSize} to fit at least 1 payload byte.");

        int maxPayload = mtu - HeaderSize;

        // Edge case: empty payload is a single frame with zero payload bytes.
        int frameCount = data.Length == 0 ? 1 : (data.Length + maxPayload - 1) / maxPayload;

        // Frame count must fit in uint16.
        if (frameCount > ushort.MaxValue)
            throw new InvalidOperationException(
                $"Data is too large to frame with MTU {mtu}: would require {frameCount} frames " +
                $"(maximum {ushort.MaxValue}).");

        var frames = new byte[frameCount][];

        for (int i = 0; i < frameCount; i++)
        {
            int offset = i * maxPayload;
            int payloadLen = Math.Min(maxPayload, data.Length - offset);
            if (payloadLen < 0) payloadLen = 0; // empty-data edge case on frame 0

            var frame = new byte[HeaderSize + payloadLen];
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(0, 2), (ushort)frameCount);
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2, 2), (ushort)i);

            if (payloadLen > 0)
                Buffer.BlockCopy(data, offset, frame, HeaderSize, payloadLen);

            frames[i] = frame;
        }

        return frames;
    }

    /// <summary>
    /// Collects an ordered sequence of frames and reassembles the original data.
    /// </summary>
    /// <param name="frames">Frames in transmission order (index 0 first).</param>
    /// <returns>
    /// Reassembled bytes when all frames are present and valid; <c>null</c> when the
    /// sequence is incomplete, empty, or contains a header inconsistency.
    /// </returns>
    public static byte[]? Reassemble(IEnumerable<byte[]> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);

        var list = frames is List<byte[]> l ? l : new List<byte[]>(frames);

        if (list.Count == 0)
            return null;

        // Read expected count from the first frame.
        if (list[0].Length < HeaderSize)
            return null;

        int expectedCount = BinaryPrimitives.ReadUInt16LittleEndian(list[0].AsSpan(0, 2));

        if (list.Count != expectedCount)
            return null;

        // Validate every frame: frame_count must be consistent, frame_index must match position.
        int totalPayload = 0;
        for (int i = 0; i < list.Count; i++)
        {
            var frame = list[i];
            if (frame.Length < HeaderSize)
                return null;

            int fc = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(0, 2));
            int fi = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(2, 2));

            if (fc != expectedCount || fi != i)
                return null;

            totalPayload += frame.Length - HeaderSize;
        }

        // Reassemble.
        var result = new byte[totalPayload];
        int writeOffset = 0;
        foreach (var frame in list)
        {
            int payloadLen = frame.Length - HeaderSize;
            if (payloadLen > 0)
            {
                Buffer.BlockCopy(frame, HeaderSize, result, writeOffset, payloadLen);
                writeOffset += payloadLen;
            }
        }

        return result;
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="frames"/> contains exactly the full set
    /// of frames for a complete sequence (all indices 0 .. frame_count-1 present, in order).
    /// </summary>
    /// <param name="frames">Accumulated frames so far.</param>
    public static bool IsComplete(List<byte[]> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);

        if (frames.Count == 0)
            return false;

        var first = frames[0];
        if (first.Length < HeaderSize)
            return false;

        int expectedCount = BinaryPrimitives.ReadUInt16LittleEndian(first.AsSpan(0, 2));

        if (frames.Count != expectedCount)
            return false;

        // Verify all indexes are present in order.
        for (int i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            if (frame.Length < HeaderSize)
                return false;

            int fc = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(0, 2));
            int fi = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(2, 2));

            if (fc != expectedCount || fi != i)
                return false;
        }

        return true;
    }
}

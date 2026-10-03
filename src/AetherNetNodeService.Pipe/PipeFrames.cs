// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Text;
using AetherNetNodeService.Ipc;

namespace AetherNetNodeService.Pipe;

/// <summary>One thing sent on the pipe: a call, an answer, or a push.</summary>
/// <param name="Kind">A call's or a push's <see cref="NodeOp"/>, or <see cref="PipeFrames.Answer"/>.</param>
/// <param name="Id">The call's number; an answer carries the number of the call it answers; a push carries 0.</param>
/// <param name="Body">The <see cref="NodeWire"/> bytes — the same bytes the Android binder carries.</param>
internal readonly record struct PipeFrame(byte Kind, int Id, byte[] Body);

/// <summary>
/// How calls, answers and pushes travel on the pipe. A frame is its length (4 bytes, little-endian, counting what
/// follows), then its kind (1 byte), its number (4 bytes) and its body. An answer's body is 1 then the result, or 0
/// then an <see cref="AetherNodeErrorCode"/> (4 bytes) and a message — so <c>NodeUnavailable</c> and
/// <c>IdentityAbsent</c> stay themselves across the pipe, as they do across the binder.
/// </summary>
internal static class PipeFrames
{
    /// <summary>The kind of an answer. Calls are 1 and up, pushes 100 and up (<see cref="NodeOp"/>).</summary>
    public const byte Answer = 0;

    /// <summary>The most one frame may carry. A message is far smaller; more than this is a broken peer.</summary>
    public const int MaxBody = 16 * 1024 * 1024;

    private const int HeadSize = 5;   // kind + number

    public static async Task WriteAsync(Stream stream, PipeFrame frame, CancellationToken cancellationToken)
    {
        if (frame.Body.Length > MaxBody)
        {
            throw new ArgumentException($"a frame may carry at most {MaxBody} bytes, not {frame.Body.Length}", nameof(frame));
        }

        var bytes = new byte[4 + HeadSize + frame.Body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, HeadSize + frame.Body.Length);
        bytes[4] = frame.Kind;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(5), frame.Id);
        frame.Body.CopyTo(bytes.AsSpan(4 + HeadSize));

        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The next frame, or null when the other end closed the pipe between frames.</summary>
    /// <exception cref="InvalidDataException">The other end sent something that is not a frame.</exception>
    /// <exception cref="EndOfStreamException">The other end closed the pipe part way through a frame.</exception>
    public static async Task<PipeFrame?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var length = new byte[4];
        var read = await stream.ReadAtLeastAsync(length, length.Length, throwOnEndOfStream: false, cancellationToken)
            .ConfigureAwait(false);
        if (read == 0)
        {
            return null;
        }

        if (read < length.Length)
        {
            throw new EndOfStreamException("the pipe closed part way through a frame");
        }

        var size = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (size < HeadSize || size > HeadSize + MaxBody)
        {
            throw new InvalidDataException($"not a frame: it says it is {size} bytes long");
        }

        var rest = new byte[size];
        await stream.ReadExactlyAsync(rest, cancellationToken).ConfigureAwait(false);
        return new PipeFrame(rest[0], BinaryPrimitives.ReadInt32LittleEndian(rest.AsSpan(1)), rest.AsSpan(HeadSize).ToArray());
    }

    public static byte[] Ok(byte[] result)
    {
        var body = new byte[1 + result.Length];
        body[0] = 1;
        result.CopyTo(body.AsSpan(1));
        return body;
    }

    public static byte[] Error(AetherNodeErrorCode code, string message)
    {
        var text = Encoding.UTF8.GetBytes(message ?? string.Empty);
        var body = new byte[1 + 4 + text.Length];
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(1), (int)code);
        text.CopyTo(body.AsSpan(5));
        return body;
    }

    /// <summary>The result an answer carries.</summary>
    /// <exception cref="AetherNodeException">The answer is an error, thrown with its own code.</exception>
    public static byte[] Result(byte[] answer)
    {
        if (answer.Length >= 1 && answer[0] == 1)
        {
            return answer.AsSpan(1).ToArray();
        }

        if (answer.Length >= 5)
        {
            var code = (AetherNodeErrorCode)BinaryPrimitives.ReadInt32LittleEndian(answer.AsSpan(1));
            throw new AetherNodeException(code, Encoding.UTF8.GetString(answer.AsSpan(5)));
        }

        throw new AetherNodeException(AetherNodeErrorCode.Internal, "AetherNetService sent an answer that says nothing");
    }

    /// <summary>A whole number as a call's argument (the inbox's limit).</summary>
    public static byte[] Number(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    public static int Number(byte[] bytes, int otherwise)
        => bytes.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(bytes) : otherwise;
}

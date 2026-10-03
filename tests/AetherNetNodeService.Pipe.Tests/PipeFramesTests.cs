// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using Xunit;

namespace AetherNetNodeService.Pipe.Tests;

/// <summary>
/// What crosses the pipe, byte for byte: a frame comes back as itself, a pipe closed between frames is the end, and
/// anything that is not a frame is refused rather than read as one.
/// </summary>
public class PipeFramesTests
{
    [Fact]
    public async Task A_frame_comes_back_as_itself()
    {
        using var stream = new MemoryStream();
        await PipeFrames.WriteAsync(stream, new PipeFrame(4, 77, [1, 2, 3]), CancellationToken.None);
        stream.Position = 0;

        var frame = await PipeFrames.ReadAsync(stream, CancellationToken.None);

        Assert.NotNull(frame);
        Assert.Equal(4, frame.Value.Kind);
        Assert.Equal(77, frame.Value.Id);
        Assert.Equal(new byte[] { 1, 2, 3 }, frame.Value.Body);
        Assert.Null(await PipeFrames.ReadAsync(stream, CancellationToken.None));   // closed between frames: the end
    }

    [Fact]
    public async Task A_length_that_is_not_a_frame_is_refused()
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, PipeFrames.MaxBody + 100);
        using var stream = new MemoryStream(bytes);

        await Assert.ThrowsAsync<InvalidDataException>(() => PipeFrames.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task A_pipe_closed_part_way_through_a_frame_is_not_a_frame()
    {
        using var whole = new MemoryStream();
        await PipeFrames.WriteAsync(whole, new PipeFrame(1, 1, [9, 9, 9, 9]), CancellationToken.None);
        using var cut = new MemoryStream(whole.ToArray()[..7]);

        await Assert.ThrowsAsync<EndOfStreamException>(() => PipeFrames.ReadAsync(cut, CancellationToken.None));
    }

    [Fact]
    public void An_answer_keeps_its_error_code_and_message()
    {
        var ex = Assert.Throws<AetherNodeException>(() =>
            PipeFrames.Result(PipeFrames.Error(AetherNodeErrorCode.NodeLocked, "locked for now")));

        Assert.Equal(AetherNodeErrorCode.NodeLocked, ex.Code);
        Assert.Equal("locked for now", ex.Message);
        Assert.Equal(new byte[] { 5 }, PipeFrames.Result(PipeFrames.Ok([5])));
    }
}

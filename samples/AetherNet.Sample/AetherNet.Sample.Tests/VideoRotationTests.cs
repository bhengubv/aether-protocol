// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Services;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// Which way up a video call is.
///
/// <para>
/// Nothing in the video path read the sensor's mounting angle at all — no reference to
/// <c>SENSOR_ORIENTATION</c>, no rotation set on the encoder, no transform on any surface — so a
/// portrait-held phone encoded a landscape frame and both ends drew a picture on its side. These pin
/// the arithmetic that replaces the omission, because it is four lines that are wrong in a way nobody
/// notices until two particular handsets are held two particular ways up.
/// </para>
/// </summary>
public class VideoRotationTests
{
    // ── the ordinary case ──────────────────────────────────────────────────

    /// <summary>
    /// A phone held upright with the usual 90°-mounted sensor. Both lenses need a quarter turn, and
    /// this is the case that covers almost every call anyone will ever place.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_upright_phone_with_the_usual_sensor_needs_a_quarter_turn(bool front)
        => Assert.Equal(90, VideoRotation.ForCapture(sensorDegrees: 90, displayDegrees: 0, front));

    /// <summary>
    /// The sensor mounted the other way round — some phones do — and the answer follows it rather
    /// than a constant.
    /// </summary>
    [Fact]
    public void The_answer_follows_the_sensor_not_a_guess()
        => Assert.Equal(270, VideoRotation.ForCapture(sensorDegrees: 270, displayDegrees: 0, front: true));

    // ── the mirror ─────────────────────────────────────────────────────────

    /// <summary>
    /// The selfie camera is mirrored, so its correction runs the other way. This is the one that is
    /// easy to get backwards, and getting it backwards is the difference between a picture that is
    /// upside down and one that is merely sideways.
    /// </summary>
    [Fact]
    public void The_front_camera_turns_the_other_way_on_a_rotated_screen()
    {
        var front = VideoRotation.ForCapture(sensorDegrees: 90, displayDegrees: 90, front: true);
        var back = VideoRotation.ForCapture(sensorDegrees: 90, displayDegrees: 90, front: false);

        Assert.Equal(180, front);
        Assert.Equal(0, back);
        Assert.NotEqual(front, back);
    }

    // ── the sign trap ──────────────────────────────────────────────────────

    /// <summary>
    /// A back camera on a screen turned further than the sensor is mounted produces a negative angle
    /// from the raw subtraction.
    /// </summary>
    /// <remarks>
    /// C#'s <c>%</c> keeps the sign of its left operand, so <c>(90 - 180) % 360</c> is -90, not 270.
    /// A rotation matrix accepts -90 perfectly happily and turns the picture the wrong way, so this
    /// fails as a wrong picture rather than as an error.
    /// </remarks>
    [Fact]
    public void A_negative_angle_never_escapes()
    {
        Assert.Equal(270, VideoRotation.ForCapture(sensorDegrees: 90, displayDegrees: 180, front: false));
        Assert.Equal(180, VideoRotation.ForCapture(sensorDegrees: 90, displayDegrees: 270, front: false));
    }

    [Theory]
    [InlineData(-90, 270)]
    [InlineData(-360, 0)]
    [InlineData(450, 90)]
    [InlineData(720, 0)]
    public void Any_angle_folds_onto_a_quarter_turn(int given, int expected)
        => Assert.Equal(expected, VideoRotation.Normalise(given));

    /// <summary>A phone reporting something that is not a right angle gets the nearest one.</summary>
    [Theory]
    [InlineData(89, 90)]
    [InlineData(46, 90)]
    [InlineData(44, 0)]
    public void An_odd_angle_is_rounded_rather_than_trusted(int given, int expected)
        => Assert.Equal(expected, VideoRotation.Normalise(given));

    // ── the wire ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void An_angle_survives_the_round_trip(int degrees)
        => Assert.Equal(degrees, VideoRotation.FromWire(VideoRotation.ToWire(degrees)));

    /// <summary>One byte, holding a quarter-turn count — it rides the real-time lane beside the voice.</summary>
    [Fact]
    public void The_wire_form_is_a_quarter_turn_count()
    {
        Assert.Equal(0, VideoRotation.ToWire(0));
        Assert.Equal(1, VideoRotation.ToWire(90));
        Assert.Equal(3, VideoRotation.ToWire(270));
    }

    /// <summary>
    /// A phone on the older build sends the camera-state byte and no angle at all. It must read as
    /// upright rather than as anything else — that is what was being drawn before, so nothing gets
    /// worse for the pair who have not both updated.
    /// </summary>
    [Fact]
    public void A_phone_that_sends_no_angle_reads_as_upright()
    {
        byte[] oldBuild = [1];                       // camera on, nothing more
        var rotation = oldBuild.Length > 1 ? VideoRotation.FromWire(oldBuild[1]) : 0;

        Assert.Equal(0, rotation);
    }

    /// <summary>And a byte outside the four quarter turns cannot produce a nonsense angle.</summary>
    [Theory]
    [InlineData((byte)4)]
    [InlineData((byte)255)]
    public void A_wire_byte_out_of_range_still_folds_onto_a_quarter_turn(byte wire)
        => Assert.Contains(VideoRotation.FromWire(wire), new[] { 0, 90, 180, 270 });
}

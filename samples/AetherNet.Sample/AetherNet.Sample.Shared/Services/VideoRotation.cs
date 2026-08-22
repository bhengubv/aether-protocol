// SPDX-License-Identifier: MIT

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// Which way up a camera's pictures come out.
///
/// <para>
/// A phone's camera sensor is fixed to the board at an angle — 90° on most, 270° on some — and it
/// delivers every frame in that orientation no matter how the phone is being held. Nothing in this
/// app ever asked. The encoder was configured 640x480 landscape, the sensor handed it a landscape
/// frame, and both ends drew a picture lying on its side.
/// </para>
///
/// <para>
/// The arithmetic lives here rather than beside the camera so it can be checked without a phone. It
/// is four lines and it is wrong in a way nobody notices until two specific handsets are held two
/// specific ways up, which is exactly the kind of thing that should be pinned by a test.
/// </para>
/// </summary>
public static class VideoRotation
{
    /// <summary>
    /// How far the far end must turn this phone's picture to draw it the way up it was taken.
    /// </summary>
    /// <param name="sensorDegrees">
    ///   The sensor's mounting angle, from <c>CameraCharacteristics.SENSOR_ORIENTATION</c>.
    /// </param>
    /// <param name="displayDegrees">How far the screen itself is rotated: 0, 90, 180 or 270.</param>
    /// <param name="front">
    ///   Whether this is the selfie camera. Its image is mirrored, so its correction runs the other
    ///   way round — getting this backwards is the difference between upside down and merely sideways.
    /// </param>
    public static int ForCapture(int sensorDegrees, int displayDegrees, bool front)
    {
        var sensor = Normalise(sensorDegrees);
        var display = Normalise(displayDegrees);

        return front
            ? Normalise(sensor + display)
            : Normalise(sensor - display);
    }

    /// <summary>Fold any angle onto 0, 90, 180 or 270.</summary>
    /// <remarks>
    /// Negatives included. C#'s <c>%</c> keeps the sign of the left operand, so a naive
    /// <c>(a - b) % 360</c> yields -90 for a back camera on a landscape screen — which a rotation
    /// matrix will happily apply, turning the picture the wrong way rather than failing.
    /// </remarks>
    public static int Normalise(int degrees)
    {
        var d = degrees % 360;
        if (d < 0) d += 360;

        // Cameras report right angles. Anything else is a phone being creative, and rounding to the
        // nearest quarter turn is closer to right than trusting it.
        return ((d + 45) / 90 % 4) * 90;
    }

    /// <summary>The angle packed into a single byte for the wire, and back again.</summary>
    /// <remarks>
    /// One byte holding a quarter-turn count rather than two holding degrees. The camera-state packet
    /// is sent on every camera toggle inside a live call, and it rides the real-time lane beside the
    /// voice — there is no reason for it to be larger than it has to be.
    /// </remarks>
    public static byte ToWire(int degrees) => (byte)(Normalise(degrees) / 90);

    /// <inheritdoc cref="ToWire"/>
    public static int FromWire(byte quarters) => Normalise(quarters * 90);
}

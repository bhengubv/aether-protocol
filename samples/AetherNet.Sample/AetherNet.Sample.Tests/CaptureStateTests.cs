// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Services;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// One answer to "is video on".
///
/// <para>
/// There were nine. Sending could be read from <c>VideoOn</c> on the 1:1 call, <c>CameraOn</c> on the
/// group call, and three separate flags inside the camera object; receiving from <c>TheirVideoOn</c>,
/// the group's <c>OnCamera</c> set, a per-peer <c>Ready</c> flag, and whether an overlay existed. Nine
/// answers across two services and one device, with nothing tying any of them together — and every
/// fault in this area has been two of them disagreeing.
/// </para>
/// </summary>
public class CaptureStateTests
{
    /// <summary>
    /// "On" is one state and not a family of them. It was written as "running" in one place and "the
    /// encoder exists" in another, and those are not the same during a start or a stop.
    /// </summary>
    [Theory]
    [InlineData(CaptureState.Idle, false)]
    [InlineData(CaptureState.Starting, false)]
    [InlineData(CaptureState.Capturing, true)]
    [InlineData(CaptureState.Stopping, false)]
    public void Only_capturing_counts_as_on(CaptureState state, bool on)
        => Assert.Equal(on, CaptureStates.IsOn(state));

    /// <summary>
    /// A camera starts from a standing stop, never from a start already in flight and never from a
    /// stop still closing one. The old test was "not running", which is true of both of those.
    /// </summary>
    [Theory]
    [InlineData(CaptureState.Idle, true)]
    [InlineData(CaptureState.Starting, false)]
    [InlineData(CaptureState.Capturing, false)]
    [InlineData(CaptureState.Stopping, false)]
    public void A_camera_starts_only_from_a_standing_stop(CaptureState state, bool canStart)
        => Assert.Equal(canStart, CaptureStates.CanStart(state));

    /// <summary>
    /// openCamera answers on a callback, and by then the call may be over. A camera arriving outside
    /// a start in progress is closed rather than kept — otherwise it runs with nothing holding it.
    /// </summary>
    [Theory]
    [InlineData(CaptureState.Idle, false)]
    [InlineData(CaptureState.Starting, true)]
    [InlineData(CaptureState.Capturing, false)]
    [InlineData(CaptureState.Stopping, false)]
    public void A_camera_is_only_adopted_by_a_start_that_is_still_waiting(CaptureState state, bool adopt)
        => Assert.Equal(adopt, CaptureStates.ShouldAdopt(state));

    // ── the rule the whole rework exists for ───────────────────────────────

    /// <summary>
    /// Intent may not outlive the device.
    /// </summary>
    /// <remarks>
    /// A camera stops by itself for ordinary reasons — a link too tight to carry a picture, a failed
    /// encoder, the hardware taken by another app. Every one of those left the button reading
    /// "Camera on", and left the far end watching a frozen last frame for the rest of the call,
    /// because the only thing that would have corrected it was a camera-off message nobody sent.
    /// </remarks>
    [Theory]
    [InlineData(CaptureState.Idle)]
    [InlineData(CaptureState.Stopping)]
    public void A_service_that_thinks_it_is_sending_must_be_corrected(CaptureState state)
        => Assert.True(CaptureStates.MustGiveUp(state, intended: true));

    /// <summary>
    /// Starting counts too. A camera that is still opening is not one whose pictures anybody should
    /// be promised — announcing "my camera is on" from here is exactly what left a phone holding a
    /// black tile for a capture session that went on to fail.
    /// </summary>
    [Fact]
    public void A_camera_that_is_still_opening_is_not_one_to_promise()
        => Assert.True(CaptureStates.MustGiveUp(CaptureState.Starting, intended: true));

    [Fact]
    public void A_camera_that_is_genuinely_running_needs_no_correction()
        => Assert.False(CaptureStates.MustGiveUp(CaptureState.Capturing, intended: true));

    /// <summary>
    /// And nothing is corrected for a service that never claimed to be sending — otherwise every
    /// ordinary stop would announce a camera-off that the stop itself is already announcing.
    /// </summary>
    [Theory]
    [InlineData(CaptureState.Idle)]
    [InlineData(CaptureState.Starting)]
    [InlineData(CaptureState.Capturing)]
    [InlineData(CaptureState.Stopping)]
    public void Nothing_is_corrected_for_a_service_that_never_claimed_to_be_sending(CaptureState state)
        => Assert.False(CaptureStates.MustGiveUp(state, intended: false));

    /// <summary>
    /// Stated as the invariant rather than as cases: if the device is not capturing, no one may go on
    /// believing it is. Every state is covered, so a state added later fails here rather than in a call.
    /// </summary>
    [Fact]
    public void Across_every_state_intent_never_outlives_the_device()
    {
        foreach (var state in Enum.GetValues<CaptureState>())
            Assert.Equal(!CaptureStates.IsOn(state), CaptureStates.MustGiveUp(state, intended: true));
    }
}

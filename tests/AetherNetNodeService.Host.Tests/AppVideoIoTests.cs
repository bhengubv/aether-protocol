// SPDX-License-Identifier: MIT

using Xunit;

namespace AetherNetNodeService.Host.Tests;

/// <summary>
/// The app's camera as a call in the service sees it: a start is asked of the app and its answer is the answer; with
/// no app to ask it is a no rather than a wait; what the app reports and encodes reaches the call; and only the call
/// holding the camera can put it down.
/// </summary>
public class AppVideoIoTests
{
    [Fact]
    public async Task A_start_is_what_the_app_answers()
    {
        var camera = new AppVideoIo();
        camera.StartAsked += id => camera.Answer(id, true);

        Assert.True(await camera.StartAsync());
    }

    [Fact]
    public async Task A_camera_the_app_could_not_open_is_a_no()
    {
        var camera = new AppVideoIo();
        camera.StartAsked += id => camera.Answer(id, false);

        Assert.False(await camera.StartAsync());
    }

    [Fact]
    public async Task With_no_app_to_ask_a_start_is_a_no_at_once()
    {
        var camera = new AppVideoIo();

        Assert.False(await camera.StartAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task An_answer_for_another_ask_does_not_answer_this_one()
    {
        var camera = new AppVideoIo();
        var asked = new TaskCompletionSource<int>();
        camera.PermissionAsked += id => asked.TrySetResult(id);

        var permission = camera.EnsurePermissionAsync();
        var id = await asked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        camera.Answer(id + 1000, true);
        Assert.False(permission.IsCompleted);

        camera.Answer(id, true);
        Assert.True(await permission.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void What_the_app_reports_is_what_the_call_sees_and_a_change_is_said_once()
    {
        var camera = new AppVideoIo();
        var changes = new List<CaptureState>();
        camera.CaptureChanged += changes.Add;

        camera.Report(true, null, (int)CaptureState.Capturing, 4, 400_000, 90, 1280, 720);
        camera.Report(true, null, (int)CaptureState.Capturing, 4, 400_000, 90, 1280, 720);

        Assert.Equal([CaptureState.Capturing], changes);
        Assert.True(camera.IsPresent);
        Assert.Null(camera.UnavailableReason);
        Assert.True(camera.IsRunning);
        Assert.Equal(4, camera.MaxConcurrentStreams);
        Assert.Equal(90, camera.CaptureRotation);
    }

    [Fact]
    public void Before_the_app_says_anything_there_is_no_camera_and_it_says_why()
    {
        var camera = new AppVideoIo();

        Assert.False(camera.IsPresent);
        Assert.False(string.IsNullOrEmpty(camera.UnavailableReason));
    }

    [Fact]
    public void A_frame_from_the_app_reaches_the_call()
    {
        var camera = new AppVideoIo();
        byte[]? sent = null;
        camera.FrameEncoded += frame => sent = frame;

        camera.Frame([1, 2, 3]);

        Assert.Equal(new byte[] { 1, 2, 3 }, sent);
    }

    [Fact]
    public async Task Only_the_call_holding_the_camera_can_put_it_down()
    {
        var camera = new AppVideoIo();
        var stops = 0;
        camera.StopAsked += () => stops++;
        object oneToOne = new(), group = new();

        Assert.True(camera.Claim(group));
        Assert.False(camera.Claim(oneToOne));

        await camera.ReleaseAsync(oneToOne);
        Assert.Equal(0, stops);

        await camera.ReleaseAsync(group);
        Assert.Equal(1, stops);
    }
}

// SPDX-License-Identifier: MIT

using Xunit;

namespace AetherNet.Aware.Tests;

public class HelpTriggerTests
{
    [Fact]
    public void TheScreenButtonNotificationPowerButtonAndShakeAreOnToStart()
    {
        var settings = new HelpTriggerSettings();
        Assert.True(settings.On(HelpTrigger.AppButton));
        Assert.True(settings.On(HelpTrigger.Notification));
        Assert.True(settings.On(HelpTrigger.PowerButton));
        Assert.True(settings.On(HelpTrigger.Shake));
        Assert.False(settings.On(HelpTrigger.DuressPin));
        Assert.False(settings.On(HelpTrigger.None));
        Assert.Equal(0, settings.HoldSeconds);
    }

    [Fact]
    public void ThePersonCanTurnThemOffAndOn()
    {
        var quietPocket = new HelpTriggerSettings { Enabled = HelpTrigger.AppButton | HelpTrigger.DuressPin };
        Assert.True(quietPocket.On(HelpTrigger.AppButton));
        Assert.True(quietPocket.On(HelpTrigger.DuressPin));
        Assert.False(quietPocket.On(HelpTrigger.Shake));
        Assert.False(new HelpTriggerSettings { Enabled = HelpTrigger.None }.On(HelpTrigger.AppButton));
    }

    [Fact]
    public void FivePressesInThreeSecondsAsksForHelp()
    {
        var power = new PowerButtonWatch();
        Assert.False(power.Press(1_000));
        Assert.False(power.Press(1_300));
        Assert.False(power.Press(1_600));
        Assert.False(power.Press(1_900));
        Assert.True(power.Press(2_200));
    }

    [Fact]
    public void PressesTooFewOrTooSlowDoNot()
    {
        var power = new PowerButtonWatch();
        for (var i = 0; i < 4; i++)
        {
            Assert.False(power.Press(1_000 + (i * 200)));
        }

        var slow = new PowerButtonWatch();
        for (var i = 0; i < 8; i++)
        {
            Assert.False(slow.Press(i * 1_000L));   // one a second: never five inside three
        }
    }

    [Fact]
    public void ItCountsAgainFromScratchAfterwards()
    {
        var power = new PowerButtonWatch();
        for (var i = 0; i < 4; i++)
        {
            power.Press(1_000 + (i * 200));
        }
        Assert.True(power.Press(1_800));
        Assert.False(power.Press(1_900));
        Assert.False(power.Press(2_000));

        power.Reset();
        for (var i = 0; i < 4; i++)
        {
            Assert.False(power.Press(5_000 + (i * 200)));
        }
        Assert.True(power.Press(5_800));
    }

    [Fact]
    public void ThreeHardShakesAskForHelp()
    {
        var shake = new ShakeWatch();
        Assert.False(shake.Reading(0, 0, 30, 0));
        Assert.False(shake.Reading(0, 0, 30, 200));
        Assert.True(shake.Reading(0, 0, 30, 400));
    }

    [Fact]
    public void BeingCarriedDoesNot()
    {
        var shake = new ShakeWatch();
        for (var i = 0; i < 50; i++)
        {
            Assert.False(shake.Reading(0, 0, 9.8 + (i % 3), i * 100L));
        }
    }

    [Fact]
    public void OneShakeIsOneShakeNotFiftyReadings()
    {
        var shake = new ShakeWatch();
        // A single hard move, sampled every 20 ms: inside the gap, so it counts once.
        for (var at = 0; at <= 120; at += 20)
        {
            Assert.False(shake.Reading(0, 0, 40, at));
        }
    }

    [Fact]
    public void ShakesTooSlowToBeOneGestureDoNot()
    {
        var shake = new ShakeWatch();
        for (var i = 0; i < 6; i++)
        {
            Assert.False(shake.Reading(0, 0, 40, i * 1_000L));
        }
    }

    [Fact]
    public void TheWatchesTakeThePersonsOwnNumbers()
    {
        var settings = new HelpTriggerSettings
        {
            PowerPresses = 3,
            PowerWindowMs = 2_000,
            ShakeThreshold = 15.0,
            ShakeCount = 2,
            ShakeWindowMs = 1_000,
        };
        var power = settings.NewPowerButtonWatch();
        Assert.False(power.Press(0));
        Assert.False(power.Press(300));
        Assert.True(power.Press(600));

        var shake = settings.NewShakeWatch();
        Assert.False(shake.Reading(0, 0, 16, 0));
        Assert.True(shake.Reading(0, 0, 16, 300));
    }

    [Fact]
    public void SettingsThatMakeNoSenseAreRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PowerButtonWatch(presses: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PowerButtonWatch(presses: 5, windowMs: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ShakeWatch(threshold: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ShakeWatch(count: 1));
    }
}

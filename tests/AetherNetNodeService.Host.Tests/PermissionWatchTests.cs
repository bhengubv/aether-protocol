// SPDX-License-Identifier: MIT

using Xunit;

namespace AetherNetNodeService.Host.Tests;

/// <summary>
/// Noticing a permission the person allowed on the phone's own settings page — the phone does not say, so the
/// service looks again while anything is missing.
/// </summary>
public class PermissionWatchTests
{
    private const string For = "find phones near you";

    private static ServicePermission Nearby(bool allowed) => new("Nearby devices", allowed, For);

    private static ServicePermission Notifications(bool allowed) => new("Notifications", allowed, "show that it is keeping you reachable");

    [Fact]
    public void Looking_again_with_nothing_changed_says_so()
    {
        var watch = new PermissionWatch(() => [Nearby(false)]);

        Assert.False(watch.Look());
        Assert.False(watch.NewlyAllowed);
    }

    [Fact]
    public void A_permission_allowed_since_the_last_look_is_noticed()
    {
        var allowed = false;
        var watch = new PermissionWatch(() => [Nearby(allowed), Notifications(false)]);

        allowed = true;

        Assert.True(watch.Look());
        Assert.True(watch.NewlyAllowed);
        Assert.True(watch.Current[0].Allowed);

        // Noticed once: the next look finds nothing new.
        Assert.False(watch.Look());
        Assert.False(watch.NewlyAllowed);
    }

    /// <summary>Taken back is a change to report, but nothing to bring up.</summary>
    [Fact]
    public void A_permission_taken_back_is_a_change_but_not_an_allowing()
    {
        var allowed = true;
        var watch = new PermissionWatch(() => [Nearby(allowed)]);

        allowed = false;

        Assert.True(watch.Look());
        Assert.False(watch.NewlyAllowed);
    }

    [Fact]
    public void It_waits_only_while_something_is_not_allowed()
    {
        var allowed = false;
        var watch = new PermissionWatch(() => [Nearby(true), Notifications(allowed)]);
        Assert.True(watch.Waiting);

        allowed = true;
        watch.Look();

        Assert.False(watch.Waiting);
    }

    /// <summary>One the phone does not report can never be seen to change, so it is no reason to keep looking.</summary>
    [Fact]
    public void A_permission_the_phone_does_not_report_is_no_reason_to_wait()
    {
        var watch = new PermissionWatch(() =>
        [
            Nearby(true),
            new ServicePermission("App launch", false, "start again after the phone stops it") { Page = PermissionPage.AppLaunch, Known = false },
        ]);

        Assert.False(watch.Waiting);
    }

    [Fact]
    public void A_host_with_no_permissions_never_waits()
    {
        var watch = new PermissionWatch(() => []);

        Assert.False(watch.Waiting);
        Assert.False(watch.Look());
    }
}

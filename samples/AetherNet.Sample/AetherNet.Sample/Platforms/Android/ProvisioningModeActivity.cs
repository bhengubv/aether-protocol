// SPDX-License-Identifier: MIT
#if ANDROID
using AetherNet.Sample.Shared.Services;
using Android.App;
using Android.Content;
using Android.OS;

namespace AetherNet.Sample.Platforms.Android;

/// <summary>
/// The question provisioning asks, and the smallest possible answer to it.
///
/// <para>
/// Halfway through installing this app by itself, Android stops and asks it one thing: <i>here are the
/// kinds of authority I will give you — pick one.</i> Answering off the list fails the install.
/// Not answering fails it too: an app that ignores this is downloaded onto somebody's phone and then
/// abandoned mid-flow, which is the worst of both outcomes.
/// </para>
///
/// <para>
/// <b>That question is the whole reason this path is acceptable.</b> Read from the outside,
/// provisioning looks like it means becoming the owner of a stranger's phone. It does not — the mode
/// is not fixed by whoever sent the tap, it is chosen here. So we take a work profile, which owns a
/// badged space and cannot see past it, and if a work profile is not on offer we let the install fail
/// rather than accept the handset.
/// </para>
///
/// <para>
/// The choosing itself lives in <see cref="ProvisioningChoice"/>, on the platform-neutral side, where
/// it can be tested without a phone. This class is the wire: read the list, hand it over, return the
/// answer.
/// </para>
/// </summary>
[global::Android.App.Activity(
    Name = "com.bhengubv.aethernet.ProvisioningMode",
    Exported = true,
    NoHistory = true,
    Theme = "@android:style/Theme.NoDisplay")]
[global::Android.App.IntentFilter(["android.app.action.GET_PROVISIONING_MODE"],
    Categories = ["android.intent.category.DEFAULT"])]
public sealed class ProvisioningModeActivity : Activity
{
    /// <summary>What the system hands us: the modes it is willing to grant.</summary>
    private const string AllowedModes = "android.app.extra.PROVISIONING_ALLOWED_PROVISIONING_MODES";

    /// <summary>What we hand back.</summary>
    private const string ChosenMode = "android.app.extra.PROVISIONING_MODE";

    /// <summary>Who to make the admin, named explicitly rather than guessed at.</summary>
    private const string AdminComponent = "android.app.extra.PROVISIONING_DEVICE_ADMIN_COMPONENT_NAME";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        var offered = Intent?.GetIntegerArrayListExtra(AllowedModes) is { } list
            ? list.Select(x => (int)x!).ToArray()
            : [];

        global::Android.Util.Log.Info("AetherProv",
            $"asked which authority to take — offered [{string.Join(", ", offered)}]");

        var choice = ProvisioningChoice.Least(offered);

        if (choice == ProvisioningChoice.Refuse)
        {
            // Walking away costs a failed install. Accepting costs somebody their phone, quietly, and
            // they find out by reading a settings screen weeks later. The first is the cheaper mistake.
            global::Android.Util.Log.Info("AetherProv",
                "⛔ refusing — " + ProvisioningChoice.Refusal(offered));

            SetResult(Result.Canceled);
            Finish();
            return;
        }

        var answer = new Intent();
        answer.PutExtra(ChosenMode, choice);
        answer.PutExtra(AdminComponent,
            new ComponentName(PackageName!, "com.bhengubv.aethernet.AetherDeviceAdmin"));

        global::Android.Util.Log.Info("AetherProv",
            choice == ProvisioningChoice.OwnProfileOnly
                ? "taking a work profile only — the rest of this phone stays theirs"
                : $"taking mode {choice}");

        SetResult(Result.Ok, answer);
        Finish();
    }
}

/// <summary>
/// The second question: are you happy with how you were set up?
///
/// <para>
/// Provisioning will not finish without an answer to this either. A managed app is meant to use the
/// moment to enforce whatever its organisation requires — a passcode, encryption, a blocked camera.
/// We require nothing of anybody's phone, so this says yes immediately and gets out of the way.
/// </para>
/// </summary>
[global::Android.App.Activity(
    Name = "com.bhengubv.aethernet.PolicyCompliance",
    Exported = true,
    NoHistory = true,
    Theme = "@android:style/Theme.NoDisplay")]
[global::Android.App.IntentFilter(["android.app.action.ADMIN_POLICY_COMPLIANCE"],
    Categories = ["android.intent.category.DEFAULT"])]
public sealed class PolicyComplianceActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        global::Android.Util.Log.Info("AetherProv", "nothing is required of this phone — done");

        SetResult(Result.Ok);
        Finish();
    }
}
#endif

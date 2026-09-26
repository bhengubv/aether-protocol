// SPDX-License-Identifier: MIT
#if ANDROID
using Android.App;
using Android.OS;
using Android.Views;
using Android.Widget;
using AetherNet.Node;

namespace AetherNet.NodeApp;

/// <summary>
/// The node's one screen: it shows the device's AetherTag (proof the identity lives here, minted once) and
/// the apps that have asked to link. Linking is the user's decision — an app only becomes <c>Bound</c> when
/// the person taps Allow here. This is the "allow this app" gate; the app can never grant itself.
/// </summary>
[Activity(Label = "Aether Node", MainLauncher = true, Exported = true, Name = "com.bhengubv.aethernode.MainActivity")]
public sealed class MainActivity : Activity
{
    private LinearLayout _root = null!;
    private TextView _tag = null!;

    protected override async void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Approving a link is normally the user tapping Allow below. For headless testing the same decision
        // can be driven in with an intent extra:
        //   adb shell am start -n com.bhengubv.aethernode/com.bhengubv.aethernode.MainActivity --es approve <package>
        var approve = Intent?.GetStringExtra("approve");
        if (!string.IsNullOrEmpty(approve))
        {
            var existing = MainApplication.Grants.Get(approve);
            MainApplication.Grants.Save(existing.State == GrantState.Bound
                ? existing
                : existing.Granted(System.DateTimeOffset.UtcNow));
        }

        _root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _root.SetPadding(48, 64, 48, 48);

        var title = new TextView(this) { Text = "Aether Node" };
        title.SetTextSize(Android.Util.ComplexUnitType.Sp, 24f);
        _root.AddView(title);

        _tag = new TextView(this) { Text = "Identity: minting…" };
        _tag.SetTextSize(Android.Util.ComplexUnitType.Sp, 16f);
        _tag.SetPadding(0, 24, 0, 24);
        _root.AddView(_tag);

        var refresh = new Button(this) { Text = "Refresh link requests" };
        refresh.Click += (_, _) => RenderGrants();
        _root.AddView(refresh);

        var scroll = new ScrollView(this);
        scroll.AddView(_root);
        SetContentView(scroll);

        try
        {
            var tag = await MainApplication.Node!.GetTagAsync();
            _tag.Text = $"Identity: {tag.Value}";
        }
        catch (System.Exception ex)
        {
            _tag.Text = "Identity error: " + ex.Message;
        }

        RenderGrants();
    }

    private void RenderGrants()
    {
        // Drop any previously-rendered grant rows (everything after the fixed header views).
        while (_root.ChildCount > 3)
        {
            _root.RemoveViewAt(3);
        }

        var grants = MainApplication.Grants.All();
        if (grants.Count == 0)
        {
            _root.AddView(new TextView(this) { Text = "No apps have asked to link yet." });
            return;
        }

        foreach (var grant in grants)
        {
            var row = new TextView(this) { Text = $"{grant.AppId} — {grant.State}" };
            row.SetPadding(0, 24, 0, 8);
            _root.AddView(row);

            if (grant.State == GrantState.AwaitingGrant)
            {
                var allow = new Button(this) { Text = $"Allow {grant.AppId}" };
                var captured = grant;
                allow.Click += (_, _) =>
                {
                    MainApplication.Grants.Save(captured.Granted(System.DateTimeOffset.UtcNow));
                    RenderGrants();
                };
                _root.AddView(allow);
            }
        }
    }
}
#endif

using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AetherNet.Sample.Shared.Services;

namespace AetherNet.Sample;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTask, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
// Scanning an Aether invite with the phone's own camera app opens Aether directly — which is why we
// need no camera permission and no scanning SDK of our own.
[IntentFilter(
    new[] { Intent.ActionView },
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataScheme = "aether")]
public class MainActivity : MauiAppCompatActivity
{
    /// <summary>An <c>aether://…</c> link the OS handed us before the UI was listening.</summary>
    public static string? PendingLink { get; private set; }

    /// <summary>Raised when a link arrives while the app is already running.</summary>
    public static event Action<string>? LinkReceived;

    /// <summary>Take (and clear) any link that arrived before a page was ready to handle it.</summary>
    public static string? ConsumePendingLink()
    {
        var link = PendingLink;
        PendingLink = null;
        return link;
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Capture(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        Capture(intent);
    }

    private static void Capture(Intent? intent)
    {
        var data = intent?.DataString;
        if (string.IsNullOrWhiteSpace(data)) return;
        if (!data.StartsWith("aether://", StringComparison.OrdinalIgnoreCase)) return;

        // Only accept links that actually carry a usable tag; anything else is ignored rather than
        // pushed at the user.
        if (!ContactService.TryParseInvite(data, out _, out _)) return;

        PendingLink = data;
        LinkReceived?.Invoke(data);
    }
}

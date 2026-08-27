using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using AetherNet.Sample.Shared.Services;

namespace AetherNet.Sample;

// AdjustResize, not the default pan: when the keyboard opens the page must get shorter, not slide
// upwards. Panning pushes the conversation header off the top of the screen, so you lose sight of
// who you are talking to and whether the chat is encrypted at exactly the moment you are typing.
[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTask, WindowSoftInputMode = SoftInput.AdjustResize, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
// Scanning an Aether invite with the phone's own camera app opens Aether directly — which is why we
// need no camera permission and no scanning SDK of our own.
[IntentFilter(
    new[] { Intent.ActionView },
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataScheme = "aether")]
// Touching a phone that already has Aether opens Aether, rather than whichever app registered the
// broadest NFC filter. Our tag carries an external record — bhengubv.com:aethertag — and Android
// turns an external record into exactly this URI, so claiming it claims our own taps and nobody
// else's. Measured on a P30: without this, a tap went to WeChat.
[IntentFilter(
    new[] { "android.nfc.action.NDEF_DISCOVERED" },
    Categories = new[] { Intent.CategoryDefault },
    DataScheme = "vnd.android.nfc",
    DataHost = "ext",
    DataPathPrefix = "/bhengubv.com:aethertag")]
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
        KeepContentAboveTheKeyboard();
        Capture(Intent);
    }

    /// <summary>
    /// Hand a chosen file back to the page that asked for it.
    /// </summary>
    /// <remarks>
    /// The activity is the only thing that receives an activity result, and the WebView's file
    /// chooser is started from a chrome client that is not one. Everything else still goes to MAUI —
    /// permissions, its own pickers — so base is called either way.
    /// </remarks>
    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);

        if (requestCode == Platforms.Android.WebViewFilePicker.RequestCode)
            Platforms.Android.WebViewFilePicker.Answer(resultCode, data);
    }

    /// <summary>
    /// Make the keyboard shorten the app instead of shoving it upwards.
    /// <para>
    /// MAUI draws edge-to-edge, and while it does, Android ignores <c>adjustResize</c> and simply
    /// pans the whole web view up behind the status bar — which silently takes the conversation
    /// header with it, so you cannot see who you are talking to while you type. The page itself
    /// cannot detect this: nothing inside the web view changes, not even the visual viewport.
    /// </para>
    /// <para>
    /// So we listen for the keyboard inset ourselves and pad the content by exactly its height. The
    /// window stays edge-to-edge, and the app keeps every pixel it can actually use.
    /// </para>
    /// </summary>
    private void KeepContentAboveTheKeyboard()
    {
        if (Window is null) return;

        // Measured on the P30: while the app draws edge-to-edge the window keeps its full height when
        // the keyboard opens — it neither resizes nor moves — so the keyboard is painted straight over
        // the app and the web view scrolls its own content up to reveal the focused box, carrying the
        // conversation header off the top. Nothing inside the page can even observe that.
        //
        // Letting the decor fit the system windows again makes adjustResize do its job: the window
        // genuinely gets shorter, so the header stays put and the composer sits above the keys.
        AndroidX.Core.View.WindowCompat.SetDecorFitsSystemWindows(Window, true);
        Window.SetSoftInputMode(SoftInput.AdjustResize);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        Capture(intent);
    }

    private static void Capture(Intent? intent)
    {
        if (CaptureTap(intent)) return;

        var data = intent?.DataString;
        if (string.IsNullOrWhiteSpace(data)) return;
        if (!data.StartsWith("aether://", StringComparison.OrdinalIgnoreCase)) return;

        // Only accept links that actually carry a usable tag; anything else is ignored rather than
        // pushed at the user.
        if (!ContactService.TryParseInvite(data, out _, out _)) return;

        PendingLink = data;
        LinkReceived?.Invoke(data);

        // The one that actually reaches the app. The two lines above are kept because the activity is
        // built before the container is, so a link that arrives on a cold launch has nowhere else to
        // wait — but the relay is what the UI listens to.
        InviteLinks.Current?.Deliver(data);
    }

    /// <summary>
    /// Somebody's phone was touched against this one, and it was an Aether phone.
    /// </summary>
    /// <remarks>
    /// The tag carries two records: an address, for a phone that has never heard of Aether, and this
    /// one, for a phone that has. A handset with the app reads the second and knows who it touched; a
    /// handset without it ignores a record type it does not recognise, exactly as the spec says it
    /// must, and follows the address instead. One gesture, two meanings, decided by what the phone on
    /// the other side already has.
    /// </remarks>
    private static bool CaptureTap(Intent? intent)
    {
        if (intent?.Action != global::Android.Nfc.NfcAdapter.ActionNdefDiscovered) return false;

        global::Android.Util.Log.Info("AetherTMB", "a tag was read and Android gave it to us");

        try
        {
            if (intent.GetParcelableArrayExtra(global::Android.Nfc.NfcAdapter.ExtraNdefMessages)
                is not { Length: > 0 } messages) return false;

            foreach (var parcel in messages)
            {
                if (parcel is not global::Android.Nfc.NdefMessage message) continue;

                foreach (var record in message.GetRecords() ?? [])
                {
                    if (record is null) continue;
                    if (record.Tnf != global::Android.Nfc.NdefRecord.TnfExternalType) continue;

                    var type = System.Text.Encoding.ASCII.GetString(record.GetTypeInfo() ?? []);
                    if (!string.Equals(type, Ndef.TagRecordType, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var tag = System.Text.Encoding.ASCII.GetString(record.GetPayload() ?? []).Trim();
                    if (string.IsNullOrEmpty(tag)) continue;

                    global::Android.Util.Log.Info("AetherTMB", $"● we touched {tag} — asking what they hold");
                    Taps.Current?.Deliver(tag);
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Info("AetherTMB", "could not read a tap: " + ex.Message);
        }

        return false;
    }
}

namespace AetherNet.Sample;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();

        // The platform WebView does not exist until MAUI has built a handler for it, so this is the
        // moment to configure it — the handler lifecycle is MAUI's own seam for exactly that.
        blazorWebView.HandlerChanged += OnWebViewHandlerChanged;
    }

    /// <summary>The app's own ground, so nothing grey is ever on screen.</summary>
    /// <remarks>
    /// A WebView paints its own background for the second or so before the first frame of the page
    /// exists, and under a dark theme Android renders that blank as a near-black grey of its own
    /// choosing — measured at #1B1B1B on merlin. That grey was the first thing anybody saw on every
    /// launch, and this page's own background was no help: it was bound to a resource named
    /// PageBackgroundColor that is not defined anywhere, so it fell through to the same default.
    /// </remarks>
    private static readonly Color Ground = Application.Current?.RequestedTheme == AppTheme.Dark
        ? Color.FromArgb("#0d1620")
        : Color.FromArgb("#eaeef3");

    /// <summary>
    /// Give the page inside the WebView what a video call needs.
    /// </summary>
    /// <remarks>
    /// The only platform-specific thing left in video. An Android WebView refuses
    /// <c>getUserMedia</c> by default: the request arrives on its chrome client, the stock one answers
    /// nothing, and the page sees <c>NotAllowedError</c> with no way to learn why. An iOS head would
    /// answer the equivalent WKUIDelegate callback in this same place.
    /// </remarks>
    private void OnWebViewHandlerChanged(object? sender, EventArgs e)
    {
#if ANDROID
        if (blazorWebView.Handler?.PlatformView is global::Android.Webkit.WebView native)
        {
            Platforms.Android.WebViewMediaPermissions.Attach(native);

            // Before the page paints, this is what is on screen. Same colour as index.html's boot
            // screen, so the app appears rather than replacing a grey rectangle.
            native.SetBackgroundColor(global::Android.Graphics.Color.ParseColor(
                Ground.ToArgbHex(includeAlpha: false)));
        }
#endif
    }
}

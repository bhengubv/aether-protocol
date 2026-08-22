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
            Platforms.Android.WebViewMediaPermissions.Attach(native);
#endif
    }
}

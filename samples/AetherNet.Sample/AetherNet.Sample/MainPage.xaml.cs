namespace AetherNet.Sample;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();

        // The platform WebView does not exist until MAUI has built a handler for it, so this is the
        // moment to configure it — the handler lifecycle is MAUI's own seam for exactly that.
        blazorWebView.HandlerChanged += OnWebViewHandlerChanged;

        // Somebody can turn the phone dark while the app is open, and the page inside will follow —
        // its stylesheet is written against prefers-color-scheme. The ground behind it has to follow
        // too, or the next cold paint shows the old theme's colour through the new one.
        if (Application.Current is { } app) app.RequestedThemeChanged += (_, _) => PaintTheGround();
    }

    /// <summary>
    /// The app's own ground — asked for now, never remembered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A WebView paints its own background for the second or so before the first frame of the page
    /// exists, and under a dark theme Android renders that blank as a near-black grey of its own
    /// choosing — measured at #1B1B1B on merlin. That grey was the first thing anybody saw on every
    /// launch, and this page's own background was no help: it was bound to a resource named
    /// PageBackgroundColor that is not defined anywhere, so it fell through to the same default.
    /// </para>
    /// <para>
    /// This was a <c>static readonly</c> field, which is wrong twice over. It is evaluated once at
    /// type initialisation and never again, so a phone switched to light while the app is open keeps
    /// painting the dark ground. And <c>Application.Current</c> can still be null at that moment — in
    /// which case <c>null == AppTheme.Dark</c> is false and it silently chose LIGHT, putting a white
    /// rectangle in front of somebody on a dark phone. That is the bug it was written to fix, inverted
    /// and harder to see. So: a method, and a platform answer when MAUI has not got one yet.
    /// </para>
    /// <para>
    /// Returned as a string rather than a <c>Color</c> because the only consumer is Android's
    /// <c>ParseColor</c>, which requires the leading <c>#</c> — round-tripping through
    /// <c>ToArgbHex</c> to get there is a conversion whose output nothing here has ever seen run.
    /// </para>
    /// </remarks>
    private static string Ground() => IsDark() ? "#0d1620" : "#eaeef3";

    /// <summary>Whether the phone is dark right now, asking the platform if MAUI cannot say.</summary>
    private static bool IsDark()
    {
        // What the person chose, before what the phone is set to. Read from Preferences rather than
        // the database because this runs on the first paint, long before the store is open.
#if ANDROID
        switch (Microsoft.Maui.Storage.Preferences.Get(Platforms.Android.AndroidAppTheme.Key, "system"))
        {
            case "light": return false;
            case "dark": return true;
        }
#endif

        var theme = Application.Current?.RequestedTheme ?? AppTheme.Unspecified;
        if (theme != AppTheme.Unspecified) return theme == AppTheme.Dark;

#if ANDROID
        var mode = global::Android.App.Application.Context.Resources?.Configuration?.UiMode
            & global::Android.Content.Res.UiMode.NightMask;
        return mode == global::Android.Content.Res.UiMode.NightYes;
#else
        return false;
#endif
    }

    /// <summary>Paint whatever is behind the page, if the platform view exists yet.</summary>
    private void PaintTheGround()
    {
#if ANDROID
        if (blazorWebView.Handler?.PlatformView is global::Android.Webkit.WebView native)
            native.SetBackgroundColor(global::Android.Graphics.Color.ParseColor(Ground()));
#endif
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
        {
            Platforms.Android.WebViewMediaPermissions.Attach(native);

            // Before the page paints, this is what is on screen. Same colour as index.html's boot
            // screen, so the app appears rather than replacing a grey rectangle.
            PaintTheGround();
        }
#endif
    }
}

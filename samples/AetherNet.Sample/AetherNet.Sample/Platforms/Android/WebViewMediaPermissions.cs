// SPDX-License-Identifier: MIT
#if ANDROID
using Android.Webkit;

namespace AetherNet.Sample.Platforms.Android;

/// <summary>
/// Lets the page inside the BlazorWebView actually reach the camera.
///
/// <para>
/// This is the one genuinely platform-specific thing video needs now, and it is about thirty lines
/// once. An Android WebView refuses <c>getUserMedia</c> by default: the request surfaces on
/// <c>WebChromeClient.OnPermissionRequest</c>, and the stock client answers nothing, so the promise
/// rejects with <c>NotAllowedError</c> and the page has no way to find out why. Granting it here is
/// the documented MAUI extension point rather than a workaround — <c>BlazorWebViewHandler</c>'s
/// mapper is provided precisely so a head can configure its own WebView.
/// </para>
///
/// <para>
/// Two permissions are in play and they are easy to confuse. Android's own runtime permission is what
/// the person grants to the APP, and it is asked for in the ordinary way; this is the WebView asking
/// whether the PAGE may use what the app already has. Both have to be true, so this checks the first
/// before answering the second — granting a WebView a camera the app has not been given produces a
/// failure much further away, inside the media stack, with nothing to point at.
/// </para>
///
/// <para>
/// An iOS head needs the same bridge in its own idiom — <c>WKUIDelegate</c>'s media capture callback,
/// plus the usage description in Info.plist. That is the whole platform surface of a video call now.
/// </para>
/// </summary>
internal static class WebViewMediaPermissions
{
    /// <summary>
    /// Teach this BlazorWebView to pass camera and microphone requests through.
    /// </summary>
    /// <remarks>
    /// Called from <c>BlazorWebViewInitialized</c>, which is the moment the platform view exists and
    /// the documented place to configure it.
    /// </remarks>
    public static void Attach(global::Android.Webkit.WebView? webView)
    {
        if (webView is null) return;
        webView.SetWebChromeClient(new MediaChromeClient(webView.WebChromeClient));
    }

    /// <summary>
    /// Answers the page's request for a camera, and forwards everything else to the client MAUI
    /// already installed.
    /// </summary>
    /// <remarks>
    /// Wrapping rather than replacing matters: MAUI's own client carries behaviour this app has no
    /// business dropping — file choosers, console message routing, the JS bridge's own callbacks. A
    /// client that only implements the one method it cares about silently removes the rest.
    /// </remarks>
    private sealed class MediaChromeClient(WebChromeClient? inner) : WebChromeClient
    {
        public override void OnPermissionRequest(PermissionRequest? request)
        {
            if (request is null) return;

            var wanted = request.GetResources() ?? [];
            var granting = new List<string>(wanted.Length);

            foreach (var resource in wanted)
            {
                // Only what the person has already given the app. Anything else is refused rather
                // than passed along to fail somewhere less legible.
                var appHasIt = resource switch
                {
                    PermissionRequest.ResourceVideoCapture =>
                        Permissions.CheckStatusAsync<Permissions.Camera>().Result == PermissionStatus.Granted,
                    PermissionRequest.ResourceAudioCapture =>
                        Permissions.CheckStatusAsync<Permissions.Microphone>().Result == PermissionStatus.Granted,
                    _ => false,
                };

                if (appHasIt) granting.Add(resource);
            }

            if (granting.Count > 0) request.Grant([.. granting]);
            else request.Deny();
        }

        /// <summary>
        /// Open a chooser when the page asks for a file.
        /// </summary>
        /// <remarks>
        /// Forwarded to MAUI's client first, in case a future version answers it. Today none does —
        /// the call falls through to <c>base</c>, which returns false, and a tap on a file input does
        /// nothing whatsoever: no error, no console message. So this answers it.
        /// </remarks>
        public override bool OnShowFileChooser(global::Android.Webkit.WebView? view,
            IValueCallback? filePathCallback, FileChooserParams? fileChooserParams)
            => (inner?.OnShowFileChooser(view, filePathCallback, fileChooserParams) ?? false)
               || WebViewFilePicker.Show(filePathCallback, fileChooserParams);

        public override bool OnConsoleMessage(ConsoleMessage? consoleMessage)
            => inner?.OnConsoleMessage(consoleMessage) ?? base.OnConsoleMessage(consoleMessage);

        public override void OnPermissionRequestCanceled(PermissionRequest? request)
            => inner?.OnPermissionRequestCanceled(request);
    }
}
#endif

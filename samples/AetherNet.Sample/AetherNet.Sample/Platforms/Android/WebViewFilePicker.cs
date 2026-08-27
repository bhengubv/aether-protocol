// SPDX-License-Identifier: MIT
#if ANDROID
using Android.App;
using Android.Content;
using Android.Webkit;

namespace AetherNet.Sample.Platforms.Android;

/// <summary>
/// Lets a page inside the BlazorWebView open a file chooser.
///
/// <para>
/// An Android WebView does not open one by itself. A tap on <c>&lt;input type="file"&gt;</c> surfaces
/// on <c>WebChromeClient.OnShowFileChooser</c>, and if nothing answers, the tap does nothing at all —
/// no error, no console message, no clue. MAUI's own chrome client does not answer it, so a file
/// input inside a Blazor Hybrid app is inert. Measured on a P30: the button was tapped, the page
/// re-rendered, and no chooser appeared.
/// </para>
///
/// <para>
/// Why the WebView rather than <c>MediaPicker</c>. A photograph off a phone is several megabytes, and
/// it has to be shrunk before it can cross a radio. The canvas that does the shrinking lives in the
/// page, so letting the page hold the file means the large bytes never leave the WebView at all —
/// only the hundred-odd kilobytes that come out the other side are handed to C#. Picking in C#
/// instead would mean carrying the whole original across the interop boundary as base64 just to send
/// it back again.
/// </para>
///
/// <para>
/// No permission is asked for and none is needed: the chooser is the system's, and it hands back a
/// content URI our process is granted access to for as long as it holds it. The app never gains the
/// right to read anybody's gallery — it gains the right to read the one file a person pointed at.
/// </para>
/// </summary>
internal static class WebViewFilePicker
{
    /// <summary>Ours, and unlikely to collide with MAUI's own.</summary>
    public const int RequestCode = 0x0A37;

    private static IValueCallback? _waiting;
    private static readonly Lock Gate = new();

    /// <summary>
    /// Open a chooser for whatever the page asked for.
    /// </summary>
    /// <returns>True if a chooser was started; false leaves the input inert, which the caller reports.</returns>
    public static bool Show(IValueCallback? callback, WebChromeClient.FileChooserParams? asked)
    {
        if (callback is null) return false;

        // Whatever was waiting is now abandoned. Left unanswered, the page's input stays stuck open
        // forever and a second tap does nothing — so it is told, explicitly, that nothing came back.
        Abandon();

        if (Platform.CurrentActivity is not { } activity)
        {
            callback.OnReceiveValue(null);
            return true;
        }

        var intent = new Intent(Intent.ActionGetContent);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType(Wanted(asked));

        // Whether more than one may be chosen is the page's decision, made in its own markup.
        if (asked?.Mode == ChromeFileChooserMode.OpenMultiple)
            intent.PutExtra(Intent.ExtraAllowMultiple, true);

        lock (Gate) _waiting = callback;

        try
        {
            activity.StartActivityForResult(
                Intent.CreateChooser(intent, asked?.Title ?? "Choose a picture"), RequestCode);
            return true;
        }
        catch (ActivityNotFoundException)
        {
            // Nothing on this phone can pick a file. Saying so beats a button that does nothing.
            Abandon();
            return true;
        }
    }

    /// <summary>
    /// Hand the chosen file — or the fact that nothing was chosen — back to the page.
    /// </summary>
    /// <remarks>
    /// Called from the activity, which is the only thing that receives results. Answering with null on
    /// a cancel is not optional: an input left unanswered never fires again, so backing out of the
    /// chooser once would break picking a picture for the rest of the session.
    /// </remarks>
    public static void Answer(Result resultCode, Intent? data)
    {
        IValueCallback? callback;
        lock (Gate) (callback, _waiting) = (_waiting, null);

        if (callback is null) return;

        var chosen = resultCode == Result.Ok
            ? WebChromeClient.FileChooserParams.ParseResult((int)resultCode, data)
            : null;

        callback.OnReceiveValue(chosen is { Length: > 0 } ? chosen : null);
    }

    /// <summary>Tell whatever was waiting that nothing is coming.</summary>
    private static void Abandon()
    {
        IValueCallback? callback;
        lock (Gate) (callback, _waiting) = (_waiting, null);

        callback?.OnReceiveValue(null);
    }

    /// <summary>
    /// What the page asked for, or anything if it did not say.
    /// </summary>
    /// <remarks>
    /// The page's own <c>accept</c> attribute, honoured rather than overridden — the chooser then
    /// shows pictures for a picture input, and the page stays the thing that decides what it wants.
    /// </remarks>
    private static string Wanted(WebChromeClient.FileChooserParams? asked)
    {
        var types = asked?.GetAcceptTypes() ?? [];

        foreach (var type in types)
            if (!string.IsNullOrWhiteSpace(type) && type.Contains('/'))
                return type;

        return "*/*";
    }
}
#endif

// SPDX-License-Identifier: MIT

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// Tells the app shell what the person chose, so the surfaces the page cannot reach follow it too.
/// </summary>
/// <remarks>
/// Stamping <c>data-theme</c> on the document changes the page and nothing else. Behind the page sit
/// two things the browser has no say over: the WebView's own background, painted for the second
/// before the first frame exists, and the activity window behind that. Both read the phone's theme —
/// so somebody choosing Light on a dark phone got a light page on a dark ground, which is the exact
/// mismatch the ground was introduced to remove.
/// </remarks>
public interface IAppTheme
{
    /// <summary>Apply a choice of "light", "dark" or "system" to the shell.</summary>
    void Apply(string theme);
}

/// <summary>Stands in where there is no shell to tell — the web head is only ever the page.</summary>
public sealed class NullAppTheme : IAppTheme
{
    public void Apply(string theme) { }
}

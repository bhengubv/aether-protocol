// SPDX-License-Identifier: MIT

using System.Linq;
using ZXing.Net.Maui;
using ZXing.Net.Maui.Controls;

namespace AetherNet.Sample;

/// <summary>
/// A full-screen camera that reads a QR and reports the first one it sees, once.
///
/// <para>
/// Built in code, not XAML: it is tiny and created by the scanner bridge, not navigated to as a route.
/// It exists as a native MAUI page because the app's UI is Blazor in a WebView and ZXing's reader is a
/// MAUI control, not an HTML element — so the bridge pushes this modally over the WebView host, and it
/// reports back through the callback the moment a code decodes or the person cancels.
/// </para>
/// </summary>
public sealed class ScannerPage : ContentPage
{
    private readonly Action<string?> _done;
    private bool _reported;

    public ScannerPage(Action<string?> done)
    {
        _done = done;
        Title = "Scan a code";
        BackgroundColor = Colors.Black;

        var reader = new CameraBarcodeReaderView
        {
            // 2-D formats only — a QR is all an invite is ever encoded as, and narrowing the decoder
            // to it is faster and refuses a stray 1-D barcode that is not ours.
            Options = new BarcodeReaderOptions { Formats = BarcodeFormats.TwoDimensional },
        };
        reader.BarcodesDetected += OnDetected;

        var hint = new Label
        {
            Text = "Point at their Aether QR",
            TextColor = Colors.White,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Start,
            Margin = new Thickness(0, 60, 0, 0),
        };

        var cancel = new Button
        {
            Text = "Cancel",
            Margin = new Thickness(24, 0, 24, 40),
            VerticalOptions = LayoutOptions.End,
        };
        cancel.Clicked += (_, _) => Report(null);

        Content = new Grid { Children = { reader, hint, cancel } };
    }

    private void OnDetected(object? sender, BarcodeDetectionEventArgs e)
    {
        var value = e.Results?.FirstOrDefault()?.Value;
        if (!string.IsNullOrWhiteSpace(value)) Report(value);
    }

    // The hardware / gesture back is a cancel too, not a way to leave the callback unanswered.
    protected override bool OnBackButtonPressed()
    {
        Report(null);
        return true;
    }

    private void Report(string? value)
    {
        if (_reported) return;   // first code (or first cancel) wins; ignore the rest
        _reported = true;
        _done(value);
    }
}

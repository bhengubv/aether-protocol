// SPDX-License-Identifier: MIT

namespace Aether.Shared.Services;

/// <summary>
/// The phone's own switches — Bluetooth, Location, NFC, Wi-Fi and mobile data — on the pages where the phone keeps
/// them, and word when the person is back in the app.
/// </summary>
/// <remarks>
/// AetherNetService puts up the phone's own prompt for a switch over the app (IAetherNetServiceSettings.Ask); one from
/// before it could (1.5 and older) only says what each radio is waiting on, and then the app the person is using opens
/// the phone's page. And the phone tells no app when one of these is switched, so coming back is said too — what the
/// radios need is asked again then.
/// </remarks>
public interface IPhoneSettings
{
    /// <summary>Open the phone's own page for <paramref name="setting"/>. False when this phone would not open it.</summary>
    bool Open(PhoneSetting setting);

    /// <summary>The person is back in the app, from that page or from anywhere else.</summary>
    event Action? Back;
}

/// <summary>A switch the phone keeps, which a radio can be waiting on.</summary>
public enum PhoneSetting
{
    /// <summary>The phone's Settings, for a switch this app does not know by name.</summary>
    Settings,
    Bluetooth,
    Location,
    Nfc,
    /// <summary>Wi-Fi and mobile data.</summary>
    Internet,
}

/// <summary>Which of the phone's switches one of AetherNetService's radios is waiting on.</summary>
public static class PhoneSwitches
{
    /// <summary>
    /// The phone's switch a radio is waiting on, read from what AetherNetService says is wrong (<paramref name="why"/>),
    /// or null for the internet waiting on somebody in the Circle to relay, which no switch on this phone changes.
    /// </summary>
    public static PhoneSetting? For(string radio, string? why)
    {
        why ??= "";
        if (radio == "Internet")
            return why.StartsWith("no internet", StringComparison.OrdinalIgnoreCase) ? PhoneSetting.Internet : null;

        // Below Android 12 Bluetooth, and below 13 Wi-Fi Direct and Wi-Fi Aware, find nobody with Location off.
        if (why.Contains("location", StringComparison.OrdinalIgnoreCase)) return PhoneSetting.Location;

        return radio switch
        {
            "BLE" => PhoneSetting.Bluetooth,
            "NFC" => PhoneSetting.Nfc,
            _ => PhoneSetting.Settings,
        };
    }
}

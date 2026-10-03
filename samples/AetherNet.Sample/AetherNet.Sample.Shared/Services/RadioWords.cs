// SPDX-License-Identifier: MIT
namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// One of AetherNetService's radios, in a person's words: what it is called, what it does while on, what is lost while
/// off. Every radio is on until the person switches it off, so both are said — the choice is theirs, made knowing both.
/// </summary>
/// <param name="Name">What the radio is called on the screen.</param>
/// <param name="On">What it does while it is on.</param>
/// <param name="Off">What is lost while it is off.</param>
/// <param name="Nearby">Whether it is one of the nearby radios the AetherNet switch turns off with it (the internet relay is not).</param>
public sealed record RadioWords(string Name, string On, string Off, bool Nearby = true)
{
    /// <summary>The words for a radio, by the name AetherNetService gives it.</summary>
    public static RadioWords For(string radio) => radio switch
    {
        "Wi-Fi Direct" => new("Wi-Fi Direct",
            "devices near you link straight to each other — fast enough for calls and video, no router needed",
            "devices near you reach you only through a shared Wi-Fi or Bluetooth, and calls and big files may not get through"),
        "BLE" => new("Bluetooth",
            "finds devices near you, and carries messages where there is no Wi-Fi at all",
            "where there is no Wi-Fi, devices near you cannot reach you and you cannot reach them"),
        "Wi-Fi Aware" => new("Wi-Fi Aware",
            "phones that have it find each other over Wi-Fi without joining a network",
            "those phones find you a little slower, over the other radios"),
        "Wi-Fi" => new("Your Wi-Fi network",
            "reaches people on the same Wi-Fi or network as you, through the router — it sees that you talk, never what you say",
            "people on your network reach you only over the other radios"),
        "Internet" => new("Internet relay",
            "when nobody is near, messages go through a phone in your Circle that offered to relay — sealed, so it cannot read them",
            "when nobody is near, messages wait until someone is",
            Nearby: false),
        "NFC" => new("Tap to add",
            "hold two devices right up against each other to swap AetherTags",
            "add people with a QR code or a link instead"),
        "LoRa" => new("LoRa",
            "with a LoRa radio plugged in, reaches people kilometres away, slowly",
            "a plugged-in LoRa radio is not used"),
        _ => new(radio,
            "carries messages whenever it can reach someone",
            "stays down, and the other radios carry what they can"),
    };
}

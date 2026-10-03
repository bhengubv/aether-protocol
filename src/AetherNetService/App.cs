// SPDX-License-Identifier: MIT
namespace AetherNetService;

/// <summary>
/// The app MAUI hosts, with no window. AetherNetService has no UI (a screen is attack surface), so nothing ever asks
/// this for one.
/// </summary>
public sealed class App : Microsoft.Maui.Controls.Application
{
}

// SPDX-License-Identifier: MIT
// Ported from Fieldwatch app/src/main/java/app/fieldwatch/domain/Models.kt (github.com/offgridpete/fieldwatch, cf6562d).
// Copyright (c) 2026 Off Grid Pete LLC. See NOTICE.md.

namespace AetherNet.Aware;

/// <summary>Which radio heard it: a Wi-Fi access point's beacon, or a Bluetooth Low Energy advert.</summary>
public enum RadioKind
{
    Wifi,
    Ble,
}

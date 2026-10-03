// SPDX-License-Identifier: MIT
// Ported from Fieldwatch app/src/main/java/app/fieldwatch/domain/Models.kt (github.com/offgridpete/fieldwatch, cf6562d).
// Copyright (c) 2026 Off Grid Pete LLC. See NOTICE.md.

namespace AetherNet.Aware;

/// <summary>The kind of thing a signature names: a finder tag, a drone, a camera, a router…</summary>
public enum SignatureClass
{
    Finder,
    Beacon,
    Signage,
    Wearable,
    Surveillance,
    Drone,
    Hacking,

    /// <summary>A retired class, folded into <see cref="Wearable"/>; kept so older packs still read.</summary>
    Bodyworn,
    LawEnforcement,
    Vehicle,
    Glasses,
    Audio,
    Camera,
    Thermostat,
    Lock,
    Health,
    Home,
    Isp,
    Mesh,
    Phone,
    Other,
}

public static class SignatureClassText
{
    /// <summary>Fieldwatch's own words for the class.</summary>
    public static string Label(this SignatureClass kind) => kind switch
    {
        SignatureClass.Finder => "Finder tags",
        SignatureClass.Beacon => "Retail beacons",
        SignatureClass.Signage => "Signage",
        SignatureClass.Wearable => "Wearables",
        SignatureClass.Surveillance => "Surveillance",
        SignatureClass.Drone => "Drones",
        SignatureClass.Hacking => "Pentest",
        SignatureClass.Bodyworn => "Body-worn",
        SignatureClass.LawEnforcement => "Public safety",
        SignatureClass.Vehicle => "Vehicle",
        SignatureClass.Glasses => "Glasses",
        SignatureClass.Audio => "Audio",
        SignatureClass.Camera => "Cameras",
        SignatureClass.Thermostat => "Thermostats",
        SignatureClass.Lock => "Access control",
        SignatureClass.Health => "Health",
        SignatureClass.Home => "Home IoT",
        SignatureClass.Isp => "ISP / routers",
        SignatureClass.Mesh => "Mesh",
        SignatureClass.Phone => "Phones / PCs",
        _ => "Other",
    };

    /// <summary>Body-worn was a leftover bucket; it reads as Wearables.</summary>
    public static SignatureClass Folded(this SignatureClass kind) =>
        kind == SignatureClass.Bodyworn ? SignatureClass.Wearable : kind;
}

namespace TrayCer.Core.Models;

/// <summary>
/// The protocol a printer speaks. Values are persisted, so append new kinds; never renumber.
/// </summary>
public enum PrinterKind
{
    OctoPrint = 0,
    Moonraker = 1,
    PrusaLink = 2,
    BambuLab = 3,
    Creality = 4,
    Elegoo = 5,
    FlashForge = 6,
    FlashForgeLegacy = 7,
    Duet = 8,
    Ultimaker = 9,
    Repetier = 10,
}

/// <summary>
/// What a kind needs from the user and what it covers. The add-printer form shows only the fields
/// a kind asks for, so a printer that needs nothing but an address gets a one-field form.
/// </summary>
public sealed record PrinterKindInfo(
    PrinterKind Kind,
    string DisplayName,
    string Covers,
    int DefaultPort,
    string? SecretLabel,
    bool SecretRequired,
    string? SecretHelp,
    string? SerialLabel,
    string? SerialHelp,
    bool AsksForUsername,
    bool AsksForSlug,
    bool HasWebInterface)
{
    public static IReadOnlyList<PrinterKindInfo> All { get; } =
    [
        new(PrinterKind.BambuLab, "Bambu Lab", "X1, P1, A1 and H2 series", 8883,
            "Access code", true, "On the printer: Settings > Network (WLAN). Turn on LAN mode or Developer mode if the printer refuses to connect.",
            "Serial number", "On the printer: Settings > Device. It's also on the label on the back.",
            AsksForUsername: false, AsksForSlug: false, HasWebInterface: false),
        new(PrinterKind.PrusaLink, "Prusa (PrusaLink)", "Core One, MK4/MK4S, MK3.9, XL, MINI, and MK3S with PrusaLink", 80,
            "Password", true, "On the printer: Settings > Network > PrusaLink. Older PrusaLink versions use an API key instead; leave the user name empty for those.",
            null, null, AsksForUsername: true, AsksForSlug: false, HasWebInterface: true),
        new(PrinterKind.Moonraker, "Klipper (Moonraker)", "Voron, RatRig, Sovol SV08, Elegoo Neptune 4, Qidi, rooted Creality K1/K2, Mainsail and Fluidd printers", 7125,
            "API key", false, "Only needed when Moonraker has authorization turned on. Find it in Mainsail or Fluidd under Settings > Authorization.",
            null, null, AsksForUsername: false, AsksForSlug: false, HasWebInterface: true),
        new(PrinterKind.OctoPrint, "OctoPrint", "Any printer connected to OctoPrint or OctoPi", 80,
            "API key", true, "In OctoPrint: Settings > Application Keys. Generate a key for Traycer.",
            null, null, AsksForUsername: false, AsksForSlug: false, HasWebInterface: true),
        new(PrinterKind.Creality, "Creality", "K1, K1C, K1 Max, K2, Hi and Ender-3 V3 with stock firmware", 9999,
            null, false, null, null, null, AsksForUsername: false, AsksForSlug: false, HasWebInterface: false),
        new(PrinterKind.Elegoo, "Elegoo", "Centauri Carbon, and Saturn and Mars resin printers", 3030,
            null, false, null, null, null, AsksForUsername: false, AsksForSlug: false, HasWebInterface: false),
        new(PrinterKind.FlashForge, "FlashForge", "Adventurer 5M, 5M Pro, AD5X and Creator 5", 8898,
            "Check code", true, "On the printer: Settings > Network (the Wi-Fi icon). Turn on LAN-only mode to see it.",
            "Serial number", "On the printer: Settings > About, or on the label underneath.",
            AsksForUsername: false, AsksForSlug: false, HasWebInterface: false),
        new(PrinterKind.FlashForgeLegacy, "FlashForge (older)", "Adventurer 3 and 4, Finder, Guider and Creator", 8899,
            null, false, null, null, null, AsksForUsername: false, AsksForSlug: false, HasWebInterface: false),
        new(PrinterKind.Duet, "Duet (RepRapFirmware)", "Duet 2 and 3 boards, standalone or with a Raspberry Pi", 80,
            "Password", false, "Only needed when the board has a password set (M551).",
            null, null, AsksForUsername: false, AsksForSlug: false, HasWebInterface: true),
        new(PrinterKind.Ultimaker, "UltiMaker", "S3, S5, S7 and Ultimaker 3", 80,
            null, false, null, null, null, AsksForUsername: false, AsksForSlug: false, HasWebInterface: true),
        new(PrinterKind.Repetier, "Repetier-Server", "Any printer connected to Repetier-Server", 3344,
            "API key", false, "In Repetier-Server: Global settings > Connectivity > API key.",
            null, null, AsksForUsername: false, AsksForSlug: true, HasWebInterface: true),
    ];

    public static PrinterKindInfo For(PrinterKind kind) => All.First(info => info.Kind == kind);
}

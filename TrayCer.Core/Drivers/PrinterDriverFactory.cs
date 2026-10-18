using TrayCer.Core.Drivers.Bambu;
using TrayCer.Core.Drivers.Creality;
using TrayCer.Core.Drivers.Duet;
using TrayCer.Core.Drivers.Elegoo;
using TrayCer.Core.Drivers.FlashForge;
using TrayCer.Core.Drivers.Moonraker;
using TrayCer.Core.Drivers.OctoPrint;
using TrayCer.Core.Drivers.PrusaLink;
using TrayCer.Core.Drivers.Repetier;
using TrayCer.Core.Drivers.Ultimaker;
using TrayCer.Core.Models;
using TrayCer.Core.Net;

namespace TrayCer.Core.Drivers;

public static class PrinterDriverFactory
{
    /// <param name="secret">The API key, access code, password or check code from the vault.</param>
    /// <param name="handler">Tests pass a fake; null builds a real one per printer.</param>
    public static IPrinterDriver Create(PrinterConfig config, string? secret, HttpMessageHandler? handler = null)
    {
        PrinterEndpoint endpoint = PrinterEndpoint.Parse(config.Host, config.Info.DefaultPort);
        HttpMessageHandler Http() => handler ?? PrinterHttp.CreateHandler();

        return config.Kind switch
        {
            PrinterKind.OctoPrint => new OctoPrintDriver(endpoint, secret, Http()),
            PrinterKind.Moonraker => new MoonrakerDriver(endpoint, secret, Http()),
            PrinterKind.PrusaLink => PrusaLinkDriver.Create(endpoint, config.Username, secret, handler),
            PrinterKind.BambuLab => new BambuDriver(endpoint, config.Serial ?? "", secret ?? ""),
            PrinterKind.Creality => new CrealityDriver(endpoint, Http()),
            PrinterKind.Elegoo => new SdcpDriver(endpoint),
            PrinterKind.FlashForge => new FlashForgeDriver(endpoint, config.Serial ?? "", secret ?? "", Http()),
            PrinterKind.FlashForgeLegacy => new FlashForgeLegacyDriver(endpoint),
            PrinterKind.Duet => new DuetDriver(endpoint, secret, Http()),
            PrinterKind.Ultimaker => new UltimakerDriver(endpoint, Http()),
            PrinterKind.Repetier => new RepetierDriver(endpoint, secret, config.Slug, Http()),
            _ => throw new NotSupportedException($"No driver for {config.Kind}."),
        };
    }

    /// <summary>What the form must collect before a printer of this kind can be added; null when complete.</summary>
    public static string? MissingField(PrinterConfig config, string? secret)
    {
        PrinterKindInfo info = config.Info;
        if (!PrinterEndpoint.TryParse(config.Host, info.DefaultPort, out _))
        {
            return "Enter the printer's IP address or host name.";
        }

        if (info.SerialLabel is not null && string.IsNullOrWhiteSpace(config.Serial))
        {
            return $"Enter the {info.SerialLabel.ToLowerInvariant()}.";
        }

        if (info.SecretRequired && string.IsNullOrWhiteSpace(secret))
        {
            return $"Enter the {info.SecretLabel!.ToLowerInvariant()}.";
        }

        return null;
    }
}

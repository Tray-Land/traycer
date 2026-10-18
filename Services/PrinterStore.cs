using TrayCer.Core.Models;

namespace TrayCer.Services;

/// <summary>The saved printers: configs in settings, each one's secret in the vault.</summary>
internal static class PrinterStore
{
    // Read on every status update, so parse the settings once and again only after a change.
    private static IReadOnlyList<PrinterConfig>? _all;

    /// <summary>Raised after a printer is added, edited or removed, with its id.</summary>
    public static event EventHandler<string>? Changed;

    public static IReadOnlyList<PrinterConfig> All => _all ??= SettingsService.Printers;

    public static PrinterConfig? Find(string id) => All.FirstOrDefault(p => p.Id == id);

    public static string? SecretFor(string id) => AccessCodeVault.Get(id);

    /// <summary>Adds or replaces <paramref name="config"/>, and its secret unless <paramref name="secret"/> is null.</summary>
    public static void Save(PrinterConfig config, string? secret)
    {
        List<PrinterConfig> printers = [.. All];
        int index = printers.FindIndex(p => p.Id == config.Id);
        if (index >= 0)
        {
            printers[index] = config;
        }
        else
        {
            printers.Add(config);
        }

        if (secret is not null)
        {
            AccessCodeVault.Set(config.Id, secret);
        }

        SettingsService.Printers = printers;
        _all = null;
        Changed?.Invoke(null, config.Id);
    }

    public static void Remove(string id)
    {
        SettingsService.Printers = [.. All.Where(p => p.Id != id)];
        _all = null;
        AccessCodeVault.Remove(id);
        Changed?.Invoke(null, id);
    }
}

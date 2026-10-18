using TrayCer.Core.Models;

namespace TrayCer.Core.Discovery;

/// <summary>A printer found on the network or identified at an address, ready to prefill the add form.</summary>
/// <param name="Host">The address to save, with a port only when it isn't the kind's default.</param>
/// <param name="NeedsSecret">True when the printer is known to want a password or key (a Duet with one set).</param>
public sealed record DiscoveredPrinter(
    PrinterKind Kind,
    string Host,
    string? Name = null,
    string? Serial = null,
    string? Model = null,
    bool NeedsSecret = false)
{
    /// <summary>What to call it in a list: its name, else its model, else its address.</summary>
    public string Title => Name ?? Model ?? Host;
}

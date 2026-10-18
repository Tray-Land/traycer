using TrayCer.Core.Models;

namespace TrayCer.Core.Monitoring;

public enum ConnectionState
{
    /// <summary>No answer yet since the monitor started.</summary>
    Connecting,
    Connected,

    /// <summary>The last attempt failed, but recently enough that the last reading still stands.</summary>
    Reconnecting,
    Unreachable,
    AuthFailed,

    /// <summary>Something answered, but not like this kind of printer.</summary>
    Incompatible,
}

public enum PrintEventKind
{
    Started,
    Finished,
    Cancelled,

    /// <summary>A print ended in an error.</summary>
    Failed,

    /// <summary>The printer went into an error state outside a print.</summary>
    Error,
    Paused,
    NeedsAttention,
}

/// <summary>Something that happened to a print, found by comparing two readings.</summary>
public sealed record PrintEvent(PrintEventKind Kind, string? JobName, string? Message, DateTimeOffset At);

/// <summary>What the tray icon should flag for a printer until the user opens the flyout.</summary>
public enum AttentionLevel
{
    None,
    Done,
    Warning,
    Error,
}

/// <summary>Everything the UI knows about one printer: its last reading plus how the connection is going.</summary>
public sealed record PrinterStatus
{
    public required PrinterConfig Config { get; init; }

    /// <summary>The last successful reading; it stays while the printer is briefly unreachable.</summary>
    public PrinterSnapshot? Snapshot { get; init; }

    public ConnectionState Connection { get; init; } = ConnectionState.Connecting;

    /// <summary>Why the last attempt failed, worded for the user.</summary>
    public string? Problem { get; init; }

    public DateTimeOffset? LastUpdated { get; init; }

    public DateTimeOffset? NextCheck { get; init; }

    /// <summary>The most recent print event, so a card can say "Finished at 3:42 PM" after the printer went idle.</summary>
    public PrintEvent? LastEvent { get; init; }

    public AttentionLevel Attention { get; init; }

    /// <summary>True when the reading can be shown as current.</summary>
    public bool HasReading => Snapshot is not null && Connection is ConnectionState.Connected or ConnectionState.Reconnecting;

    /// <summary>The state to show: the reading's while it stands, Offline once the printer has been unreachable for a while.</summary>
    public PrinterState DisplayState => HasReading ? Snapshot!.State : PrinterState.Offline;
}

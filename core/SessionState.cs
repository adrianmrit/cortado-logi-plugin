namespace Cortado.Core;

public enum KeepAwakeMode { MacOnly, MacAndDisplay }
public enum SessionKind { Indefinite, Duration, Until, Unknown }

public sealed record ActiveSession(SessionKind Kind, KeepAwakeMode Mode, DateTimeOffset? Deadline);

public sealed record SavedState(Int32 SchemaVersion, KeepAwakeMode PreferredMode)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public SavedSession? Session { get; init; }
    public static SavedState Empty => new(3, KeepAwakeMode.MacAndDisplay);

    public void Validate()
    {
        if (SchemaVersion is not (1 or 2 or 3) || !Enum.IsDefined(PreferredMode))
        {
            throw new InvalidDataException("Unsupported Cortado settings. Back up or remove state.json to reset.");
        }

        Session?.Validate();
    }
}

public sealed record SavedSession(ExternalCaffeinateSession Identity, String BootIdentity,
    ActiveSession Active, Int64 StartedAt, Int64 TimestampFrequency, TimeSpan? InitialRemaining)
{
    public void Validate()
    {
        if (Identity is null || Identity.Pid <= 0 || Identity.StartSeconds < 0 ||
            Identity.StartMicroseconds is < 0 or >= 1_000_000 || String.IsNullOrWhiteSpace(BootIdentity) ||
            Active is null || !Enum.IsDefined(Active.Mode) ||
            Active.Kind is not (SessionKind.Indefinite or SessionKind.Duration or SessionKind.Until) ||
            StartedAt < 0 || TimestampFrequency <= 0 ||
            (Active.Kind == SessionKind.Indefinite
                ? Active.Deadline is not null || InitialRemaining is not null
                : Active.Deadline is null || InitialRemaining is null ||
                  InitialRemaining <= TimeSpan.Zero || Math.Ceiling(InitialRemaining.Value.TotalSeconds) > Int32.MaxValue))
        {
            throw new InvalidDataException("Cortado state.json has invalid session metadata.");
        }
    }
}

public sealed record SessionSnapshot(Int64 Revision, KeepAwakeMode PreferredMode,
    ActiveSession? Active, TimeSpan? Remaining, String? Error)
{
    internal static readonly TimeSpan ExpirationGrace = TimeSpan.FromSeconds(2);
    public IReadOnlyList<ExternalCaffeinateSession> ExternalSessions { get; init; } = [];
    public TimeSpan? ExternalRemaining { get; init; }
    public Boolean HasObservation { get; init; } = true;
    public String? ObservationError { get; init; }
    public Int64 CheckedTimestamp { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
    public Boolean CanControl => HasObservation && ObservationError is null;
    public Boolean IsActive => Active is not null || ExternalSessions.Count > 0;
    public Boolean CanEdit => CanControl && Active?.Deadline is not null && Remaining > TimeSpan.Zero && ExternalSessions.Count == 0;
    public Boolean IsExpiring => CanControl &&
        ((Active?.Deadline is not null && Remaining is { } remaining &&
            remaining <= TimeSpan.FromSeconds(1) && remaining >= -ExpirationGrace) ||
        ExternalSessions.Any(session => session.Deadline - ObservedAt is { } left &&
            left <= TimeSpan.FromSeconds(1) && left >= -ExpirationGrace));

    public String Status => ObservationError is not null ? "Error" : !HasObservation ? "Checking" :
        Active?.Kind == SessionKind.Indefinite ? "Indefinitely" :
        ExternalSessions.Count > 0 ? ExternalStatus : Active switch
        {
            null => "Off",
            { Kind: SessionKind.Unknown } => "Unknown",
            { Kind: SessionKind.Indefinite } => "Indefinitely",
            { Kind: SessionKind.Until, Deadline: { } deadline } => $"Until {deadline.ToLocalTime():HH:mm}",
            _ => FormatRemaining(Remaining ?? TimeSpan.Zero)
        };

    private String ExternalStatus => Active?.Kind == SessionKind.Unknown ||
        ExternalSessions.Any(session => session.Deadline is null || session.Deadline < ObservedAt - ExpirationGrace)
        ? "Unknown"
        : "~" + FormatRemaining(Remaining is { } owned && owned > ExternalRemaining ? owned : ExternalRemaining ?? TimeSpan.Zero);

    public static String FormatRemaining(TimeSpan remaining)
    {
        var seconds = Math.Max(0L, (Int64)Math.Ceiling(remaining.TotalSeconds));
        return seconds >= 3600
            ? $"{seconds / 3600}:{seconds % 3600 / 60:00}:{seconds % 60:00}"
            : $"{seconds / 60}:{seconds % 60:00}";
    }
}

public interface IStateStore
{
    SavedState Load();
    void Save(SavedState state);
}

public interface IAwakeProcess : IDisposable
{
    ExternalCaffeinateSession Identity { get; }
    Int32 Id => Identity.Pid;
    Boolean HasExited { get; }
    void Stop();
}

public sealed record ExternalCaffeinateSession(Int32 Pid, Int64 StartSeconds, Int64 StartMicroseconds, DateTimeOffset? Deadline)
{
    public Boolean SameProcess(ExternalCaffeinateSession other) => Pid == other.Pid &&
        StartSeconds == other.StartSeconds && StartMicroseconds == other.StartMicroseconds;
}

public interface IExternalSessionMonitor
{
    IReadOnlyList<ExternalCaffeinateSession> Read(Int32? excludedPid);
    void Stop(ExternalCaffeinateSession session);
}

public interface IAwakeProcessFactory
{
    String BootIdentity { get; }
    IAwakeProcess Start(KeepAwakeMode mode, TimeSpan? remaining);
    IAwakeProcess? Attach(ExternalCaffeinateSession identity);
}

public sealed class SessionConflictException()
    : InvalidOperationException("The session changed on another control. Review the refreshed menu and try again.");
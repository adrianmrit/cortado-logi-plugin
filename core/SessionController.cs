namespace Cortado.Core;

public sealed class SessionController(IStateStore store, IAwakeProcessFactory factory,
    TimeProvider clock, Action<Exception> reportError, IExternalSessionMonitor? externalMonitor = null) : IDisposable
{
    private readonly Object gate = new();
    private SavedState preferences = SavedState.Empty;
    private IAwakeProcess? process;
    private ActiveSession? owned;
    private IReadOnlyList<ExternalCaffeinateSession> observed = [];
    private Int64 revision;
    private Int64 startedAt;
    private TimeSpan? initialRemaining;
    private Boolean initialized;
    private Boolean disposed;
    private Boolean hasObservation;
    private SavedSession? pendingRestore;
    private Boolean stateDirty;
    private String? bootIdentity;
    private String? persistenceError;
    private String? preferenceError;
    private String? operationError;
    private String? observationError;
    private DateTimeOffset? observedAt;
    private Int64 checkedTimestamp;
    private SessionSnapshot published = new(0, SavedState.Empty.PreferredMode, null, null, null) { HasObservation = false };

    public SessionSnapshot Snapshot
    {
        get
        {
            var snapshot = Volatile.Read(ref published);
            return snapshot.CanControl && clock.GetElapsedTime(snapshot.CheckedTimestamp) > TimeSpan.FromSeconds(5)
                ? snapshot with
                {
                    ObservationError = "Live process status is stale. Waiting for a new check.",
                    Error = "Live process status is stale. Waiting for a new check."
                }
                : snapshot;
        }
    }

    public Boolean NeedsRefresh => !Volatile.Read(ref initialized) ||
        clock.GetElapsedTime(Volatile.Read(ref published).CheckedTimestamp) >= TimeSpan.FromSeconds(1);

    public void Initialize()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (initialized)
            {
                return;
            }

            initialized = true;
            try
            {
                var loaded = store.Load();
                loaded.Validate();
                preferences = loaded with { SchemaVersion = 3 };
                pendingRestore = loaded.Session;
                stateDirty = loaded.SchemaVersion != 3;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                preferenceError = $"Could not load or migrate preferences. Using {preferences.PreferredMode}. {exception.Message}";
                reportError(new IOException(preferenceError, exception));
            }
            Observe();
        }
    }

    public void SetPreference(KeepAwakeMode mode)
    {
        SavePreferences(current =>
        {
            return !Enum.IsDefined(mode) ? throw new ArgumentOutOfRangeException(nameof(mode)) : (current with { PreferredMode = mode });
        });
    }

    public void SetTimeFormat(TimeDisplayFormat format)
    {
        SavePreferences(current =>
        {
            return !Enum.IsDefined(format) ? throw new ArgumentOutOfRangeException(nameof(format)) : (current with { TimeFormat = format });
        });
    }

    public void SetShowModeBadge(Boolean show) =>
        SavePreferences(current => current with { ShowModeBadge = show });

    public void StartIndefinite(Int64 expectedRevision) => Start(SessionKind.Indefinite, null, expectedRevision);

    public void StartDuration(TimeSpan duration, Int64 expectedRevision)
    {
        ValidateDuration(duration);
        Start(SessionKind.Duration, clock.GetUtcNow() + duration, expectedRevision);
    }

    public void StartUntil(DateTimeOffset deadline, Int64 expectedRevision) =>
        Start(SessionKind.Until, deadline.ToUniversalTime(), expectedRevision);

    private void Start(SessionKind kind, DateTimeOffset? deadline, Int64 expectedRevision)
    {
        lock (gate)
        {
            CheckRevision(expectedRevision);
            if (observed.Count > 0)
            {
                throw new SessionConflictException();
            }

            Replace(new(kind, preferences.PreferredMode, deadline));
        }
    }

    public void ChangeEndTime(DateTimeOffset deadline, Int64 expectedRevision)
    {
        lock (gate)
        {
            CheckRevision(expectedRevision);
            RequireTimed();
            Replace(owned! with { Kind = SessionKind.Until, Deadline = deadline.ToUniversalTime() });
        }
    }

    public void Extend(TimeSpan extra, Int64 expectedRevision)
    {
        lock (gate)
        {
            CheckRevision(expectedRevision);
            RequireTimed();
            ValidateDuration(extra);
            Replace(owned! with { Deadline = clock.GetUtcNow() + Remaining()!.Value + extra });
        }
    }

    public void Stop(Int64 expectedRevision)
    {
        lock (gate)
        {
            CheckRevision(expectedRevision);
            var targets = observed.ToArray();
            var verified = false;
            try
            {
                foreach (var target in targets)
                {
                    if (process is not null && target.SameProcess(process.Identity))
                    {
                        process.Stop();
                    }
                    else
                    {
                        externalMonitor!.Stop(target);
                    }
                }
                operationError = null;
            }
            finally
            {
                verified = Observe();
            }
            if (!verified)
            {
                throw new IOException("The stop command completed, but its process status could not be verified.");
            }

            if (observed.Any(session => targets.Any(target => target.SameProcess(session))))
            {
                throw new IOException("A caffeinate session is still running after Stop. Review its current status and try again.");
            }
        }
    }

    public void Tick()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            EnsureInitialized();
            if (!Observe() || process is null || owned is null)
            {
                return;
            }

            var remaining = Remaining();
            if (owned.Kind == SessionKind.Until && remaining is { } left &&
                Math.Abs((initialRemaining!.Value - clock.GetElapsedTime(startedAt) - left).TotalSeconds) > 2)
            {
                if (left <= TimeSpan.Zero)
                {
                    try
                    { process.Stop(); }
                    finally { Observe(); }
                }
                else
                {
                    Replace(owned);
                }
            }
        }
    }

    public void ReportFailure(Exception exception)
    {
        lock (gate)
        {
            if (operationError == exception.Message)
            {
                return;
            }

            operationError = exception.Message;
            Publish();
        }
        reportError(exception);
    }

    private void Replace(ActiveSession session)
    {
        var remaining = session.Deadline - clock.GetUtcNow();
        if (remaining is { } duration)
        {
            ValidateDuration(duration);
        }

        bootIdentity ??= factory.BootIdentity;
        var replacement = factory.Start(session.Mode, remaining);
        try
        {
            process?.Stop();
        }
        catch (Exception exception)
        {
            try
            { replacement.Stop(); }
            catch (Exception cleanupError)
            {
                throw new AggregateException("Could not replace the session or stop its replacement.", exception, cleanupError);
            }
            finally
            {
                replacement.Dispose();
                Observe();
            }
            throw;
        }
        process?.Dispose();
        process = replacement;
        owned = session;
        startedAt = clock.GetTimestamp();
        initialRemaining = remaining;
        pendingRestore = null;
        UpdateSavedSession();
        Persist();
        operationError = null;
        if (!Observe())
        {
            throw new IOException("The command completed, but its process status could not be verified.");
        }

        if (process is null)
        {
            throw new IOException("caffeinate exited before the session could be verified.");
        }
    }

    private Boolean Observe()
    {
        IReadOnlyList<ExternalCaffeinateSession> current;
        Boolean ownedHasExited;
        checkedTimestamp = clock.GetTimestamp();
        try
        {
            current = externalMonitor is not null ? externalMonitor.Read(null) :
                process is { HasExited: false } ? [process.Identity] : [];
            if (current.GroupBy(session => session.Pid).Any(group => group.Count() > 1))
            {
                throw new InvalidDataException("macOS returned conflicting process identities.");
            }

            if (pendingRestore is { } saved)
            {
                bootIdentity ??= factory.BootIdentity;
                if (saved.BootIdentity == bootIdentity && saved.TimestampFrequency == clock.TimestampFrequency &&
                    saved.StartedAt <= clock.GetTimestamp() &&
                    (externalMonitor is null || current.Any(session => session.SameProcess(saved.Identity))))
                {
                    process = factory.Attach(saved.Identity);
                    if (process is not null)
                    {
                        owned = saved.Active;
                        startedAt = saved.StartedAt;
                        initialRemaining = saved.InitialRemaining;
                        if (externalMonitor is null)
                        {
                            current = [process.Identity];
                        }
                    }
                    else
                    {
                        current = current.Where(session => !session.SameProcess(saved.Identity)).ToArray();
                    }
                }
                pendingRestore = null;
                if (process is null)
                {
                    preferences = preferences with { Session = null };
                    stateDirty = true;
                }
            }
            ownedHasExited = process?.HasExited ?? false;
        }
        catch (Exception exception)
        {
            var changed = observationError != exception.Message;
            observationError = exception.Message;
            Publish();
            if (changed)
            {
                reportError(exception);
            }

            return false;
        }

        var currentArray = current.Where(session => !ownedHasExited || process is null || !session.SameProcess(process.Identity))
            .OrderBy(session => session.Pid).ToArray();
        current = currentArray;
        if (process is not null && (ownedHasExited || !current.Any(session => session.SameProcess(process.Identity))))
        {
            process.Dispose();
            process = null;
            owned = null;
            initialRemaining = null;
            preferences = preferences with { Session = null };
            stateDirty = true;
        }
        if (!observed.SequenceEqual(current))
        {
            revision++;
        }

        observed = Array.AsReadOnly(currentArray);
        hasObservation = true;
        observationError = null;
        observedAt = clock.GetUtcNow();
        Publish();
        UpdateSavedSession();
        Persist();
        return true;
    }

    private void UpdateSavedSession()
    {
        if (process is null || owned is null)
        {
            return;
        }

        bootIdentity ??= factory.BootIdentity;
        var saved = new SavedSession(process.Identity, bootIdentity, owned, startedAt,
            clock.TimestampFrequency, initialRemaining);
        if (preferences.Session != saved)
        {
            preferences = preferences with { Session = saved };
            stateDirty = true;
        }
    }

    private void SavePreferences(Func<SavedState, SavedState> update)
    {
        lock (gate)
        {
            EnsureInitialized();
            var updated = update(preferences);
            store.Save(updated);
            preferences = updated;
            stateDirty = false;
            persistenceError = null;
            preferenceError = null;
            operationError = null;
            Publish();
        }
    }

    private void Persist()
    {
        if (!stateDirty)
        {
            return;
        }

        try
        {
            store.Save(preferences);
            stateDirty = false;
            persistenceError = null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            var message = $"Could not save session state. Restart recovery is not guaranteed. {exception.Message}";
            if (persistenceError != message)
            {
                reportError(new IOException(message, exception));
            }

            persistenceError = message;
        }
        Publish();
    }

    private void Publish()
    {
        var remaining = Remaining();
        if (owned?.Kind == SessionKind.Duration && owned.Deadline is { } deadline && remaining is { } left &&
            Math.Abs((deadline - clock.GetUtcNow() - left).TotalSeconds) > 2)
        {
            owned = owned with { Deadline = clock.GetUtcNow() + left };
        }

        var active = owned;
        if (active?.Deadline is not null && remaining < -SessionSnapshot.ExpirationGrace)
        {
            active = active with { Kind = SessionKind.Unknown, Deadline = null };
        }

        var previous = Volatile.Read(ref published);
        var external = observed.Where(session => owned is null || process is null || !session.SameProcess(process.Identity)).ToArray();
        var next = new SessionSnapshot(revision, preferences.PreferredMode, active, remaining,
            observationError ?? preferenceError ?? persistenceError ?? operationError)
        {
            ExternalSessions = Array.AsReadOnly(external),
            ExternalRemaining = external.Length > 0 && external.All(session => session.Deadline is not null)
                ? external.Max(session => session.Deadline!.Value) - clock.GetUtcNow() : null,
            TimeFormat = preferences.TimeFormat,
            ShowModeBadge = preferences.ShowModeBadge,
            HasObservation = hasObservation,
            ObservationError = observationError,
            ObservedAt = observedAt,
            CheckedTimestamp = checkedTimestamp
        };
        if (previous.HasObservation && (previous.Active != next.Active ||
            previous.CanControl != next.CanControl || previous.CanEdit != next.CanEdit))
        {
            next = next with { Revision = ++revision };
        }

        Volatile.Write(ref published, next);
    }

    private TimeSpan? Remaining() => owned switch
    {
        { Kind: SessionKind.Duration } when initialRemaining is { } initial => initial - clock.GetElapsedTime(startedAt),
        { Deadline: { } deadline } => deadline - clock.GetUtcNow(),
        _ => null
    };

    private void CheckRevision(Int64 expectedRevision)
    {
        EnsureInitialized();
        if (!Observe())
        {
            throw new IOException("Could not verify the live session. Check the reported process-inspection error.");
        }

        if (revision != expectedRevision)
        {
            throw new SessionConflictException();
        }
    }

    private void EnsureInitialized()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!initialized)
        {
            throw new InvalidOperationException("Initialize Cortado's live session coordinator first.");
        }
    }

    private void RequireTimed()
    {
        if (owned?.Deadline is null || Remaining() <= TimeSpan.Zero ||
            observed.Any(session => process is null || !session.SameProcess(process.Identity)))
        {
            throw new InvalidOperationException("Only a verified, solely Cortado-owned timed session can be edited.");
        }
    }

    private static void ValidateDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || Math.Ceiling(duration.TotalSeconds) > Int32.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Choose a positive duration within caffeinate's supported range.");
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            try
            {
                if (initialized)
                {
                    Observe();
                    Persist();
                }
            }
            finally
            {
                try
                { process?.Dispose(); }
                finally { process = null; disposed = true; }
            }
        }
    }
}
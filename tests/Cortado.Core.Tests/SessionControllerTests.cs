using Cortado.Core;

namespace Cortado.Core.Tests;

[TestFixture]
public sealed class SessionControllerTests
{
    private TestClock clock = null!;
    private MemoryStore store = null!;
    private FakeFactory factory = null!;
    private FakeExternalMonitor monitor = null!;
    private SessionController controller = null!;
    private List<Exception> errors = null!;

    [SetUp]
    public void SetUp()
    {
        clock = new();
        store = new();
        monitor = new();
        factory = new(monitor);
        errors = [];
        controller = new(store, factory, clock, errors.Add, monitor);
        controller.Initialize();
    }

    [TearDown]
    public void TearDown() => controller.Dispose();

    [Test]
    public void BeforeInitialization_DoesNotClaimToBeOff()
    {
        using var uninitialized = new SessionController(store, factory, clock, errors.Add, monitor);
        Assert.That(uninitialized.Snapshot.Status, Is.EqualTo("Checking"));
        Assert.That(uninitialized.Snapshot.CanControl, Is.False);
    }

    [Test]
    public void StartIndefinite_UsesMacAndDisplayByDefault()
    {
        controller.StartIndefinite(controller.Snapshot.Revision);
        Assert.That(controller.Snapshot.Active?.Mode, Is.EqualTo(KeepAwakeMode.MacAndDisplay));
    }

    [Test]
    public void StartIndefinite_VerifiesAndSavesSessionIdentity()
    {
        controller.StartIndefinite(controller.Snapshot.Revision);
        Assert.Multiple(() =>
        {
            Assert.That(factory.Started.Single().Remaining, Is.Null);
            Assert.That(controller.Snapshot.Status, Is.EqualTo("Indefinitely"));
            Assert.That(store.State.Session?.Identity, Is.EqualTo(factory.Started.Single().Process.Identity));
            Assert.That(store.State.Session?.Active, Is.EqualTo(controller.Snapshot.Active));
            Assert.That(store.Saves, Is.EqualTo(1));
        });
    }

    [Test]
    public void Preference_ChangesOnlyFutureSessionsAndPreservesSavedOwnership()
    {
        controller.SetPreference(KeepAwakeMode.MacOnly);
        controller.StartIndefinite(0);
        controller.SetPreference(KeepAwakeMode.MacAndDisplay);
        Assert.Multiple(() =>
        {
            Assert.That(controller.Snapshot.Active?.Mode, Is.EqualTo(KeepAwakeMode.MacOnly));
            Assert.That(controller.Snapshot.PreferredMode, Is.EqualTo(KeepAwakeMode.MacAndDisplay));
            Assert.That(store.State.PreferredMode, Is.EqualTo(KeepAwakeMode.MacAndDisplay));
            Assert.That(store.State.Session?.Active.Mode, Is.EqualTo(KeepAwakeMode.MacOnly));
            Assert.That(store.Saves, Is.EqualTo(3));
        });
    }

    [Test]
    public void ExpiredMetadata_WithLiveProcess_ShowsUnknownRatherThanOff()
    {
        controller.StartDuration(TimeSpan.FromMinutes(5), 0);
        clock.Advance(TimeSpan.FromMinutes(6));
        controller.Tick();
        Assert.That(controller.Snapshot.Status, Is.EqualTo("Unknown"));
        Assert.That(controller.Snapshot.IsActive, Is.True);
        Assert.That(controller.Snapshot.CanEdit, Is.False);
        Assert.That(controller.Snapshot.IsExpiring, Is.False);
        Assert.That(factory.Started.Single().Process.HasExited, Is.False);
    }

    [Test]
    public void DeadlineBoundary_ShowsZeroUntilVerifiedExitWithoutFlashingUnknown()
    {
        controller.StartDuration(TimeSpan.FromSeconds(5), controller.Snapshot.Revision);
        clock.Advance(TimeSpan.FromMilliseconds(5100));
        controller.Tick();
        Assert.That(controller.Snapshot.Status, Is.EqualTo("0:00"));
        Assert.That(controller.Snapshot.IsActive, Is.True);
        Assert.That(controller.Snapshot.CanEdit, Is.False);
        Assert.That(controller.Snapshot.IsExpiring, Is.True);
        factory.Started.Single().Process.HasExited = true;
        clock.Advance(TimeSpan.FromMilliseconds(100));
        controller.Tick();
        Assert.That(controller.Snapshot.Status, Is.EqualTo("Off"));
    }

    [Test]
    public void DeadlineBoundary_StillAliveBeyondTheGracePeriod_ShowsUnknown()
    {
        controller.StartDuration(TimeSpan.FromSeconds(5), controller.Snapshot.Revision);
        clock.Advance(TimeSpan.FromSeconds(8));
        controller.Tick();
        Assert.That(controller.Snapshot.Status, Is.EqualTo("Unknown"));
        Assert.That(controller.Snapshot.IsActive, Is.True);
        Assert.That(controller.Snapshot.IsExpiring, Is.False);
    }

    [Test]
    public void ConfirmedExit_ShowsOffAndClearsSavedSessionDespitePriorCommandError()
    {
        controller.StartDuration(TimeSpan.FromMinutes(5), 0);
        controller.ReportFailure(new IOException("An earlier command failed."));
        factory.Started.Single().Process.HasExited = true;
        controller.Tick();
        Assert.That(controller.Snapshot.Status, Is.EqualTo("Off"));
        Assert.That(controller.Snapshot.Active, Is.Null);
        Assert.That(store.Saves, Is.EqualTo(2));
        Assert.That(store.State.Session, Is.Null);
    }

    [Test]
    public void Extend_PreservesActualModeAndAddsToObservedRemaining()
    {
        controller.SetPreference(KeepAwakeMode.MacOnly);
        controller.StartDuration(TimeSpan.FromMinutes(5), 0);
        controller.SetPreference(KeepAwakeMode.MacAndDisplay);
        clock.Advance(TimeSpan.FromMinutes(2));
        controller.Extend(TimeSpan.FromMinutes(5), controller.Snapshot.Revision);
        Assert.That(controller.Snapshot.Remaining, Is.EqualTo(TimeSpan.FromMinutes(8)));
        Assert.That(controller.Snapshot.Active?.Mode, Is.EqualTo(KeepAwakeMode.MacOnly));
        Assert.That(factory.Started[0].Process.HasExited, Is.True);
    }

    [Test]
    public void ChangeEndTime_FromDuration_UsesNewVerifiedProcess()
    {
        controller.SetPreference(KeepAwakeMode.MacOnly);
        controller.StartDuration(TimeSpan.FromMinutes(5), 0);
        var end = clock.GetUtcNow().AddHours(2);
        controller.ChangeEndTime(end, controller.Snapshot.Revision);
        Assert.That(controller.Snapshot.Active, Is.EqualTo(new ActiveSession(SessionKind.Until, KeepAwakeMode.MacOnly, end)));
        Assert.That(store.Saves, Is.EqualTo(3));
        Assert.That(store.State.Session?.Active, Is.EqualTo(controller.Snapshot.Active));
    }

    [Test]
    public void Extend_Indefinite_RejectsWithoutRestarting()
    {
        controller.StartIndefinite(0);
        Assert.Throws<InvalidOperationException>(() => controller.Extend(TimeSpan.FromMinutes(5), controller.Snapshot.Revision));
        Assert.That(factory.Started, Has.Count.EqualTo(1));
    }

    [Test]
    public void StaleStop_DoesNotStopTheNewSession()
    {
        controller.StartIndefinite(0);
        Assert.Throws<SessionConflictException>(() => controller.Stop(0));
        Assert.That(factory.Started.Single().Process.HasExited, Is.False);
    }

    [Test]
    public void Start_WhileActive_Rejects()
    {
        controller.StartIndefinite(0);
        Assert.Throws<SessionConflictException>(() => controller.StartIndefinite(controller.Snapshot.Revision));
    }

    [Test]
    public void ReplacementLaunchFailure_KeepsPreviousLiveSession()
    {
        controller.StartDuration(TimeSpan.FromMinutes(5), 0);
        var previous = controller.Snapshot.Active;
        factory.FailNextStart = true;
        Assert.Throws<IOException>(() => controller.ChangeEndTime(clock.GetUtcNow().AddHours(1), controller.Snapshot.Revision));
        Assert.That(factory.Started.Single().Process.HasExited, Is.False);
        Assert.That(controller.Snapshot.Active, Is.EqualTo(previous));
    }

    [Test]
    public void PreferenceWriteFailure_CannotBlockStartExtendOrStop()
    {
        store.FailNextSave = true;
        Assert.Throws<IOException>(() => controller.SetPreference(KeepAwakeMode.MacAndDisplay));
        controller.StartDuration(TimeSpan.FromMinutes(5), controller.Snapshot.Revision);
        controller.Extend(TimeSpan.FromMinutes(5), controller.Snapshot.Revision);
        controller.Stop(controller.Snapshot.Revision);
        Assert.That(controller.Snapshot.Status, Is.EqualTo("Off"));
        Assert.That(store.Saves, Is.EqualTo(4));
    }

    [Test]
    public void UnreadablePreferences_StillDiscoversAndStopsExternalSessions()
    {
        store.FailLoad = true;
        monitor.Sessions.Add(new(123, 100, 0, null));
        using var fresh = new SessionController(store, factory, clock, errors.Add, monitor);
        fresh.Initialize();
        Assert.That(fresh.Snapshot.Status, Is.EqualTo("Unknown"));
        Assert.That(fresh.Snapshot.Error, Does.Contain("preferences"));
        Assert.That(errors, Is.Not.Empty);
        fresh.Stop(fresh.Snapshot.Revision);
        Assert.That(fresh.Snapshot.Status, Is.EqualTo("Off"));
    }

    [Test]
    public void LegacyPreferences_MigrateWithoutRestoringAnySession()
    {
        store.State = new(1, KeepAwakeMode.MacAndDisplay);
        using var fresh = new SessionController(store, factory, clock, errors.Add, monitor);
        fresh.Initialize();
        Assert.That(factory.Started, Is.Empty);
        Assert.That(fresh.Snapshot.Status, Is.EqualTo("Off"));
        Assert.That(store.State, Is.EqualTo(new SavedState(3, KeepAwakeMode.MacAndDisplay)));
    }

    [Test]
    public void FailedMigration_PreservesReadPreferenceAndLiveStatus()
    {
        store.State = new(1, KeepAwakeMode.MacAndDisplay);
        store.FailNextSave = true;
        using var fresh = new SessionController(store, factory, clock, errors.Add, monitor);
        fresh.Initialize();
        Assert.That(fresh.Snapshot.PreferredMode, Is.EqualTo(KeepAwakeMode.MacAndDisplay));
        Assert.That(fresh.Snapshot.Status, Is.EqualTo("Off"));
        Assert.That(fresh.Snapshot.Error, Is.Not.Null);
    }

    [Test]
    public void InvalidPreferenceSchema_IsReportedButCannotClaimAnActiveSession()
    {
        store.State = new(99, KeepAwakeMode.MacAndDisplay);
        using var fresh = new SessionController(store, factory, clock, errors.Add, monitor);
        fresh.Initialize();
        Assert.That(fresh.Snapshot.Status, Is.EqualTo("Off"));
        Assert.That(errors, Is.Not.Empty);
        Assert.That(factory.Started, Is.Empty);
    }

    [Test]
    public void Duration_WallClockJump_PreservesMonotonicRemainingWithoutRecreatingProcess()
    {
        controller.StartDuration(TimeSpan.FromMinutes(10), 0);
        clock.Advance(TimeSpan.FromMinutes(2));
        clock.Jump(TimeSpan.FromHours(1));
        controller.Tick();
        Assert.That(controller.Snapshot.Remaining, Is.EqualTo(TimeSpan.FromMinutes(8)));
        Assert.That(controller.Snapshot.Active?.Deadline, Is.EqualTo(clock.GetUtcNow().AddMinutes(8)));
        Assert.That(factory.Started, Has.Count.EqualTo(1));
    }

    [Test]
    public void Until_WallClockJump_ReconcilesTheOwnedProcessTimeout()
    {
        controller.StartUntil(clock.GetUtcNow().AddHours(2), 0);
        clock.Jump(TimeSpan.FromHours(1));
        controller.Tick();
        Assert.That(factory.Started.Last().Remaining, Is.EqualTo(TimeSpan.FromHours(1)));
        Assert.That(factory.Started, Has.Count.EqualTo(2));
    }

    [Test]
    public void UnloadAndRestart_RestoresOwnedProcessWithoutLaunchingAnother()
    {
        controller.StartIndefinite(0);
        controller.Dispose();
        using var fresh = new SessionController(store, factory, clock, errors.Add, monitor);
        fresh.Initialize();
        Assert.That(factory.Started, Has.Count.EqualTo(1));
        Assert.That(factory.Started.Single().Process.HasExited, Is.False);
        Assert.That(factory.Started.Single().Process.Disposed, Is.True);
        Assert.That(fresh.Snapshot.Status, Is.EqualTo("Indefinitely"));
        Assert.That(store.Saves, Is.EqualTo(1));
        fresh.Stop(fresh.Snapshot.Revision);
        Assert.That(factory.Started.Single().Process.HasExited, Is.True);
    }

    [Test]
    public void LostOwnershipMetadata_AdoptsLiveProcessAsUnknown()
    {
        controller.StartIndefinite(0);
        store.State = store.State with { Session = null };
        using var otherCoordinator = new SessionController(store, factory, clock, errors.Add, monitor);
        otherCoordinator.Initialize();
        Assert.That(otherCoordinator.Snapshot.Status, Is.EqualTo("Unknown"));
        Assert.That(factory.Started, Has.Count.EqualTo(1));
    }

    [Test]
    public void ReusedPid_DiscardsOwnedTimingWithoutStoppingReplacement()
    {
        controller.StartDuration(TimeSpan.FromMinutes(5), 0);
        var previous = factory.Started.Single().Process;
        monitor.Sessions.Add(previous.Identity with { StartSeconds = 999 });
        controller.Tick();
        Assert.That(controller.Snapshot.Active, Is.Null);
        Assert.That(controller.Snapshot.Status, Is.EqualTo("Unknown"));
        Assert.That(previous.Disposed, Is.True);
        Assert.That(previous.HasExited, Is.False);
    }

    [Test]
    public void ObservationFailureAndRecovery_ClearInspectionErrorWithoutInventingOff()
    {
        monitor.Sessions.Add(new(123, 100, 0, null));
        controller.Tick();
        monitor.FailRead = true;
        controller.Tick();
        Assert.That(controller.Snapshot.Status, Is.EqualTo("Error"));
        Assert.That(controller.Snapshot.CanControl, Is.False);
        monitor.FailRead = false;
        controller.Tick();
        Assert.That(controller.Snapshot.Status, Is.EqualTo("Unknown"));
        Assert.That(controller.Snapshot.Error, Is.Null);
    }

    [Test]
    public void StaleObservation_IsNotPresentedAsFreshOff()
    {
        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.That(controller.Snapshot.Status, Is.EqualTo("Error"));
        controller.Tick();
        Assert.That(controller.Snapshot.Status, Is.EqualTo("Off"));
    }

    [Test]
    public void InitialInspectionFailure_DoesNotInventOffOrConfirmedActivity()
    {
        monitor.FailRead = true;
        using var fresh = new SessionController(store, factory, clock, errors.Add, monitor);
        fresh.Initialize();
        Assert.That(fresh.Snapshot.Status, Is.EqualTo("Error"));
        Assert.That(fresh.Snapshot.HasObservation, Is.False);
        Assert.That(fresh.Snapshot.CanControl, Is.False);
        monitor.FailRead = false;
        fresh.Tick();
        Assert.That(fresh.Snapshot.Status, Is.EqualTo("Off"));
    }

    [Test]
    public void FailedStartVerification_ReportsAnErrorAndRecoversWithoutAnotherLaunch()
    {
        factory.AfterStart = _ => monitor.FailRead = true;
        Assert.Throws<IOException>(() => controller.StartIndefinite(controller.Snapshot.Revision));
        Assert.That(controller.Snapshot.Status, Is.EqualTo("Error"));
        Assert.That(controller.Snapshot.CanControl, Is.False);
        monitor.FailRead = false;
        controller.Tick();
        Assert.That(controller.Snapshot.Status, Is.EqualTo("Indefinitely"));
        Assert.That(factory.Started, Has.Count.EqualTo(1));
    }

    [Test]
    public void ImmediateProcessExit_DoesNotPublishAStartedSession()
    {
        factory.AfterStart = process => process.HasExited = true;
        Assert.Throws<IOException>(() => controller.StartIndefinite(controller.Snapshot.Revision));
        Assert.That(controller.Snapshot.Status, Is.EqualTo("Off"));
        Assert.That(factory.Started.Single().Process.Disposed, Is.True);
    }

    [Test]
    public void Stop_WhenProcessRemainsAlive_ReportsFailureRatherThanSuccess()
    {
        controller.StartIndefinite(controller.Snapshot.Revision);
        var process = factory.Started.Single().Process;
        process.IgnoreStop = true;
        try
        {
            Assert.Throws<IOException>(() => controller.Stop(controller.Snapshot.Revision));
            Assert.That(controller.Snapshot.Status, Is.EqualTo("Indefinitely"));
        }
        finally { process.IgnoreStop = false; }
    }

    [Test]
    public void Dispose_PreservesVerifiedSessionWithoutRedundantSave()
    {
        controller.StartIndefinite(controller.Snapshot.Revision);
        controller.Dispose();
        Assert.That(controller.Snapshot.Status, Is.EqualTo("Indefinitely"));
        Assert.That(controller.Snapshot.Active, Is.Not.Null);
        Assert.That(store.Saves, Is.EqualTo(1));
        Assert.That(factory.Started.Single().Process.HasExited, Is.False);
    }

    [Test]
    public void ExpiredLiveMetadata_InvalidatesAnOpenEditDraft()
    {
        controller.StartDuration(TimeSpan.FromMinutes(5), controller.Snapshot.Revision);
        var menu = new CortadoMenu(controller, clock, () => TimeZoneInfo.Utc);
        menu.Open();
        menu.Execute(menu.Items().Single(item => item.Label == "Change\nend time").Parameter);
        var staleConfirmation = menu.Items().Single(item => item.Label == "Save").Parameter;
        clock.Advance(TimeSpan.FromMinutes(6));
        controller.Tick();
        Assert.That(menu.Synchronize(), Is.True);
        Assert.That(menu.View, Is.EqualTo(MenuView.Root));
        Assert.That(menu.Items().Select(item => item.Label), Does.Contain("Stop").And.Not.Contain("Change\nend time"));
        Assert.Throws<SessionConflictException>(() => menu.Execute(staleConfirmation));
    }

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(2147483648d)]
    public void InvalidDuration_DoesNotLaunch(Double seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => controller.StartDuration(TimeSpan.FromSeconds(seconds), 0));
        Assert.That(factory.Started, Is.Empty);
    }

    [Test]
    public void ConcurrentStarts_CreateOnlyOneProcess()
    {
        var successes = 0;
        Parallel.For(0, 10, _ =>
        {
            try
            { controller.StartIndefinite(0); Interlocked.Increment(ref successes); }
            catch (SessionConflictException) { }
        });
        Assert.That(successes, Is.EqualTo(1));
        Assert.That(factory.Started, Has.Count.EqualTo(1));
    }
}
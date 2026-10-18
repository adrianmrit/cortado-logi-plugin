using Cortado.Core;

namespace Cortado.Core.Tests;

[TestFixture]
public sealed class SessionRecoveryTests
{
    private TestClock clock = null!;
    private MemoryStore store = null!;
    private FakeExternalMonitor monitor = null!;
    private FakeFactory factory = null!;
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
        controller = CreateController();
    }

    [TearDown]
    public void TearDown() => controller.Dispose();

    private SessionController CreateController()
    {
        var result = new SessionController(store, factory, clock, errors.Add, monitor);
        result.Initialize();
        return result;
    }

    private void Restart()
    {
        controller.Dispose();
        controller = CreateController();
    }

    [TestCase(SessionKind.Indefinite)]
    [TestCase(SessionKind.Duration)]
    [TestCase(SessionKind.Until)]
    public void Restart_RestoresSessionDetailsAndControlsWithoutLaunching(SessionKind kind)
    {
        controller.SetPreference(KeepAwakeMode.MacAndDisplay);
        if (kind == SessionKind.Indefinite)
        {
            controller.StartIndefinite(controller.Snapshot.Revision);
        }
        else if (kind == SessionKind.Duration)
        {
            controller.StartDuration(TimeSpan.FromMinutes(10), controller.Snapshot.Revision);
        }
        else
        {
            controller.StartUntil(clock.GetUtcNow().AddMinutes(10), controller.Snapshot.Revision);
        }

        controller.SetPreference(KeepAwakeMode.MacOnly);
        var original = controller.Snapshot.Active;
        controller.Dispose();
        clock.Advance(TimeSpan.FromMinutes(3));
        controller = CreateController();
        Assert.Multiple(() =>
        {
            Assert.That(controller.Snapshot.Active, Is.EqualTo(original));
            Assert.That(controller.Snapshot.PreferredMode, Is.EqualTo(KeepAwakeMode.MacOnly));
            Assert.That(controller.Snapshot.ExternalSessions, Is.Empty);
            Assert.That(controller.Snapshot.Remaining, Is.EqualTo(kind == SessionKind.Indefinite ? null : TimeSpan.FromMinutes(7)));
            Assert.That(controller.Snapshot.CanEdit, Is.EqualTo(kind != SessionKind.Indefinite));
            Assert.That(factory.Started, Has.Count.EqualTo(1));
        });
        controller.Stop(controller.Snapshot.Revision);
        Assert.That(store.State.Session, Is.Null);
        Assert.That(factory.Started.Single().Process.HasExited, Is.True);
    }

    [Test]
    public void Restart_RepeatedReloads_PreserveIdentityWithoutRewritingOrLaunching()
    {
        controller.StartIndefinite(controller.Snapshot.Revision);
        var saved = store.State;
        for (var index = 0; index < 3; index++)
        {
            Restart();
        }

        Assert.That(store.State, Is.EqualTo(saved));
        Assert.That(store.Saves, Is.EqualTo(1));
        Assert.That(factory.Started, Has.Count.EqualTo(1));
        Assert.That(controller.Snapshot.Status, Is.EqualTo("Indefinitely"));
    }

    [Test]
    public void Restart_DurationWithWallClockJump_UsesContinuousElapsedTime()
    {
        controller.StartDuration(TimeSpan.FromMinutes(10), controller.Snapshot.Revision);
        controller.Dispose();
        clock.Advance(TimeSpan.FromMinutes(2));
        clock.Jump(TimeSpan.FromDays(1));
        controller = CreateController();
        Assert.That(controller.Snapshot.Remaining, Is.EqualTo(TimeSpan.FromMinutes(8)));
        Assert.That(controller.Snapshot.Active?.Deadline, Is.EqualTo(clock.GetUtcNow().AddMinutes(8)));
        Assert.That(store.State.Session?.Active, Is.EqualTo(controller.Snapshot.Active));
        Assert.That(factory.Started, Has.Count.EqualTo(1));
        controller.Extend(TimeSpan.FromMinutes(5), controller.Snapshot.Revision);
        Restart();
        Assert.That(controller.Snapshot.Remaining, Is.EqualTo(TimeSpan.FromMinutes(13)));
        Assert.That(factory.Started, Has.Count.EqualTo(2));
    }

    [TestCase(60)]
    [TestCase(-60)]
    [TestCase(180)]
    public void Restart_UntilWithWallClockJump_ReconcilesNativeTimeout(Int32 minutes)
    {
        controller.StartUntil(clock.GetUtcNow().AddHours(2), controller.Snapshot.Revision);
        controller.Dispose();
        clock.Jump(TimeSpan.FromMinutes(minutes));
        controller = CreateController();
        controller.Tick();
        if (minutes >= 120)
        {
            Assert.That(controller.Snapshot.Status, Is.EqualTo("Off"));
            Assert.That(store.State.Session, Is.Null);
        }
        else
        {
            Assert.That(factory.Started.Last().Remaining, Is.EqualTo(TimeSpan.FromMinutes(120 - minutes)));
            Assert.That(store.State.Session?.Active.Kind, Is.EqualTo(SessionKind.Until));
        }
    }

    [Test]
    public void Restart_ElapsedDeadlineButLiveProcess_DoesNotInventExit()
    {
        controller.StartDuration(TimeSpan.FromMinutes(1), controller.Snapshot.Revision);
        controller.Dispose();
        clock.Advance(TimeSpan.FromMinutes(2));
        controller = CreateController();
        Assert.That(controller.Snapshot.Status, Is.EqualTo("Unknown"));
        Assert.That(controller.Snapshot.IsActive, Is.True);
        Assert.That(controller.Snapshot.CanEdit, Is.False);
        Assert.That(store.State.Session, Is.Not.Null);
    }

    [Test]
    public void Restart_ProcessExpiredWhileUnloaded_ClearsSavedOwnership()
    {
        controller.StartDuration(TimeSpan.FromMinutes(1), controller.Snapshot.Revision);
        controller.Dispose();
        factory.Started.Single().Process.HasExited = true;
        clock.Advance(TimeSpan.FromMinutes(2));
        controller = CreateController();
        Assert.That(controller.Snapshot.Status, Is.EqualTo("Off"));
        Assert.That(store.State.Session, Is.Null);
        Assert.That(factory.Started, Has.Count.EqualTo(1));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Restart_StaleIdentity_DoesNotAdoptOrStopReplacement(Boolean samePid)
    {
        controller.StartDuration(TimeSpan.FromMinutes(5), controller.Snapshot.Revision);
        controller.Dispose();
        var previous = factory.Started.Single().Process;
        previous.HasExited = true;
        var replacement = previous.Identity with { Pid = samePid ? previous.Id : 2345, StartMicroseconds = 999 };
        monitor.Sessions.Add(replacement);
        controller = CreateController();
        Assert.That(controller.Snapshot.Active, Is.Null);
        Assert.That(controller.Snapshot.ExternalSessions, Is.EqualTo(new[] { replacement }));
        Assert.That(store.State.Session, Is.Null);
        Assert.That(monitor.Stopped, Is.Empty);
        Assert.That(factory.Started, Has.Count.EqualTo(1));
    }

    [TestCase("boot")]
    [TestCase("frequency")]
    [TestCase("timestamp")]
    public void Restart_IncompatibleTimingIdentity_DiscardsOwnership(String mismatch)
    {
        controller.StartDuration(TimeSpan.FromMinutes(5), controller.Snapshot.Revision);
        controller.Dispose();
        var saved = store.State.Session!;
        store.State = store.State with
        {
            Session = mismatch switch
            {
                "boot" => saved with { BootIdentity = "previous-boot" },
                "frequency" => saved with { TimestampFrequency = 1 },
                _ => saved with { StartedAt = clock.GetTimestamp() + 1 }
            }
        };
        controller = CreateController();
        Assert.That(controller.Snapshot.Active, Is.Null);
        Assert.That(controller.Snapshot.ExternalSessions, Has.Count.EqualTo(1));
        Assert.That(store.State.Session, Is.Null);
        Assert.That(factory.Started.Single().Process.HasExited, Is.False);
        Assert.That(monitor.Stopped, Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Restart_InspectionOrAttachmentFailure_RetainsMetadataAndRetries(Boolean attach)
    {
        controller.StartIndefinite(controller.Snapshot.Revision);
        controller.Dispose();
        var saved = store.State;
        monitor.FailRead = !attach;
        factory.FailAttach = attach;
        controller = CreateController();
        Assert.That(controller.Snapshot.Status, Is.EqualTo("Error"));
        Assert.That(controller.Snapshot.Active, Is.Null);
        Assert.That(store.State, Is.EqualTo(saved));
        Assert.That(errors, Has.Count.EqualTo(1));
        monitor.FailRead = false;
        factory.FailAttach = false;
        controller.Tick();
        Assert.That(controller.Snapshot.Status, Is.EqualTo("Indefinitely"));
        Assert.That(controller.Snapshot.Error, Is.Null);
        Assert.That(factory.Started, Has.Count.EqualTo(1));
    }

    [Test]
    public void Restart_ProcessExitsDuringAttachment_DoesNotLaunchReplacement()
    {
        controller.StartIndefinite(controller.Snapshot.Revision);
        controller.Dispose();
        factory.BeforeAttach = () => factory.Started.Single().Process.HasExited = true;
        controller = CreateController();
        Assert.That(controller.Snapshot.Status, Is.EqualTo("Off"));
        Assert.That(store.State.Session, Is.Null);
        Assert.That(factory.Started, Has.Count.EqualTo(1));
    }

    [Test]
    public void Restart_UnverifiedStart_RestoresFromSavedIdentityAfterInspectionRecovers()
    {
        factory.AfterStart = _ => monitor.FailRead = true;
        Assert.Throws<IOException>(() => controller.StartDuration(TimeSpan.FromMinutes(5), controller.Snapshot.Revision));
        Assert.That(store.State.Session, Is.Not.Null);
        controller.Dispose();
        clock.Advance(TimeSpan.FromMinutes(1));
        monitor.FailRead = false;
        controller = CreateController();
        Assert.That(controller.Snapshot.Remaining, Is.EqualTo(TimeSpan.FromMinutes(4)));
        Assert.That(controller.Snapshot.CanEdit, Is.True);
        Assert.That(factory.Started, Has.Count.EqualTo(1));
    }

    [Test]
    public void Restart_WithoutExternalMonitor_StillVerifiesAndRestoresOwnProcess()
    {
        using var original = new SessionController(store, factory, clock, errors.Add);
        original.Initialize();
        original.StartIndefinite(original.Snapshot.Revision);
        original.Dispose();
        using var restored = new SessionController(store, factory, clock, errors.Add);
        restored.Initialize();
        Assert.That(restored.Snapshot.Status, Is.EqualTo("Indefinitely"));
        Assert.That(restored.Snapshot.ExternalSessions, Is.Empty);
        restored.Stop(restored.Snapshot.Revision);
        Assert.That(store.State.Session, Is.Null);
    }

    [Test]
    public void Restart_ExternalAlongsideRestoredSession_PreservesExternalRules()
    {
        controller.StartDuration(TimeSpan.FromMinutes(10), controller.Snapshot.Revision);
        monitor.Sessions.Add(new(2345, 100, 200, null));
        Restart();
        Assert.That(controller.Snapshot.Active?.Kind, Is.EqualTo(SessionKind.Duration));
        Assert.That(controller.Snapshot.ExternalSessions, Has.Count.EqualTo(1));
        Assert.That(controller.Snapshot.CanEdit, Is.False);
        Assert.Throws<InvalidOperationException>(() => controller.Extend(TimeSpan.FromMinutes(5), controller.Snapshot.Revision));
    }

    [Test]
    public void Tick_UnchangedCountdown_DoesNotRewriteSavedState()
    {
        controller.StartDuration(TimeSpan.FromMinutes(5), controller.Snapshot.Revision);
        for (var index = 0; index < 10; index++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            controller.Tick();
        }
        Assert.That(store.Saves, Is.EqualTo(1));
        Assert.That(store.State.Session?.StartedAt, Is.Zero);
    }

    [Test]
    public void Start_SaveFailure_ReportsTruthfulLiveStatusAndRetries()
    {
        store.FailSaves = true;
        controller.StartIndefinite(controller.Snapshot.Revision);
        controller.Tick();
        Assert.That(controller.Snapshot.Status, Is.EqualTo("Indefinitely"));
        Assert.That(controller.Snapshot.Error, Does.Contain("Restart recovery is not guaranteed"));
        Assert.That(store.State.Session, Is.Null);
        Assert.That(errors, Has.Count.EqualTo(1));
        store.FailSaves = false;
        controller.Tick();
        Assert.That(store.State.Session?.Active, Is.EqualTo(controller.Snapshot.Active));
        Assert.That(controller.Snapshot.Error, Is.Null);
        Restart();
        Assert.That(controller.Snapshot.Status, Is.EqualTo("Indefinitely"));
    }

    [Test]
    public void Stop_SaveFailure_DoesNotBlockProcessStopAndRetriesClearingState()
    {
        controller.StartIndefinite(controller.Snapshot.Revision);
        store.FailSaves = true;
        controller.Stop(controller.Snapshot.Revision);
        Assert.That(controller.Snapshot.Status, Is.EqualTo("Off"));
        Assert.That(controller.Snapshot.Error, Is.Not.Null);
        Assert.That(factory.Started.Single().Process.HasExited, Is.True);
        Assert.That(store.State.Session, Is.Not.Null);
        store.FailSaves = false;
        controller.Tick();
        Assert.That(store.State.Session, Is.Null);
        Assert.That(controller.Snapshot.Error, Is.Null);
    }

    [Test]
    public void Replace_SaveFailure_RestoresOnlyTheNewProcessAfterRetry()
    {
        controller.StartDuration(TimeSpan.FromMinutes(5), controller.Snapshot.Revision);
        store.FailSaves = true;
        controller.ChangeEndTime(clock.GetUtcNow().AddHours(1), controller.Snapshot.Revision);
        Assert.That(factory.Started[0].Process.HasExited, Is.True);
        Assert.That(controller.Snapshot.Error, Is.Not.Null);
        store.FailSaves = false;
        controller.Tick();
        Restart();
        Assert.That(controller.Snapshot.Active?.Kind, Is.EqualTo(SessionKind.Until));
        Assert.That(store.State.Session?.Identity, Is.EqualTo(factory.Started.Last().Process.Identity));
        Assert.That(factory.Started, Has.Count.EqualTo(2));
    }

    [Test]
    public void Restart_StaleRecordClearFails_RemainsInactiveAndRetries()
    {
        controller.StartIndefinite(controller.Snapshot.Revision);
        controller.Dispose();
        factory.Started.Single().Process.HasExited = true;
        store.FailSaves = true;
        controller = CreateController();
        Assert.That(controller.Snapshot.Status, Is.EqualTo("Off"));
        Assert.That(controller.Snapshot.Error, Is.Not.Null);
        store.FailSaves = false;
        controller.Tick();
        Assert.That(store.State.Session, Is.Null);
        Assert.That(controller.Snapshot.Error, Is.Null);
        Assert.That(factory.Started, Has.Count.EqualTo(1));
    }
}
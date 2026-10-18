using System.Diagnostics;

using Cortado.Core;

namespace Cortado.Core.Tests;

[TestFixture]
public sealed class ExternalSessionTests
{
    private TestClock clock = null!;
    private MemoryStore store = null!;
    private FakeFactory factory = null!;
    private FakeExternalMonitor monitor = null!;
    private SessionController controller = null!;

    [SetUp]
    public void SetUp()
    {
        clock = new();
        store = new();
        monitor = new();
        factory = new(monitor);
        controller = new(store, factory, clock, _ => { }, monitor);
        controller.Initialize();
    }

    [TearDown]
    public void TearDown() => controller.Dispose();

    private ExternalCaffeinateSession External(Int32 pid = 123, Int32? seconds = null) =>
        new(pid, 100, 200, seconds is { } value ? clock.GetUtcNow().AddSeconds(value) : null);

    [Test]
    public void Tick_ExternalUnknown_ShowsActiveStatusAndStopWithoutStartOrEdit()
    {
        monitor.Sessions.Add(External());
        controller.Tick();
        var menu = new CortadoMenu(controller, clock, () => TimeZoneInfo.Utc);
        menu.Open();
        Assert.Multiple(() =>
        {
            Assert.That(controller.Snapshot.IsActive, Is.True);
            Assert.That(controller.Snapshot.Status, Is.EqualTo("Unknown"));
            Assert.That(controller.Snapshot.Active, Is.Null);
            Assert.That(store.Saves, Is.Zero);
            Assert.That(menu.Items().Select(item => item.Label), Is.EqualTo(new[] { "Stop", "Settings" }));
        });
        menu.Execute(menu.Items().Single(item => item.Label == "Stop").Parameter);
        Assert.That(monitor.Stopped, Has.Count.EqualTo(1));
        Assert.That(controller.Snapshot.Status, Is.EqualTo("Off"));
    }

    [Test]
    public void Tick_TimedExternalSessions_UsesLatestEstimatedDeadlineAndLiveCountdown()
    {
        monitor.Sessions.AddRange([External(123, 300), External(124, 600)]);
        controller.Tick();
        Assert.That(controller.Snapshot.Status, Is.EqualTo("~10:00"));
        clock.Advance(TimeSpan.FromSeconds(1));
        controller.Tick();
        Assert.That(controller.Snapshot.Status, Is.EqualTo("~9:59"));
        monitor.Sessions.RemoveAt(1);
        controller.Tick();
        Assert.That(controller.Snapshot.Status, Is.EqualTo("~4:59"));
        monitor.Sessions.Clear();
        controller.Tick();
        Assert.That(controller.Snapshot.Status, Is.EqualTo("Off"));
    }

    [Test]
    public void MixedKnownAndUnknown_ReportsUnknownUntilAllEndTimesAreKnown()
    {
        monitor.Sessions.AddRange([External(123, 300), External(124)]);
        controller.Tick();
        Assert.That(controller.Snapshot.Status, Is.EqualTo("Unknown"));
    }

    [Test]
    public void ExpiredEstimate_WithLiveProcess_RemainsUnknownUntilObservedExit()
    {
        monitor.Sessions.Add(External(seconds: 30));
        controller.Tick();
        clock.Advance(TimeSpan.FromMinutes(1));
        controller.Tick();
        Assert.That(controller.Snapshot.Status, Is.EqualTo("Unknown"));
        Assert.That(controller.Snapshot.IsActive, Is.True);
        monitor.Sessions.Clear();
        controller.Tick();
        Assert.That(controller.Snapshot.Status, Is.EqualTo("Off"));
    }

    [Test]
    public void EstimatedDeadlineBoundary_UsesZeroAndFastChecksBeforeFallingBackToUnknown()
    {
        monitor.Sessions.Add(External(seconds: 5));
        controller.Tick();
        clock.Advance(TimeSpan.FromMilliseconds(5100));
        controller.Tick();
        Assert.That(controller.Snapshot.Status, Is.EqualTo("~0:00"));
        Assert.That(controller.Snapshot.IsExpiring, Is.True);
        clock.Advance(TimeSpan.FromSeconds(3));
        controller.Tick();
        Assert.That(controller.Snapshot.Status, Is.EqualTo("Unknown"));
        Assert.That(controller.Snapshot.IsExpiring, Is.False);
    }

    [Test]
    public void Start_RechecksProcessesAndRejectsAStaleInactiveMenu()
    {
        var revision = controller.Snapshot.Revision;
        monitor.Sessions.Add(External());
        Assert.Throws<SessionConflictException>(() => controller.StartIndefinite(revision));
        Assert.That(factory.Started, Is.Empty);
        Assert.That(controller.Snapshot.Status, Is.EqualTo("Unknown"));
    }

    [Test]
    public void Stop_NewOrReusedProcessSinceMenuWasOpened_RejectsWithoutStoppingIt()
    {
        monitor.Sessions.Add(External());
        controller.Tick();
        var revision = controller.Snapshot.Revision;
        monitor.Sessions[0] = External() with { StartSeconds = 999 };
        Assert.Throws<SessionConflictException>(() => controller.Stop(revision));
        Assert.That(monitor.Stopped, Is.Empty);
    }

    [Test]
    public void Stop_MultipleSessions_ExplicitlyLabelsAndStopsAllObservedSessions()
    {
        controller.StartDuration(TimeSpan.FromMinutes(5), controller.Snapshot.Revision);
        monitor.Sessions.AddRange([External(123, 600), External(124)]);
        controller.Tick();
        var menu = new CortadoMenu(controller, clock, () => TimeZoneInfo.Utc);
        menu.Open();
        Assert.That(menu.Items().Select(item => item.Label), Is.EqualTo(new[] { "Stop all", "Settings" }));
        Assert.That(controller.Snapshot.Status, Is.EqualTo("Unknown"));
        Assert.That(controller.Snapshot.CanEdit, Is.False);
        controller.Stop(controller.Snapshot.Revision);
        Assert.That(monitor.Stopped, Has.Count.EqualTo(2));
        Assert.That(factory.Started.Single().Process.HasExited, Is.True);
        Assert.That(controller.Snapshot.Status, Is.EqualTo("Off"));
    }

    [Test]
    public void Tick_VerifiesOwnedIdentityWithoutTreatingItAsAnExternalSession()
    {
        controller.StartIndefinite(controller.Snapshot.Revision);
        controller.Tick();
        Assert.That(monitor.LastExcludedPid, Is.Null);
        Assert.That(monitor.OwnedProcesses, Has.Count.EqualTo(1));
        Assert.That(controller.Snapshot.ExternalSessions, Is.Empty);
        Assert.That(controller.Snapshot.Status, Is.EqualTo("Indefinitely"));
    }

    [Test]
    public void Dispose_DoesNotStopOrPersistExternalSessions()
    {
        monitor.Sessions.Add(External());
        controller.Tick();
        controller.Dispose();
        Assert.That(monitor.Stopped, Is.Empty);
        Assert.That(store.Saves, Is.Zero);
        using var restored = new SessionController(store, factory, clock, _ => { }, monitor);
        restored.Initialize();
        Assert.That(restored.Snapshot.Status, Is.EqualTo("Unknown"));
        Assert.That(factory.Started, Is.Empty);
    }

    [Test]
    public void Tick_InspectionFailure_IsNotReportedAsOff()
    {
        monitor.Sessions.Add(External());
        controller.Tick();
        monitor.FailRead = true;
        controller.Tick();
        Assert.That(controller.Snapshot.Status, Is.EqualTo("Error"));
        Assert.That(controller.Snapshot.ExternalSessions, Has.Count.EqualTo(1));
    }

    [Test]
    public void Stop_Failure_PreservesExternalSessionForRetry()
    {
        monitor.Sessions.Add(External());
        controller.Tick();
        monitor.FailStop = true;
        Assert.Throws<IOException>(() => controller.Stop(controller.Snapshot.Revision));
        Assert.That(controller.Snapshot.IsActive, Is.True);
        monitor.FailStop = false;
        controller.Stop(controller.Snapshot.Revision);
        Assert.That(controller.Snapshot.Status, Is.EqualTo("Off"));
    }

    [Test]
    public void TimedOwnedAndExternal_ShowsTheLatestRemainingTimeButCannotEditForeignProcesses()
    {
        controller.StartDuration(TimeSpan.FromMinutes(10), controller.Snapshot.Revision);
        monitor.Sessions.Add(External(123, 300));
        controller.Tick();
        Assert.That(controller.Snapshot.Status, Is.EqualTo("~10:00"));
        Assert.Throws<InvalidOperationException>(() => controller.Extend(TimeSpan.FromMinutes(5), controller.Snapshot.Revision));
        Assert.That(factory.Started, Has.Count.EqualTo(1));
    }

    [Test]
    public void IndefiniteOwnedSession_StillProvidesAKnownIndefiniteLifetimeWithUnknownExternalProcesses()
    {
        controller.StartIndefinite(controller.Snapshot.Revision);
        monitor.Sessions.Add(External());
        controller.Tick();
        Assert.That(controller.Snapshot.Status, Is.EqualTo("Indefinitely"));
    }

    [Test]
    [Platform("MacOsX")]
    public void RealMonitor_DoesNotSignalAnUnrelatedExecutable()
    {
        using var process = Process.Start("/bin/sleep", "30")!;
        try
        {
            new MacCaffeinateMonitor().Stop(new(process.Id, 1, 0, null));
            Assert.That(process.HasExited, Is.False);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit();
            }
        }
    }

    [TestCase("-i -t 120", 120)]
    [TestCase("-dit120", 120)]
    [TestCase("-i -t120 -w123", 120)]
    [TestCase("-t 60 -t 120", 120)]
    [TestCase("-t 120 --", 120)]
    [TestCase("-i", null)]
    [TestCase("-u", null)]
    [TestCase("-t 120 /bin/sleep 30", null)]
    [TestCase("-t120 -- /bin/sleep 30", null)]
    [TestCase("-t 0", null)]
    [TestCase("-t -1", null)]
    [TestCase("-t 1.5", null)]
    [TestCase("-t", null)]
    [TestCase("-t 2147483648", null)]
    public void TimeoutDeadline_OnlyUsesUnambiguousExplicitTimeouts(String command, Int32? seconds)
    {
        var started = DateTimeOffset.UnixEpoch;
        var arguments = new[] { "/usr/bin/caffeinate" }.Concat(command.Split(' ')).ToArray();
        Assert.That(MacCaffeinateMonitor.TimeoutDeadline(arguments, started),
            Is.EqualTo(seconds is { } value ? started.AddSeconds(value) : (DateTimeOffset?)null));
    }

    [TestCase(false)]
    [TestCase(true)]
    [Platform("MacOsX")]
    public void RealMonitor_DetectsTimeoutExcludesOwnedAndSafelyStopsOnlyTheTestProcess(Boolean timed)
    {
        var start = new ProcessStartInfo("/usr/bin/caffeinate") { UseShellExecute = false };
        start.ArgumentList.Add("-i");
        if (timed)
        {
            start.ArgumentList.Add("-t");
            start.ArgumentList.Add("30");
        }
        using var process = Process.Start(start)!;
        var native = new MacCaffeinateMonitor();
        try
        {
            ExternalCaffeinateSession? detected = null;
            Assert.That(SpinWait.SpinUntil(() =>
            {
                detected = native.Read(null).SingleOrDefault(session => session.Pid == process.Id);
                return detected is not null;
            }, TimeSpan.FromSeconds(2)), Is.True);
            Assert.That(native.Read(process.Id).Any(session => session.Pid == process.Id), Is.False);
            if (timed)
            {
                Assert.That(detected!.Deadline, Is.InRange(DateTimeOffset.UtcNow.AddSeconds(25), DateTimeOffset.UtcNow.AddSeconds(31)));
            }
            else
            {
                Assert.That(detected!.Deadline, Is.Null);
            }

            Assert.Throws<SessionConflictException>(() => native.Stop(detected! with { StartMicroseconds = (detected.StartMicroseconds + 1) % 1_000_000 }));
            Assert.That(process.HasExited, Is.False);
            native.Stop(detected!);
            Assert.That(process.WaitForExit(1000), Is.True);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit();
            }
        }
    }
}
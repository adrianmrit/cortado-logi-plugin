using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

using Cortado.Core;

namespace Cortado.Core.Tests;

[TestFixture]
public sealed class StorageAndProcessTests
{
    private String directory = null!;

    [SetUp]
    public void SetUp() => directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "state-" + Guid.NewGuid().ToString("N"));

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void Save_RoundTrip_PreservesPreferencesWithoutInventingSession()
    {
        var store = new JsonStateStore(Path.Combine(directory, "state.json"));
        var state = new SavedState(3, KeepAwakeMode.MacAndDisplay);
        store.Save(state);
        Assert.That(store.Load(), Is.EqualTo(state));
        Assert.That(Directory.GetFiles(directory), Has.Length.EqualTo(1));
        Assert.That(File.ReadAllText(Path.Combine(directory, "state.json")), Does.Not.Contain("Active").And.Not.Contain("Deadline").And.Not.Contain("Pid"));
    }

    [Test]
    public void Load_MissingFile_ReturnsInactiveDefaults() => Assert.That(new JsonStateStore(Path.Combine(directory, "state.json")).Load(), Is.EqualTo(SavedState.Empty));

    [TestCase(SessionKind.Indefinite)]
    [TestCase(SessionKind.Duration)]
    [TestCase(SessionKind.Until)]
    public void Save_RoundTrip_PreservesProcessIdentityAndSessionTiming(SessionKind kind)
    {
        var store = new JsonStateStore(Path.Combine(directory, "state.json"));
        var state = StateWithSession(kind);
        store.Save(state);
        Assert.That(store.Load(), Is.EqualTo(state));
        Assert.That(Directory.GetFiles(directory), Has.Length.EqualTo(1));
    }

    private static SavedState StateWithSession(SessionKind kind = SessionKind.Duration) =>
        new(3, KeepAwakeMode.MacAndDisplay)
        {
            Session = new(new(1234, 1_700_000_000, 123456, null), "boot-id",
                new(kind, KeepAwakeMode.MacOnly, kind == SessionKind.Indefinite ? null : DateTimeOffset.UtcNow.AddMinutes(5)),
                123456789, TimeSpan.TicksPerSecond, kind == SessionKind.Indefinite ? null : TimeSpan.FromMinutes(5))
        };

    [TestCase("Identity")]
    [TestCase("BootIdentity")]
    [TestCase("Active")]
    [TestCase("StartedAt")]
    [TestCase("TimestampFrequency")]
    [TestCase("InitialRemaining")]
    [TestCase("Identity.Pid")]
    [TestCase("Identity.StartSeconds")]
    [TestCase("Identity.StartMicroseconds")]
    [TestCase("Active.Kind")]
    [TestCase("Active.Mode")]
    [TestCase("Active.Deadline")]
    public void Load_SessionWithMissingRequiredField_Rejects(String field)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "state.json");
        var json = JsonSerializer.SerializeToNode(StateWithSession())!;
        var names = field.Split('.');
        var parent = names.Length == 1 ? json["Session"]! : json["Session"]![names[0]]!;
        parent.AsObject().Remove(names[^1]);
        File.WriteAllText(path, json.ToJsonString());
        Assert.Throws<InvalidDataException>(() => new JsonStateStore(path).Load());
    }

    [TestCase("Identity", "null")]
    [TestCase("Identity.Pid", "0")]
    [TestCase("Identity.StartSeconds", "-1")]
    [TestCase("Identity.StartMicroseconds", "1000000")]
    [TestCase("BootIdentity", "\"\"")]
    [TestCase("Active", "null")]
    [TestCase("Active.Kind", "3")]
    [TestCase("Active.Kind", "0")]
    [TestCase("Active.Mode", "99")]
    [TestCase("Active.Deadline", "null")]
    [TestCase("StartedAt", "-1")]
    [TestCase("TimestampFrequency", "0")]
    [TestCase("InitialRemaining", "null")]
    [TestCase("InitialRemaining", "\"00:00:00\"")]
    [TestCase("InitialRemaining", "\"-00:05:00\"")]
    [TestCase("InitialRemaining", "\"24856.00:00:00\"")]
    public void Load_InvalidSessionMetadata_Rejects(String field, String value)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "state.json");
        var json = JsonSerializer.SerializeToNode(StateWithSession())!;
        var names = field.Split('.');
        var parent = names.Length == 1 ? json["Session"]! : json["Session"]![names[0]]!;
        parent[names[^1]] = JsonNode.Parse(value);
        File.WriteAllText(path, json.ToJsonString());
        Assert.Throws<InvalidDataException>(() => new JsonStateStore(path).Load());
    }

    [TestCase(1)]
    [TestCase(2)]
    public void LegacyPreferences_MigrateToCurrentSchemaWithoutSession(Int32 version)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "state.json");
        File.WriteAllText(path, $"{{\"SchemaVersion\":{version},\"PreferredMode\":1}}");
        var factory = new FakeFactory();
        using var sessions = new SessionController(new JsonStateStore(path), factory, new TestClock(), _ => { });
        sessions.Initialize();
        var saved = new JsonStateStore(path).Load();
        Assert.That(saved, Is.EqualTo(new SavedState(3, KeepAwakeMode.MacAndDisplay)));
        Assert.That(factory.Started, Is.Empty);
    }

    [TestCase("null")]
    [TestCase("{\"SchemaVersion\":99}")]
    [TestCase("{\"SchemaVersion\":1,\"PreferredMode\":99}")]
    [TestCase("{\"SchemaVersion\":\"1\",\"PreferredMode\":0}")]
    [TestCase("{\"SchemaVersion\":2,\"PreferredMode\":null}")]
    [TestCase("{\"SchemaVersion\":2,\"PreferredMode\":\"MacOnly\"}")]
    public void Load_CorruptOrUnknownState_Rejects(String json)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "state.json");
        File.WriteAllText(path, json);
        Assert.Throws<InvalidDataException>(() => new JsonStateStore(path).Load());
    }

    [TestCase("{\"Kind\":0,\"Mode\":0,\"Deadline\":null}")]
    [TestCase("{\"Kind\":1,\"Mode\":0,\"Deadline\":\"2050-01-01T00:00:00Z\"}")]
    [TestCase("\"invalid legacy session\"")]
    public void LegacyActiveIntent_IsIgnoredAndRemovedWithoutStartingAProcess(String active)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "state.json");
        File.WriteAllText(path, "{\"SchemaVersion\":1,\"PreferredMode\":1,\"Active\":" + active + "}");
        var factory = new FakeFactory();
        using var sessions = new SessionController(new JsonStateStore(path), factory, new TestClock(), _ => { });
        sessions.Initialize();
        Assert.That(sessions.Snapshot.Status, Is.EqualTo("Off"));
        Assert.That(sessions.Snapshot.PreferredMode, Is.EqualTo(KeepAwakeMode.MacAndDisplay));
        Assert.That(factory.Started, Is.Empty);
        Assert.That(File.ReadAllText(path), Does.Not.Contain("Active").And.Not.Contain("Deadline"));
        Assert.That(new JsonStateStore(path).Load().SchemaVersion, Is.EqualTo(3));
    }

    [TestCase(KeepAwakeMode.MacOnly, false)]
    [TestCase(KeepAwakeMode.MacAndDisplay, true)]
    public void Arguments_ProducesValidatedFlagsAndRoundsTimeoutUp(KeepAwakeMode mode, Boolean display)
    {
        var args = CaffeinateProcessFactory.Arguments(mode, TimeSpan.FromMilliseconds(1001));
        Assert.Multiple(() =>
        {
            Assert.That(args.Contains("-d"), Is.EqualTo(display));
            Assert.That(args, Does.Contain("-i"));
            Assert.That(args.TakeLast(2), Is.EqualTo(new[] { "-t", "2" }));
            Assert.That(args, Does.Not.Contain("-w"));
        });
    }

    [Test]
    public void Arguments_Indefinite_HasNoTimeout() =>
        Assert.That(CaffeinateProcessFactory.Arguments(KeepAwakeMode.MacOnly, null), Does.Not.Contain("-t").And.Not.Contain("-w"));

    [Test]
    public void Arguments_InvalidMode_IdentifiesParameter()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            CaffeinateProcessFactory.Arguments((KeepAwakeMode)999, null));
        Assert.That(exception!.ParamName, Is.EqualTo("mode"));
    }

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(2147483648d)]
    public void Arguments_InvalidTimeout_Rejects(Double seconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => CaffeinateProcessFactory.Arguments(KeepAwakeMode.MacOnly, TimeSpan.FromSeconds(seconds)));

    [Test]
    [Platform("MacOsX")]
    [Category("macOS")]
    public void RealCaffeinate_TimedProcess_ExitsWithoutAffectingOtherSession()
    {
        var factory = new CaffeinateProcessFactory();
        using var other = factory.Start(KeepAwakeMode.MacOnly, null);
        try
        {
            using var timed = factory.Start(KeepAwakeMode.MacOnly, TimeSpan.FromSeconds(1));
            try
            {
                Assert.That(SpinWait.SpinUntil(() => timed.HasExited, TimeSpan.FromSeconds(4)), Is.True);
                Assert.That(other.HasExited, Is.False);
            }
            finally { timed.Stop(); }
        }
        finally { other.Stop(); }
    }

    [Test]
    [Platform("MacOsX")]
    public void RealCaffeinate_RoundedTimeout_DoesNotFlashUnknownOrClaimOffBeforeExit()
    {
        using var sessions = new SessionController(new MemoryStore(), new CaffeinateProcessFactory(),
            new MacContinuousClock(), exception => Assert.Fail(exception.Message));
        try
        {
            sessions.Initialize();
            sessions.StartDuration(TimeSpan.FromMilliseconds(1001), sessions.Snapshot.Revision);
            var statuses = new List<String>();
            Assert.That(SpinWait.SpinUntil(() =>
            {
                sessions.Tick();
                statuses.Add(sessions.Snapshot.Status);
                Thread.Sleep(20);
                return !sessions.Snapshot.IsActive;
            }, TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(statuses, Does.Contain("0:00").And.Not.Contain("Unknown"));
            Assert.That(statuses.Last(), Is.EqualTo("Off"));
        }
        finally
        {
            sessions.Tick();
            if (sessions.Snapshot.IsActive)
            {
                sessions.Stop(sessions.Snapshot.Revision);
            }
        }
    }

    [Test]
    [Platform("MacOsX")]
    [Category("macOS")]
    public void RealCaffeinate_LauncherExits_PreservesProcessWithAndWithoutTimeout()
    {
        foreach (var timed in new[] { false, true })
        {
            // The test shell launches only this test's bounded child and exits immediately.
            var arguments = String.Join(' ', CaffeinateProcessFactory.Arguments(KeepAwakeMode.MacOnly,
                timed ? TimeSpan.FromSeconds(30) : null));
            var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, RedirectStandardOutput = true };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add($"/usr/bin/caffeinate {arguments} >/dev/null 2>&1 & echo $!");
            using var launcher = Process.Start(start)!;
            var pid = Int32.Parse(launcher.StandardOutput.ReadToEnd(), System.Globalization.CultureInfo.InvariantCulture);
            launcher.WaitForExit();
            using var caffeinate = Process.GetProcessById(pid);
            try
            {
                Assert.That(caffeinate.WaitForExit(200), Is.False);
                using var attached = new CaffeinateProcessFactory().Attach(
                    new MacCaffeinateMonitor().Read(null).Single(session => session.Pid == pid));
                Assert.That(attached, Is.Not.Null);
            }
            finally
            {
                if (!caffeinate.HasExited)
                { caffeinate.Kill(); caffeinate.WaitForExit(); }
            }
        }
    }

    [TestCase(KeepAwakeMode.MacOnly, false)]
    [TestCase(KeepAwakeMode.MacAndDisplay, true)]
    [Platform("MacOsX")]
    [Category("macOS")]
    public void RealCaffeinate_AssertionsMatchConfiguredMode(KeepAwakeMode mode, Boolean display)
    {
        using var process = new CaffeinateProcessFactory().Start(mode, TimeSpan.FromSeconds(15));
        try
        {
            var ownAssertions = "";
            var found = SpinWait.SpinUntil(() =>
            {
                using var pmset = Process.Start(new ProcessStartInfo("/usr/bin/pmset", "-g assertions")
                { UseShellExecute = false, RedirectStandardOutput = true })!;
                var output = pmset.StandardOutput.ReadToEnd();
                pmset.WaitForExit();
                ownAssertions = String.Join('\n', output.Split('\n').Where(line => line.Contains($"pid {process.Id}(", StringComparison.Ordinal)));
                return ownAssertions.Contains("PreventUserIdleSystemSleep", StringComparison.Ordinal) &&
                    (!display || ownAssertions.Contains("PreventUserIdleDisplaySleep", StringComparison.Ordinal));
            }, TimeSpan.FromSeconds(3));
            Assert.That(found, Is.True, "Cortado's caffeinate process should own the expected assertions.");
            Assert.That(ownAssertions.Contains("PreventUserIdleDisplaySleep", StringComparison.Ordinal), Is.EqualTo(display));
        }
        finally
        {
            process.Stop();
        }
    }

    [Test]
    [Platform("MacOsX")]
    public void RealCaffeinate_IndefiniteCommandsAndMonitoring_HaveNoLongSynchronousWait()
    {
        var factory = new RecordingFactory();
        using var sessions = new SessionController(new JsonStateStore(Path.Combine(directory, "state.json")),
            factory, new MacContinuousClock(), _ => { }, new TestProcessMonitor(factory));
        sessions.Initialize();
        try
        {
            for (var iteration = 0; iteration < 3; iteration++)
            {
                var watch = Stopwatch.StartNew();
                sessions.StartIndefinite(sessions.Snapshot.Revision);
                var start = watch.Elapsed;
                watch.Restart();
                sessions.Stop(sessions.Snapshot.Revision);
                var stop = watch.Elapsed;
                TestContext.Out.WriteLine($"Indefinite iteration {iteration + 1}: start + verification {start.TotalMilliseconds:F1} ms; stop + verification {stop.TotalMilliseconds:F1} ms.");
                Assert.Multiple(() =>
                {
                    Assert.That(start, Is.LessThan(TimeSpan.FromMilliseconds(500)));
                    Assert.That(stop, Is.LessThan(TimeSpan.FromMilliseconds(500)));
                });
            }
        }
        finally
        {
            if (factory.Identity is { } identity)
            {
                new MacCaffeinateMonitor().Stop(identity);
            }
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    [Platform("MacOsX")]
    public void RealCaffeinate_ControllerRestart_RestoresSameProcessAndNativeExpiry(Boolean timed)
    {
        var store = new JsonStateStore(Path.Combine(directory, "state.json"));
        var factory = new RecordingFactory();
        var monitor = new TestProcessMonitor(factory);
        var clock = new MacContinuousClock();
        var errors = new List<Exception>();
        using var original = new SessionController(store, factory, clock, errors.Add, monitor);
        original.Initialize();
        try
        {
            if (timed)
            {
                original.StartDuration(TimeSpan.FromSeconds(2), original.Snapshot.Revision);
            }
            else
            {
                original.StartIndefinite(original.Snapshot.Revision);
            }

            var saved = store.Load();
            original.Dispose();
            Thread.Sleep(50);
            using var restored = new SessionController(store, factory, clock, errors.Add, monitor);
            restored.Initialize();
            Assert.That(restored.Snapshot.Active?.Kind, Is.EqualTo(timed ? SessionKind.Duration : SessionKind.Indefinite));
            Assert.That(restored.Snapshot.ExternalSessions, Is.Empty);
            Assert.That(store.Load().Session?.Identity, Is.EqualTo(saved.Session!.Identity));
            if (timed)
            {
                Assert.That(SpinWait.SpinUntil(() =>
                {
                    restored.Tick();
                    return !restored.Snapshot.IsActive;
                }, TimeSpan.FromSeconds(5)), Is.True);
                Assert.That(store.Load().Session, Is.Null);
            }
            else
            {
                restored.Stop(restored.Snapshot.Revision);
                Assert.That(restored.Snapshot.Status, Is.EqualTo("Off"));
            }
            Assert.That(errors, Is.Empty);
        }
        finally
        {
            if (factory.Identity is { } identity)
            {
                new MacCaffeinateMonitor().Stop(identity);
            }
        }
    }

    [Test]
    [Platform("MacOsX")]
    public void Attach_UnrelatedExecutableOrReusedIdentity_ReturnsNullWithoutSignaling()
    {
        using var other = Process.Start("/bin/sleep", "15")!;
        var factory = new CaffeinateProcessFactory();
        using var own = factory.Start(KeepAwakeMode.MacOnly, TimeSpan.FromSeconds(15));
        try
        {
            Assert.That(factory.Attach(new(other.Id, 1, 0, null)), Is.Null);
            Assert.That(factory.Attach(own.Identity with { StartMicroseconds = (own.Identity.StartMicroseconds + 1) % 1_000_000 }), Is.Null);
            Assert.That(other.HasExited, Is.False);
            Assert.That(own.HasExited, Is.False);
        }
        finally
        {
            own.Stop();
            if (!other.HasExited)
            { other.Kill(); other.WaitForExit(); }
        }
    }

    [Test]
    [Platform("MacOsX")]
    public void MacContinuousClock_ElapsedTime_IsMonotonicAndTracksRealTime()
    {
        var clock = new MacContinuousClock();
        var start = clock.GetTimestamp();
        Thread.Sleep(20);
        var elapsed = clock.GetElapsedTime(start);
        Assert.That(elapsed, Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds(15)).And.LessThan(TimeSpan.FromSeconds(1)));
    }

    [TestCase(0, "0:00")]
    [TestCase(59.1, "1:00")]
    [TestCase(3600, "1:00:00")]
    [TestCase(43200, "12:00:00")]
    public void FormatRemaining_RoundsAndFormats(Double seconds, String expected) =>
        Assert.That(SessionSnapshot.FormatRemaining(TimeSpan.FromSeconds(seconds)), Is.EqualTo(expected));

    private sealed class RecordingFactory : IAwakeProcessFactory
    {
        public String BootIdentity => new CaffeinateProcessFactory().BootIdentity;
        public IAwakeProcess? Attach(ExternalCaffeinateSession identity) => new CaffeinateProcessFactory().Attach(identity);
        public ExternalCaffeinateSession? Identity { get; private set; }
        public IAwakeProcess Start(KeepAwakeMode mode, TimeSpan? remaining)
        {
            var process = new CaffeinateProcessFactory().Start(mode, remaining);
            Identity = process.Identity;
            return process;
        }
    }

    private sealed class TestProcessMonitor(RecordingFactory factory) : IExternalSessionMonitor
    {
        public IReadOnlyList<ExternalCaffeinateSession> Read(Int32? excludedPid) =>
            new MacCaffeinateMonitor().Read(null).Where(session =>
                factory.Identity is { } identity && session.SameProcess(identity)).ToArray();
        public void Stop(ExternalCaffeinateSession session) =>
            throw new InvalidOperationException("This test may stop only its own process through its owned handle.");
    }
}
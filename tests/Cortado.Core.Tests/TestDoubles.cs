using Cortado.Core;

namespace Cortado.Core.Tests;

internal sealed class TestClock : TimeProvider
{
    private DateTimeOffset now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
    private Int64 ticks;
    public override DateTimeOffset GetUtcNow() => now;
    public override Int64 GetTimestamp() => ticks;
    public override Int64 TimestampFrequency => TimeSpan.TicksPerSecond;
    public void Advance(TimeSpan elapsed)
    {
        now += elapsed;
        ticks += elapsed.Ticks;
    }
    public void Jump(TimeSpan difference) => now += difference;
}

internal sealed class MemoryStore : IStateStore
{
    public SavedState State { get; set; } = SavedState.Empty;
    public Boolean FailNextSave { get; set; }
    public Boolean FailLoad { get; set; }
    public Boolean FailSaves { get; set; }
    public Int32 Saves { get; private set; }
    public SavedState Load() => FailLoad ? throw new IOException("Storage unavailable") : State;
    public void Save(SavedState state)
    {
        Saves++;
        if (FailNextSave || FailSaves)
        {
            FailNextSave = false;
            throw new IOException("Disk full");
        }
        State = state;
    }
}

internal sealed class FakeProcess : IAwakeProcess
{
    private sealed class ProcessState
    {
        public Boolean HasExited;
        public Boolean IgnoreStop;
    }
    private readonly ProcessState state;
    public FakeProcess() : this(new ProcessState()) { }
    private FakeProcess(ProcessState state) => this.state = state;
    public ExternalCaffeinateSession Identity { get; init; } = new(1000, 100, 0, null);
    public Int32 Id => Identity.Pid;
    public Boolean HasExited { get => state.HasExited; set => state.HasExited = value; }
    public Boolean IgnoreStop { get => state.IgnoreStop; set => state.IgnoreStop = value; }
    public Boolean Disposed { get; private set; }
    public void Stop()
    {
        if (!IgnoreStop)
        {
            HasExited = true;
        }
    }
    public void Dispose() => Disposed = true;
    public FakeProcess Attach() => new(state) { Identity = Identity };
}

internal sealed class FakeFactory(FakeExternalMonitor? monitor = null) : IAwakeProcessFactory
{
    public String BootIdentity { get; set; } = "test-boot";
    public Boolean FailAttach { get; set; }
    public Action? BeforeAttach { get; set; }
    public IAwakeProcess? Attach(ExternalCaffeinateSession identity)
    {
        if (FailAttach)
        {
            throw new IOException("Process reattachment failed.");
        }

        BeforeAttach?.Invoke();
        return Started.Select(item => item.Process)
            .SingleOrDefault(process => !process.HasExited && process.Identity.SameProcess(identity))?.Attach();
    }
    public Boolean FailNextStart { get; set; }
    public Action<FakeProcess>? AfterStart { get; set; }
    public List<(KeepAwakeMode Mode, TimeSpan? Remaining, FakeProcess Process)> Started { get; } = [];
    public IAwakeProcess Start(KeepAwakeMode mode, TimeSpan? remaining)
    {
        if (FailNextStart)
        {
            FailNextStart = false;
            throw new IOException("Process launch failed");
        }
        var process = new FakeProcess { Identity = new(1000 + Started.Count, 100 + Started.Count, 200, null) };
        Started.Add((mode, remaining, process));
        monitor?.OwnedProcesses.Add(process);
        AfterStart?.Invoke(process);
        return process;
    }
}

internal sealed class FakeExternalMonitor : IExternalSessionMonitor
{
    public List<ExternalCaffeinateSession> Sessions { get; } = [];
    public List<ExternalCaffeinateSession> Stopped { get; } = [];
    public List<FakeProcess> OwnedProcesses { get; } = [];
    public Int32? LastExcludedPid { get; private set; }
    public Boolean FailRead { get; set; }
    public Boolean FailStop { get; set; }

    public IReadOnlyList<ExternalCaffeinateSession> Read(Int32? excludedPid)
    {
        LastExcludedPid = excludedPid;
        return FailRead
            ? throw new IOException("Process inspection failed.")
            : (IReadOnlyList<ExternalCaffeinateSession>)Sessions.Concat(OwnedProcesses.Where(process => !process.HasExited && !Sessions.Any(session => session.Pid == process.Id))
            .Select(process => process.Identity)).Where(session => session.Pid != excludedPid).ToArray();
    }

    public void Stop(ExternalCaffeinateSession session)
    {
        if (FailStop)
        {
            throw new IOException("External process could not be stopped.");
        }

        Stopped.Add(session);
        Sessions.Remove(session);
    }
}
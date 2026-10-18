using System.Globalization;

namespace Cortado.Core;

public sealed class CaffeinateProcessFactory : IAwakeProcessFactory
{
    public String BootIdentity => MacContinuousClock.BootIdentity;

    public static IReadOnlyList<String> Arguments(KeepAwakeMode mode, TimeSpan? remaining)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Invalid caffeinate mode.");
        }

        var arguments = new List<String> { "-i" };
        if (mode == KeepAwakeMode.MacAndDisplay)
        {
            arguments.Add("-d");
        }

        if (remaining is { } duration)
        {
            var seconds = Math.Ceiling(duration.TotalSeconds);
            if (seconds is <= 0 or > Int32.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(remaining), "Duration must be positive and fit caffeinate's timeout.");
            }

            arguments.AddRange(["-t", seconds.ToString("0", CultureInfo.InvariantCulture)]);
        }
        return arguments;
    }

    public IAwakeProcess Start(KeepAwakeMode mode, TimeSpan? remaining)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("Cortado requires macOS.");
        }

        var pid = MacCaffeinateLauncher.Start(Arguments(mode, remaining));
        try
        {
            var identity = MacCaffeinateMonitor.ReadIdentity(pid)
                ?? throw new IOException("macOS could not verify the new caffeinate process.");
            MacCaffeinateLauncher.Track(pid);
            return new OwnedProcess(identity);
        }
        catch
        {
            MacCaffeinateLauncher.StopFailedStart(pid);
            throw;
        }
    }

    public IAwakeProcess? Attach(ExternalCaffeinateSession identity)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("Cortado requires macOS.");
        }

        MacCaffeinateLauncher.ReapExitedChildren();
        var current = MacCaffeinateMonitor.ReadIdentity(identity.Pid);
        return current is null || !current.SameProcess(identity) ? null : (IAwakeProcess)new OwnedProcess(identity);
    }

    private sealed class OwnedProcess(ExternalCaffeinateSession identity) : IAwakeProcess
    {
        public ExternalCaffeinateSession Identity => identity;
        public Int32 Id => identity.Pid;
        public Boolean HasExited
        {
            get
            {
                MacCaffeinateLauncher.ReapExitedChildren();
                var current = MacCaffeinateMonitor.ReadIdentity(identity.Pid);
                return current is null || !current.SameProcess(identity);
            }
        }

        public void Stop()
        {
            if (HasExited)
            {
                return;
            }

            try
            { new MacCaffeinateMonitor().Stop(identity); }
            finally { MacCaffeinateLauncher.ReapExitedChildren(); }
        }

        public void Dispose() => MacCaffeinateLauncher.ReapExitedChildren();
    }
}
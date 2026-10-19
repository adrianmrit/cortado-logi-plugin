using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Cortado.Core;

public sealed class MacCaffeinateMonitor : IExternalSessionMonitor
{
    internal const Int32 Einval = 22;

    public IReadOnlyList<ExternalCaffeinateSession> Read(Int32? excludedPid)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("External caffeinate detection requires macOS.");
        }

        MacCaffeinateLauncher.ReapExitedChildren();
        var size = Native.proc_listpids(4, Native.geteuid(), null, 0);
        if (size <= 0)
        {
            throw NativeError("Could not enumerate caffeinate processes");
        }

        Int32[] pids;
        Int32 bytes;
        do
        {
            pids = new Int32[size / sizeof(Int32) + 64];
            bytes = Native.proc_listpids(4, Native.geteuid(), pids, pids.Length * sizeof(Int32));
            if (bytes <= 0)
            {
                throw NativeError("Could not enumerate caffeinate processes");
            }

            size = checked(pids.Length * sizeof(Int32) * 2);
        } while (bytes >= pids.Length * sizeof(Int32));
        var result = new List<ExternalCaffeinateSession>();
        foreach (var pid in pids.Take(bytes / sizeof(Int32)))
        {
            if (pid <= 0 || pid == excludedPid)
            {
                continue;
            }

            var identity = ReadIdentity(pid);
            if (identity is null)
            {
                continue;
            }

            var arguments = ReadArguments(identity);
            var deadline = arguments is null ? null : TimeoutDeadline(arguments,
                DateTimeOffset.FromUnixTimeSeconds(identity.StartSeconds).AddTicks(identity.StartMicroseconds * 10));
            if (ReadIdentity(pid) == identity)
            {
                result.Add(identity with { Deadline = deadline });
            }
        }
        return result;
    }

    public void Stop(ExternalCaffeinateSession session)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("External caffeinate control requires macOS.");
        }

        if (session.Pid <= 0 || session.StartSeconds < 0 || session.StartMicroseconds is < 0 or >= 1_000_000)
        {
            throw new ArgumentException("Invalid external process identity.", nameof(session));
        }

        var current = ReadIdentity(session.Pid);
        if (current is null)
        {
            return;
        }

        if (current.StartSeconds != session.StartSeconds || current.StartMicroseconds != session.StartMicroseconds)
        {
            throw new SessionConflictException();
        }

        if (Native.kill(session.Pid, 15) != 0)
        {
            if (Marshal.GetLastPInvokeError() == 3)
            {
                return;
            }

            throw NativeError("Could not stop the external caffeinate process");
        }
        if (!SpinWait.SpinUntil(() => ReadIdentity(session.Pid) != current, TimeSpan.FromMilliseconds(500)))
        {
            throw new IOException("The external caffeinate process did not stop. Check the app that launched it.");
        }
    }

    public static DateTimeOffset? TimeoutDeadline(IReadOnlyList<String> arguments, DateTimeOffset started)
    {
        Int64? timeout = null;
        for (var index = 1; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument == "--")
            {
                if (index + 1 < arguments.Count)
                {
                    return null;
                }

                break;
            }
            if (argument.Length < 2 || argument[0] != '-')
            {
                return null;
            }

            for (var flag = 1; flag < argument.Length; flag++)
            {
                var option = argument[flag];
                if ("dimsu".Contains(option))
                {
                    continue;
                }

                if (option is not ('t' or 'w'))
                {
                    return null;
                }

                var value = flag + 1 < argument.Length ? argument[(flag + 1)..] :
                    ++index < arguments.Count ? arguments[index] : "";
                if (!Int64.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ||
                    number <= 0 || number > Int32.MaxValue)
                {
                    return null;
                }

                if (option == 't')
                {
                    timeout = number;
                }

                break;
            }
        }
        return timeout is { } seconds ? started.AddSeconds(seconds) : null;
    }

    internal static ExternalCaffeinateSession? ReadIdentity(Int32 pid)
    {
        var info = new ProcessInfo();
        var bytes = Native.proc_pidinfo(pid, 3, 0, ref info, Marshal.SizeOf<ProcessInfo>());
        if (bytes <= 0)
        {
            return Marshal.GetLastPInvokeError() is 2 or 3 ? null : throw NativeError("Could not inspect a process");
        }
        if (bytes != Marshal.SizeOf<ProcessInfo>())
        {
            throw new IOException("macOS returned an unsupported process identity.");
        }

        if (info.Uid != Native.geteuid() || info.Status == 5 || info.Name != "caffeinate")
        {
            return null;
        }

        var buffer = new Byte[4096];
        bytes = Native.proc_pidpath(pid, buffer, (UInt32)buffer.Length);
        if (bytes <= 0)
        {
            return Marshal.GetLastPInvokeError() is 2 or 3 ? null : throw NativeError("Could not inspect a process executable");
        }
        var end = Array.IndexOf(buffer, (Byte)0, 0, Math.Min(bytes, buffer.Length));
        var path = Encoding.UTF8.GetString(buffer, 0, end < 0 ? Math.Min(bytes, buffer.Length) : end);
        return path == "/usr/bin/caffeinate"
            ? new(pid, info.StartSeconds, info.StartMicroseconds, null) : null;
    }

    internal static IReadOnlyList<String>? ReadArguments(ExternalCaffeinateSession identity)
    {
        try
        {
            return ReadNativeArguments(identity.Pid);
        }
        // KERN_PROCARGS2 can return EINVAL when the process exits during inspection.
        catch (Win32Exception exception) when (exception.NativeErrorCode == Einval)
        {
            if (ReadIdentity(identity.Pid) != identity)
            {
                return null;
            }

            throw;
        }
    }

    internal static IReadOnlyList<String>? ReadNativeArguments(Int32 pid)
    {
        var name = new[] { 1, 49, pid }; // CTL_KERN, KERN_PROCARGS2.
        UIntPtr length = 0;
        if (Native.sysctl(name, (UInt32)name.Length, null, ref length, IntPtr.Zero, 0) != 0)
        {
            return Marshal.GetLastPInvokeError() is 1 or 2 or 3 or 13 ? null : throw NativeError("Could not inspect caffeinate arguments");
        }
        var buffer = new Byte[checked((Int32)length)];
        if (Native.sysctl(name, (UInt32)name.Length, buffer, ref length, IntPtr.Zero, 0) != 0)
        {
            return Marshal.GetLastPInvokeError() is 1 or 2 or 3 or 13 ? null : throw NativeError("Could not inspect caffeinate arguments");
        }
        var count = buffer.Length >= sizeof(Int32) ? BitConverter.ToInt32(buffer) : 0;
        if (count <= 0 || count > (Int32)length)
        {
            return null;
        }

        var offset = Array.IndexOf(buffer, (Byte)0, sizeof(Int32), (Int32)length - sizeof(Int32));
        if (offset < 0)
        {
            return null;
        }

        while (offset < (Int32)length && buffer[offset] == 0)
        {
            offset++;
        }

        var arguments = new List<String>();
        for (var index = 0; index < count; index++)
        {
            var end = Array.IndexOf(buffer, (Byte)0, offset, (Int32)length - offset);
            if (end < 0)
            {
                return null;
            }

            arguments.Add(Encoding.UTF8.GetString(buffer, offset, end - offset));
            offset = end + 1;
        }
        return arguments;
    }

    private static Win32Exception NativeError(String message)
    {
        var code = Marshal.GetLastPInvokeError();
        return new(code, $"{message}: {new Win32Exception(code).Message}");
    }

    // Darwin proc_bsdinfo has a stable 136-byte ABI on both supported 64-bit architectures.
    [StructLayout(LayoutKind.Explicit, Size = 136)]
    private struct ProcessInfo
    {
        [FieldOffset(4)] public UInt32 Status;
        [FieldOffset(20)] public UInt32 Uid;
        [FieldOffset(48), MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)] public String Name;
        [FieldOffset(120)] public Int64 StartSeconds;
        [FieldOffset(128)] public Int64 StartMicroseconds;
    }

    private static class Native
    {
        [DllImport("/usr/lib/libproc.dylib", SetLastError = true)]
        internal static extern Int32 proc_listpids(UInt32 type, UInt32 typeInfo, [Out] Int32[]? buffer, Int32 bufferSize);
        [DllImport("/usr/lib/libproc.dylib", SetLastError = true)]
        internal static extern Int32 proc_pidinfo(Int32 pid, Int32 flavor, UInt64 arg, ref ProcessInfo buffer, Int32 bufferSize);
        [DllImport("/usr/lib/libproc.dylib", SetLastError = true)]
        internal static extern Int32 proc_pidpath(Int32 pid, [Out] Byte[] buffer, UInt32 bufferSize);
        [DllImport("/usr/lib/libSystem.B.dylib")]
        internal static extern UInt32 geteuid();
        [DllImport("/usr/lib/libSystem.B.dylib", SetLastError = true)]
        internal static extern Int32 kill(Int32 pid, Int32 signal);
        [DllImport("/usr/lib/libSystem.B.dylib", SetLastError = true)]
        internal static extern Int32 sysctl(Int32[] name, UInt32 nameLength, [Out] Byte[]? buffer,
            ref UIntPtr length, IntPtr newValue, UIntPtr newLength);
    }
}
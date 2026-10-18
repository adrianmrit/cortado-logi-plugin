using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Cortado.Core;

internal static class MacCaffeinateLauncher
{
    private static readonly Object Gate = new();
    // Native-spawned children are not registered with .NET's Process reaper.
    private static readonly HashSet<Int32> Children = [];

    public static Int32 Start(IReadOnlyList<String> arguments)
    {
        ReapExitedChildren();
        Check(Native.posix_spawnattr_init(out var attributes), "initialize spawn attributes");
        try
        {
            // Close even descriptors the host opened without FD_CLOEXEC, and leave its process group.
            const Int16 PosixSpawnCloexecDefault = 0x4000; // POSIX_SPAWN_CLOEXEC_DEFAULT in macOS <spawn.h>.
            const Int16 PosixSpawnSetsid = 0x0400; // POSIX_SPAWN_SETSID in macOS <spawn.h>.
            const Int16 spawnFlags = PosixSpawnCloexecDefault | PosixSpawnSetsid;
            Check(Native.posix_spawnattr_setflags(ref attributes, spawnFlags), "isolate caffeinate");
            Check(Native.posix_spawn_file_actions_init(out var actions), "initialize spawn file actions");
            try
            {
                for (var fd = 0; fd < 3; fd++)
                {
                    Check(Native.posix_spawn_file_actions_addopen(ref actions, fd, "/dev/null", fd == 0 ? 0 : 1, 0),
                        "isolate caffeinate standard streams");
                }

                String?[] argv = ["/usr/bin/caffeinate", .. arguments, null];
                Check(Native.posix_spawn(out var pid, "/usr/bin/caffeinate", ref actions, ref attributes,
                    argv, [null]), "start caffeinate");
                return pid;
            }
            finally { Check(Native.posix_spawn_file_actions_destroy(ref actions), "release spawn file actions"); }
        }
        finally { Check(Native.posix_spawnattr_destroy(ref attributes), "release spawn attributes"); }
    }

    public static void Track(Int32 pid)
    {
        lock (Gate)
        {
            Children.Add(pid);
        }
    }

    public static void ReapExitedChildren()
    {
        lock (Gate)
        {
            foreach (var pid in Children.ToArray())
            {
                if (Wait(pid, 1) != 0)
                {
                    Children.Remove(pid);
                }
            }
        }
    }

    public static void StopFailedStart(Int32 pid)
    {
        if (Native.kill(pid, 9) != 0 && Marshal.GetLastPInvokeError() != 3)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not stop unverified caffeinate.");
        }

        Wait(pid, 0);
    }

    private static Int32 Wait(Int32 pid, Int32 options)
    {
        Int32 result;
        do
        { result = Native.waitpid(pid, out _, options); }
        while (result < 0 && Marshal.GetLastPInvokeError() == 4);
        return result < 0 && Marshal.GetLastPInvokeError() != 10
            ? throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not reap exited caffeinate.")
            : result;
    }

    private static void Check(Int32 error, String operation)
    {
        if (error != 0)
        {
            throw new Win32Exception(error, $"Could not {operation}: {new Win32Exception(error).Message}");
        }
    }

    private static class Native
    {
        [DllImport("/usr/lib/libSystem.B.dylib")]
        internal static extern Int32 posix_spawnattr_init(out IntPtr attributes);
        [DllImport("/usr/lib/libSystem.B.dylib")]
        internal static extern Int32 posix_spawnattr_setflags(ref IntPtr attributes, Int16 flags);
        [DllImport("/usr/lib/libSystem.B.dylib")]
        internal static extern Int32 posix_spawnattr_destroy(ref IntPtr attributes);
        [DllImport("/usr/lib/libSystem.B.dylib")]
        internal static extern Int32 posix_spawn_file_actions_init(out IntPtr actions);
        [DllImport("/usr/lib/libSystem.B.dylib")]
        internal static extern Int32 posix_spawn_file_actions_addopen(ref IntPtr actions, Int32 fd, String path, Int32 flags, UInt16 mode);
        [DllImport("/usr/lib/libSystem.B.dylib")]
        internal static extern Int32 posix_spawn_file_actions_destroy(ref IntPtr actions);
        [DllImport("/usr/lib/libSystem.B.dylib")]
        internal static extern Int32 posix_spawn(out Int32 pid, String path, ref IntPtr actions, ref IntPtr attributes,
            [In, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] String?[] arguments,
            [In, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] String?[] environment);
        [DllImport("/usr/lib/libSystem.B.dylib", SetLastError = true)]
        internal static extern Int32 waitpid(Int32 pid, out Int32 status, Int32 options);
        [DllImport("/usr/lib/libSystem.B.dylib", SetLastError = true)]
        internal static extern Int32 kill(Int32 pid, Int32 signal);
    }
}
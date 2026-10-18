using System.ComponentModel;
using System.Runtime.InteropServices;

using Cortado.Core;

using Microsoft.Win32.SafeHandles;

namespace Cortado.Core.Tests;

[TestFixture]
[Platform("MacOsX")]
public sealed class MacProcessIsolationTests
{
    [Test]
    public void Start_SurvivingProcess_HasItsOwnSessionAndProcessGroup()
    {
        using var process = new CaffeinateProcessFactory().Start(KeepAwakeMode.MacOnly, TimeSpan.FromSeconds(15));
        try
        {
            Assert.That(Native.getsid(process.Id), Is.EqualTo(process.Id));
            Assert.That(Native.getpgid(process.Id), Is.EqualTo(process.Id));
        }
        finally { process.Stop(); }
    }

    [Test]
    public void Dispose_TimedProcess_StillExpiresAndIsReapedWithoutRetainingAHandle()
    {
        var factory = new CaffeinateProcessFactory();
        using var process = factory.Start(KeepAwakeMode.MacOnly, TimeSpan.FromSeconds(1));
        var identity = process.Identity;
        process.Dispose();
        try
        {
            Assert.That(SpinWait.SpinUntil(() =>
            {
                using var attached = factory.Attach(identity);
                return attached is null && Native.kill(identity.Pid, 0) == -1 &&
                    Marshal.GetLastPInvokeError() == 3;
            }, TimeSpan.FromSeconds(4)), Is.True);
            var result = Native.waitpid(identity.Pid, out _, 1);
            var error = Marshal.GetLastPInvokeError();
            Assert.That(result, Is.EqualTo(-1));
            Assert.That(error, Is.EqualTo(10), "The exited child must already be reaped.");
        }
        finally { process.Stop(); }
    }

    [Test]
    public void Start_InheritableHostLock_DoesNotPreventRestartFromAcquiringIt()
    {
        var path = Path.Combine(Path.GetTempPath(), "cortado-lock-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, "");
        try
        {
            using var hostLock = Handle(Native.open(path, 2));
            Assert.That(Native.flock(Fd(hostLock), 2 | 4), Is.Zero);
            using var inheritable = Handle(Native.dup(Fd(hostLock)));
            Assert.That(Native.fcntl(Fd(inheritable), 1), Is.Zero, "The regression requires a descriptor without FD_CLOEXEC.");
            using var process = new CaffeinateProcessFactory().Start(KeepAwakeMode.MacOnly, TimeSpan.FromSeconds(15));
            try
            {
                inheritable.Dispose();
                hostLock.Dispose();
                using var restartedHost = Handle(Native.open(path, 2));
                Assert.That(Native.flock(Fd(restartedHost), 2 | 4), Is.Zero,
                    "Surviving caffeinate must not retain the exited host's lock.");
                Assert.That(process.HasExited, Is.False);
            }
            finally { process.Stop(); }
        }
        finally { File.Delete(path); }
    }

    [Test]
    public void Start_InheritableControlSocket_DoesNotKeepExitedHostConnectionAlive()
    {
        var sockets = new Int32[2];
        Assert.That(Native.socketpair(1, 1, 0, sockets), Is.Zero);
        using var receiver = Handle(sockets[0]);
        using var sender = Handle(sockets[1]);
        using var inheritable = Handle(Native.dup(Fd(sender)));
        sender.Dispose();
        using var process = new CaffeinateProcessFactory().Start(KeepAwakeMode.MacOnly, TimeSpan.FromSeconds(15));
        try
        {
            inheritable.Dispose();
            Assert.That(Native.recv(Fd(receiver), new Byte[1], 1, 0x80).ToInt64(), Is.Zero,
                "The peer must receive EOF after host shutdown, even while caffeinate is alive.");
            Assert.That(process.HasExited, Is.False);
        }
        finally { process.Stop(); }
    }

    private static SafeFileHandle Handle(Int32 fd) => fd >= 0
        ? new SafeFileHandle((IntPtr)fd, ownsHandle: true)
        : throw new Win32Exception(Marshal.GetLastPInvokeError());

    private static Int32 Fd(SafeFileHandle handle) => handle.DangerousGetHandle().ToInt32();

    private static class Native
    {
        [DllImport("/usr/lib/libSystem.B.dylib", SetLastError = true)]
        internal static extern Int32 open(String path, Int32 flags);
        [DllImport("/usr/lib/libSystem.B.dylib", SetLastError = true)]
        internal static extern Int32 flock(Int32 fd, Int32 operation);
        [DllImport("/usr/lib/libSystem.B.dylib", SetLastError = true)]
        internal static extern Int32 fcntl(Int32 fd, Int32 command);
        [DllImport("/usr/lib/libSystem.B.dylib", SetLastError = true)]
        internal static extern Int32 dup(Int32 fd);
        [DllImport("/usr/lib/libSystem.B.dylib", SetLastError = true)]
        internal static extern Int32 socketpair(Int32 domain, Int32 type, Int32 protocol, [Out] Int32[] sockets);
        [DllImport("/usr/lib/libSystem.B.dylib", SetLastError = true)]
        internal static extern IntPtr recv(Int32 socket, [Out] Byte[] buffer, UIntPtr length, Int32 flags);
        [DllImport("/usr/lib/libSystem.B.dylib", SetLastError = true)]
        internal static extern Int32 getsid(Int32 pid);
        [DllImport("/usr/lib/libSystem.B.dylib", SetLastError = true)]
        internal static extern Int32 getpgid(Int32 pid);
        [DllImport("/usr/lib/libSystem.B.dylib", SetLastError = true)]
        internal static extern Int32 waitpid(Int32 pid, out Int32 status, Int32 options);
        [DllImport("/usr/lib/libSystem.B.dylib", SetLastError = true)]
        internal static extern Int32 kill(Int32 pid, Int32 signal);
    }
}
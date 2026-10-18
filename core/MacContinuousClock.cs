using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Cortado.Core;

public sealed class MacContinuousClock : TimeProvider
{
    public static String BootIdentity
    {
        get
        {
            if (!OperatingSystem.IsMacOS())
            {
                throw new PlatformNotSupportedException("macOS boot identity is required.");
            }

            var buffer = new Byte[128];
            var length = (UIntPtr)buffer.Length;
            if (sysctlbyname("kern.bootsessionuuid", buffer, ref length, IntPtr.Zero, 0) != 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not read the macOS boot identity.");
            }

            var identity = Encoding.UTF8.GetString(buffer, 0, checked((Int32)length)).TrimEnd('\0');
            return String.IsNullOrWhiteSpace(identity) ? throw new IOException("macOS returned an empty boot identity.") : identity;
        }
    }

    private readonly Int64 frequency;

    public MacContinuousClock()
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("macOS continuous clock is required.");
        }

        if (mach_timebase_info(out var info) != 0 || info.Numerator == 0 || info.Denominator == 0)
        {
            throw new IOException("Could not initialize the macOS continuous clock.");
        }

        frequency = checked((Int64)(1_000_000_000UL * info.Denominator / info.Numerator));
    }

    public override Int64 GetTimestamp() => checked((Int64)mach_continuous_time());
    public override Int64 TimestampFrequency => frequency;

    [StructLayout(LayoutKind.Sequential)]
    private struct TimebaseInfo
    {
        public UInt32 Numerator;
        public UInt32 Denominator;
    }

    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern Int32 mach_timebase_info(out TimebaseInfo info);

    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern UInt64 mach_continuous_time();

    [DllImport("/usr/lib/libSystem.B.dylib", SetLastError = true)]
    private static extern Int32 sysctlbyname(String name, [Out] Byte[] buffer, ref UIntPtr length,
        IntPtr newValue, UIntPtr newLength);
}
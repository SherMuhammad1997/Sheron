using System.Runtime.InteropServices;

namespace Sheron.Win;

/// <summary>
/// While video runs, asks Windows for 1 ms timer resolution and not to throttle this process (TRD › Video path).
/// With the default 15.6 ms tick the hardware encoder's own short waits stretched every frame to about 16 ms
/// (Intel Quick Sync, 25 Oct). Windows 11 ignores timer requests from processes with no visible window unless they
/// opt out, and the remote PC usually has Sheron's window hidden.
/// </summary>
internal sealed class TimerResolution : IDisposable
{
    private const uint Period = 1;
    private const uint NoError = 0;                         // TIMERR_NOERROR
    private const int ProcessPowerThrottling = 4;           // PROCESS_INFORMATION_CLASS
    private const uint ThrottlingVersion = 1;               // PROCESS_POWER_THROTTLING_CURRENT_VERSION
    private const uint ExecutionSpeed = 0x1;                // PROCESS_POWER_THROTTLING_EXECUTION_SPEED
    private const uint IgnoreTimerResolution = 0x4;         // PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION

    public TimerResolution()
    {
        // Control both kinds of throttling and turn them off (state 0).
        var state = new PowerThrottlingState
        {
            Version = ThrottlingVersion,
            ControlMask = ExecutionSpeed | IgnoreTimerResolution,
            StateMask = 0,
        };
        // Fails on Windows 10 before 1709, which has no such throttling: nothing to turn off there.
        SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, Marshal.SizeOf<PowerThrottlingState>());
        IsFine = timeBeginPeriod(Period) == NoError;
    }

    /// <summary>True when Windows granted the 1 ms timer.</summary>
    public bool IsFine { get; }

    public void Dispose()
    {
        if (IsFine) timeEndPeriod(Period);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(IntPtr process, int informationClass, ref PowerThrottlingState information, int size);
}

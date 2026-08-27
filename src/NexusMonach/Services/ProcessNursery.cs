using System.Runtime.InteropServices;

namespace NexusMonach.Services;

/// <summary>
/// Детская процессов: каждый AI-воркер (whisper, silero, piper, llama, node)
/// прописывается в один Job Object с флагом KILL_ON_JOB_CLOSE. Гарантия
/// физического уровня Windows: когда умирает браузер — ЛЮБОЙ смертью, даже
/// аварийной, — дети уходят вместе с ним. Больше ни одного зависшего
/// llama-server после закрытия.
/// </summary>
public static class ProcessNursery
{
    private static readonly object Gate = new();
    private static IntPtr _job;

    /// <summary>Помещает процесс в детскую. Вызывать сразу после старта.</summary>
    public static void Adopt(System.Diagnostics.Process process)
    {
        try
        {
            lock (Gate)
            {
                if (_job == IntPtr.Zero)
                {
                    _job = CreateJobObject(IntPtr.Zero, null);
                    var info = new JobObjectExtendedLimitInformation
                    {
                        BasicLimitInformation = new JobObjectBasicLimitInformation
                        {
                            LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                        }
                    };
                    if (!SetInformationJobObject(_job, JobObjectInfoClass.ExtendedLimitInformation,
                            ref info, System.Runtime.CompilerServices.Unsafe.SizeOf<JobObjectExtendedLimitInformation>()))
                    {
                        // Без флага детская бессмысленна — не adopting'аем вслепую.
                        return;
                    }
                }
                AssignProcessToJobObject(_job, process.Handle);
            }
        }
        catch
        {
            // Не смогли усыновить (например, процесс уже умер) — не страшно:
            // штатный Shutdown всё равно попытается его остановить.
        }
    }

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    private enum JobObjectInfoClass
    {
        ExtendedLimitInformation = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, JobObjectInfoClass infoClass,
        ref JobObjectExtendedLimitInformation lpJobObjectInformation, int cbJobObjectInformationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);
}

using System.Runtime.InteropServices;

namespace EvoOptimizer.Infrastructure.Native;

internal static class NativeMethods
{
    internal const int RelationProcessorCore = 0;
    internal const byte LtpPcSmt = 1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileTime
    {
        public uint Low;
        public uint High;
        public readonly ulong Value => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GROUP_AFFINITY
    {
        public UIntPtr Mask;
        public ushort Group;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
        public ushort[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESSOR_RELATIONSHIP
    {
        public byte Flags;
        public byte EfficiencyClass;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 20)]
        public byte[] Reserved;
        public ushort GroupCount;
        public GROUP_AFFINITY GroupMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX
    {
        public int Relationship;
        public int Size;
        public PROCESSOR_RELATIONSHIP Processor;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GlobalMemoryStatusEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    internal static bool TryGetMemoryStatus(out MemoryStatusEx status)
    {
        status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetLogicalProcessorInformationEx(
        int relationshipType,
        IntPtr buffer,
        ref int returnedLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessAffinityMask(
        IntPtr hProcess,
        out UIntPtr lpProcessAffinityMask,
        out UIntPtr lpSystemAffinityMask);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetProcessAffinityMask(
        IntPtr hProcess,
        UIntPtr dwProcessAffinityMask);
}

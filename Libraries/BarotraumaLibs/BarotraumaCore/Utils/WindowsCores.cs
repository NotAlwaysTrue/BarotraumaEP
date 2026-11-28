using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Barotrauma;

internal static class WindowsCores
{
    private const int InsufficientBuffer = 122;
    private static readonly IntPtr Process = new(-1);
    private static readonly IntPtr CurrentThread = new(-2);

    [DllImport("kernel32")]
    internal static extern int GetCurrentThreadId();
    [DllImport("kernel32", SetLastError = true)]
    private static extern SafeWaitHandle OpenThread(uint access, bool inherit, int id);

    [StructLayout(LayoutKind.Sequential)]
    private struct GroupAffinity
    {
        public nuint Mask;
        public ushort Group;
        private ushort reserved1, reserved2, reserved3;
    }

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool GetSystemCpuSetInformation([Out] byte[]? buffer, int size, out int needed, IntPtr process, uint flags);
    [DllImport("kernel32", SetLastError = true)]
    private static extern bool GetProcessDefaultCpuSets(IntPtr process, [Out] uint[]? ids, int count, out int needed);
    [DllImport("kernel32", SetLastError = true)]
    private static extern bool GetProcessGroupAffinity(IntPtr process, ref ushort count, [Out] ushort[] groups);
    [DllImport("kernel32", SetLastError = true)]
    private static extern bool GetProcessAffinityMask(IntPtr process, out nuint mask, out nuint systemMask);
    [DllImport("kernel32", SetLastError = true)]
    private static extern bool SetThreadGroupAffinity(IntPtr thread, in GroupAffinity affinity, IntPtr previous);

    internal static PhysicalCore[] Discover()
    {
        byte[] records = Array.Empty<byte>();
        int recordBytes;
        while (!GetSystemCpuSetInformation(records, records.Length, out recordBytes, Process, 0))
        {
            if (Marshal.GetLastPInvokeError() != InsufficientBuffer) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
            records = new byte[recordBytes];
        }
        uint[] defaults = Array.Empty<uint>();
        int defaultCount;
        while (!GetProcessDefaultCpuSets(Process, defaults, defaults.Length, out defaultCount))
        {
            if (Marshal.GetLastPInvokeError() != InsufficientBuffer) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
            defaults = new uint[defaultCount];
        }
        var defaultSets = defaults.Take(defaultCount).ToHashSet();
        ushort groupCount = 1;
        ushort[] groups = new ushort[groupCount];
        while (!GetProcessGroupAffinity(Process, ref groupCount, groups))
        {
            if (Marshal.GetLastPInvokeError() != InsufficientBuffer) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
            groups = new ushort[groupCount];
        }
        if (!GetProcessAffinityMask(Process, out nuint processMask, out _)) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }

        // SYSTEM_CPU_SET_INFORMATION records are variable length. CoreIndex is local to Group.
        var rows = new List<(ushort Group, int Core, int Cpu, int Performance, bool Allowed)>();
        for (int pos = 0; pos < recordBytes;)
        {
            int size = BitConverter.ToInt32(records, pos);
            if (size < 8 || size > recordBytes - pos) { throw new InvalidOperationException("Invalid Windows CPU set record."); }
            if (BitConverter.ToInt32(records, pos + 4) == 0)
            {
                uint set = BitConverter.ToUInt32(records, pos + 8);
                ushort group = BitConverter.ToUInt16(records, pos + 12);
                int logical = records[pos + 14];
                int flags = records[pos + 19];
                bool available = groups.Contains(group) && (defaultSets.Count == 0 || defaultSets.Contains(set)) &&
                    (flags & 6) != 2 && (groupCount != 1 || (processMask & ((nuint)1 << logical)) != 0);
                rows.Add((group, records[pos + 15], group * 64 + logical, records[pos + 18], available));
            }
            pos += size;
        }
        return rows.GroupBy(r => (r.Group, r.Core)).OrderBy(g => g.Key.Group).ThenBy(g => g.Key.Core)
            .Select((g, id) => new PhysicalCore(id, g.First().Performance, g.Where(r => r.Allowed).Select(r => r.Cpu).ToArray()))
            .Where(c => c.LogicalCpus.Length > 0).ToArray();
    }

    internal static void Bind(int[] cpus, int threadId)
    {
        var affinity = new GroupAffinity { Group = (ushort)(cpus[0] / 64) };
        foreach (int cpu in cpus) { affinity.Mask |= (nuint)1 << (cpu % 64); }
        // A dedicated thread can move when a pair of reserved cores becomes half empty.
        using var handle = threadId == 0 ? null : OpenThread(0x0020 /* THREAD_SET_INFORMATION */, false, threadId);
        if (handle is { IsInvalid: true }) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
        if (!SetThreadGroupAffinity(handle?.DangerousGetHandle() ?? CurrentThread, in affinity, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }
}

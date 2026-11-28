using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Barotrauma;

// Id names a physical core, while LogicalCpus contains the allowed SMT siblings.
internal sealed record PhysicalCore(int Id, int Performance, int[] LogicalCpus)
{
    internal static (PhysicalCore[] Cores, bool Automatic) Select(string[] args, out string? warning)
    {
        warning = null;
        int forced = Array.FindLastIndex(args, s => s.Equals("-forceusecore", StringComparison.OrdinalIgnoreCase));
        int counted = Array.FindLastIndex(args, s => s.Equals("-corecount", StringComparison.OrdinalIgnoreCase));
        var available = OperatingSystem.IsWindows() ? WindowsCores.Discover() :
            OperatingSystem.IsLinux() ? LinuxCores.Discover() : MacCores.Discover();
        if (available.Length == 0) { throw new InvalidOperationException("No available physical CPU cores."); }
        if (forced < 0 && counted < 0) { return (available, true); }

        int option = forced >= 0 ? forced : counted;
        if (option + 1 >= args.Length) { throw new ArgumentException($"Missing value for {args[option]}."); }
        string value = args[option + 1];
        int count;
        if (forced >= 0)
        {
            int[] ids = value.Split(',').Select(s => ParseNumber(s.Trim(), "-forceusecore")).ToArray();
            if (ids.Distinct().Count() != ids.Length) { throw new ArgumentException("Duplicate physical core in -forceusecore."); }
            if (!OperatingSystem.IsMacOS())
            {
                var byId = available.ToDictionary(core => core.Id);
                return (ids.Select(id => byId.TryGetValue(id, out var core) ? core :
                    throw new ArgumentException($"Physical core {id} is unavailable. Available IDs: {string.Join(",", byId.Keys)}.")).ToArray(), false);
            }
            count = Math.Min(ids.Length, available.Length);
            warning = $"-forceusecore is unavailable on macOS. Using -corecount {count}; macOS chooses the actual cores.";
        }
        else
        {
            count = ParseNumber(value, "-corecount");
        }
        if (count < 1 || count > available.Length)
        {
            throw new ArgumentException($"Requested {count} cores; available physical core count is {available.Length}.");
        }
        return (available.Take(count).ToArray(), false);
    }

    private static int ParseNumber(string text, string option) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
            ? number : throw new ArgumentException($"Invalid integer '{text}' for {option}.");

    internal static int CurrentNativeThreadId => OperatingSystem.IsWindows() ? WindowsCores.GetCurrentThreadId() :
        OperatingSystem.IsLinux() ? LinuxCores.gettid() : 0;

    internal void Bind(int threadId = 0)
    {
        if (OperatingSystem.IsWindows()) { WindowsCores.Bind(LogicalCpus, threadId); }
        else if (OperatingSystem.IsLinux()) { LinuxCores.Bind(LogicalCpus, threadId); }
    }
}

internal static class MacCores
{
    [DllImport("libSystem.B.dylib")]
    private static extern int sysctlbyname(string name, out int value, ref nuint size, IntPtr newValue, nuint newSize);

    private static int Read(string name)
    {
        nuint size = sizeof(int);
        return sysctlbyname(name, out int value, ref size, IntPtr.Zero, 0) == 0 ? value : 0;
    }

    internal static PhysicalCore[] Discover()
    {
        int count = Read("hw.physicalcpu");
        if (count == 0) { throw new InvalidOperationException("Could not read hw.physicalcpu."); }
        int fast = Read("hw.nperflevels") > 1 ? Read("hw.perflevel0.physicalcpu") : count;
        return Enumerable.Range(0, count).Select(i => new PhysicalCore(i, i < fast ? 1 : 0, Array.Empty<int>())).ToArray();
    }
}

internal static class LinuxCores
{
    [DllImport("libc")]
    internal static extern int gettid();
    [DllImport("libc", SetLastError = true)]
    private static extern int sched_getaffinity(int pid, nuint size, [Out] byte[] mask);
    [DllImport("libc", SetLastError = true)]
    private static extern int sched_setaffinity(int pid, nuint size, byte[] mask);

    private static IEnumerable<int> CpuList(string path)
    {
        foreach (string range in File.ReadAllText(path).Trim().Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = range.Split('-');
            int first = int.Parse(parts[0], CultureInfo.InvariantCulture);
            int last = int.Parse(parts[^1], CultureInfo.InvariantCulture);
            for (int cpu = first; cpu <= last; cpu++) { yield return cpu; }
        }
    }

    internal static PhysicalCore[] Discover()
    {
        const string sys = "/sys/devices/system/cpu";
        byte[] mask = new byte[128];
        while (sched_getaffinity(0, (nuint)mask.Length, mask) != 0)
        {
            if (Marshal.GetLastPInvokeError() != 22) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
            Array.Resize(ref mask, checked(mask.Length * 2));
        }
        const string pCoreFile = "/sys/bus/event_source/devices/cpu_core/cpus";
        var fast = File.Exists(pCoreFile) ? CpuList(pCoreFile).ToHashSet() : null;
        var result = new List<PhysicalCore>();
        var seen = new HashSet<int>();
        int id = 0;
        foreach (int cpu in CpuList(sys + "/online").Order())
        {
            string dir = $"{sys}/cpu{cpu}";
            int[] siblings = CpuList(dir + "/topology/thread_siblings_list").ToArray();
            if (!seen.Add(siblings.Min())) { continue; }
            int[] allowed = siblings.Where(n => n / 8 < mask.Length && (mask[n / 8] & (1 << (n % 8))) != 0).ToArray();
            if (allowed.Length > 0)
            {
                int performance = 0;
                if (fast != null) { performance = fast.Contains(cpu) ? 1 : 0; }
                else if (File.Exists(dir + "/cpu_capacity"))
                {
                    performance = int.Parse(File.ReadAllText(dir + "/cpu_capacity").Trim(), CultureInfo.InvariantCulture);
                }
                result.Add(new PhysicalCore(id, performance, allowed));
            }
            id++;
        }
        return result.ToArray();
    }

    internal static void Bind(int[] cpus, int threadId)
    {
        var mask = new byte[(cpus.Max() / 64 + 1) * 8];
        foreach (int cpu in cpus) { mask[cpu / 8] |= (byte)(1 << (cpu % 8)); }
        if (sched_setaffinity(threadId, (nuint)mask.Length, mask) != 0) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
    }
}

using System;
using System.Runtime.InteropServices;
using UnityEngine.Profiling;
using UnityEngine.Scripting;

namespace CactusPie.RamCleanerInterval
{
    /// <summary>
    /// One snapshot of every memory number the plugin cares about. All values are bytes.
    /// Sampled at most once per second from Update, so nothing here is on a hot path.
    /// </summary>
    internal struct MemorySnapshot
    {
        public long MonoUsed;
        public long MonoReserved;
        public long WorkingSet;
        public long PrivateBytes;
        public long SystemTotal;
        public long SystemAvailable;

        /// <summary>Commit limit (RAM + page file) and how much of it is still free. When this runs out,
        /// allocations fail and the game crashes - it can happen with free RAM left if the page file is small.</summary>
        public long CommitLimit;
        public long CommitAvailable;
        public GarbageCollector.Mode GcMode;

        /// <summary>Everything the process committed that is not the Mono heap: Unity objects, textures,
        /// meshes, audio, physics, native plugin memory. This is where "the game keeps growing" usually is.</summary>
        public long Native => PrivateBytes > 0 ? Math.Max(0, PrivateBytes - MonoReserved) : -1;

        public float SystemAvailablePercent =>
            SystemTotal > 0 ? SystemAvailable * 100f / SystemTotal : 100f;
    }

    /// <summary>
    /// Win32 memory queries plus the working set trim the original mod did through the game's
    /// InGameMemoryManagement.EmptyWorkingSet (which is just psapi!EmptyWorkingSet on the current process).
    /// Calling psapi directly keeps us independent of the game class name and skips its Debug.Log spam.
    /// </summary>
    internal static class MemoryStats
    {
        public const double BytesPerGb = 1024d * 1024d * 1024d;

        private static bool _win32Failed;

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessMemoryCountersEx
        {
            public uint cb;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize;
            public UIntPtr WorkingSetSize;
            public UIntPtr QuotaPeakPagedPoolUsage;
            public UIntPtr QuotaPagedPoolUsage;
            public UIntPtr QuotaPeakNonPagedPoolUsage;
            public UIntPtr QuotaNonPagedPoolUsage;
            public UIntPtr PagefileUsage;
            public UIntPtr PeakPagefileUsage;
            public UIntPtr PrivateUsage;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
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

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCountersEx counters, uint size);

        [DllImport("psapi.dll", EntryPoint = "EmptyWorkingSet", SetLastError = true)]
        private static extern bool EmptyWorkingSetNative(IntPtr process);

        public static long MonoUsed()
        {
            try
            {
                long used = Profiler.GetMonoUsedSizeLong();
                if (used > 0)
                {
                    return used;
                }
            }
            catch
            {
                // fall through
            }

            return GC.GetTotalMemory(false);
        }

        public static MemorySnapshot Sample()
        {
            var snapshot = new MemorySnapshot
            {
                MonoUsed = MonoUsed(),
                GcMode = GarbageCollector.GCMode,
            };

            try
            {
                snapshot.MonoReserved = Profiler.GetMonoHeapSizeLong();
            }
            catch
            {
                snapshot.MonoReserved = snapshot.MonoUsed;
            }

            if (_win32Failed)
            {
                return snapshot;
            }

            try
            {
                var counters = new ProcessMemoryCountersEx
                {
                    cb = (uint)Marshal.SizeOf(typeof(ProcessMemoryCountersEx)),
                };

                if (GetProcessMemoryInfo(GetCurrentProcess(), ref counters, counters.cb))
                {
                    snapshot.WorkingSet = (long)counters.WorkingSetSize.ToUInt64();
                    snapshot.PrivateBytes = (long)counters.PrivateUsage.ToUInt64();
                }

                var status = new MemoryStatusEx
                {
                    dwLength = (uint)Marshal.SizeOf(typeof(MemoryStatusEx)),
                };

                if (GlobalMemoryStatusEx(ref status))
                {
                    snapshot.SystemTotal = (long)status.ullTotalPhys;
                    snapshot.SystemAvailable = (long)status.ullAvailPhys;
                    snapshot.CommitLimit = (long)status.ullTotalPageFile;
                    snapshot.CommitAvailable = (long)status.ullAvailPageFile;
                }
            }
            catch (Exception)
            {
                // Not on Windows (or psapi missing): keep the Mono numbers, stop retrying every second.
                _win32Failed = true;
            }

            return snapshot;
        }

        /// <summary>Working set in bytes (-1 if unknown). Win32 only, safe to call from any thread.</summary>
        public static long ReadWorkingSet()
        {
            try
            {
                var counters = new ProcessMemoryCountersEx
                {
                    cb = (uint)Marshal.SizeOf(typeof(ProcessMemoryCountersEx)),
                };
                return GetProcessMemoryInfo(GetCurrentProcess(), ref counters, counters.cb)
                    ? (long)counters.WorkingSetSize.ToUInt64()
                    : -1;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        /// <summary>Committed (private) bytes of this process, -1 if unknown. Win32 only, safe to call from any thread.</summary>
        public static long ReadPrivateBytes()
        {
            try
            {
                var counters = new ProcessMemoryCountersEx
                {
                    cb = (uint)Marshal.SizeOf(typeof(ProcessMemoryCountersEx)),
                };
                return GetProcessMemoryInfo(GetCurrentProcess(), ref counters, counters.cb)
                    ? (long)counters.PrivateUsage.ToUInt64()
                    : -1;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        /// <summary>
        /// Pushes every page of the game out of RAM. Frees nothing - the pages go to the standby list /
        /// page file and are faulted back in when touched, which is exactly the stutter people notice
        /// after the original mod fired. Only worth it when the system itself is short on RAM.
        /// </summary>
        public static bool EmptyWorkingSet()
        {
            try
            {
                return EmptyWorkingSetNative(GetCurrentProcess());
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static string Gb(long bytes)
        {
            return (bytes / BytesPerGb).ToString("0.00");
        }

        public static string ModeName(GarbageCollector.Mode mode)
        {
            switch (mode)
            {
                case GarbageCollector.Mode.Disabled:
                    return Loc.L("꺼짐", "off");
                case GarbageCollector.Mode.Enabled:
                    return Loc.L("켜짐(자동)", "on (automatic)");
                case GarbageCollector.Mode.Manual:
                    return Loc.L("수동", "manual");
                default:
                    return mode.ToString();
            }
        }
    }
}

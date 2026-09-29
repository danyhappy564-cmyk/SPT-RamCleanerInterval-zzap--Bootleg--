using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace CactusPie.RamCleanerInterval
{
    /// <summary>
    /// Reads how much video memory this game process uses, from the same Windows performance counters
    /// Task Manager shows ("GPU Process Memory" -> Dedicated / Shared Usage). Unity has no release-build
    /// API for this. Runs on its own background thread every few seconds because enumerating the counter
    /// instances can take tens of milliseconds - never on the game's main thread.
    /// </summary>
    internal sealed class VramMonitor : IDisposable
    {
        private const uint PdhFmtLarge = 0x00000400;
        private const uint PdhMoreData = 0x800007D2;
        private const int IntervalMs = 5000;

        private readonly string _pidPrefix;
        private readonly Thread _thread;
        private volatile bool _stop;

        private long _dedicated = -1;
        private long _shared = -1;

        [StructLayout(LayoutKind.Sequential)]
        private struct PdhFmtCounterValueItem
        {
            public IntPtr Name;
            public uint CStatus;
            public long LargeValue;
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhOpenQuery(string dataSource, IntPtr userData, out IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhAddEnglishCounter(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);

        [DllImport("pdh.dll")]
        private static extern uint PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);

        [DllImport("pdh.dll")]
        private static extern uint PdhCloseQuery(IntPtr query);

        public VramMonitor()
        {
            _pidPrefix = "pid_" + Process.GetCurrentProcess().Id + "_";
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "RamCleaner VRAM monitor",
                Priority = ThreadPriority.BelowNormal,
            };
            _thread.Start();
        }

        /// <summary>Dedicated VRAM used by this process in bytes, -1 if unknown.</summary>
        public long Dedicated => Interlocked.Read(ref _dedicated);

        /// <summary>Shared (system RAM used as video memory) in bytes, -1 if unknown.</summary>
        public long Shared => Interlocked.Read(ref _shared);

        public bool Failed { get; private set; }

        public void Dispose()
        {
            _stop = true;
        }

        private void Run()
        {
            IntPtr query = IntPtr.Zero;
            try
            {
                if (PdhOpenQuery(null, IntPtr.Zero, out query) != 0 ||
                    PdhAddEnglishCounter(query, @"\GPU Process Memory(*)\Dedicated Usage", IntPtr.Zero, out IntPtr dedicated) != 0 ||
                    PdhAddEnglishCounter(query, @"\GPU Process Memory(*)\Shared Usage", IntPtr.Zero, out IntPtr shared) != 0)
                {
                    Failed = true;
                    return;
                }

                while (!_stop)
                {
                    if (PdhCollectQueryData(query) == 0)
                    {
                        Interlocked.Exchange(ref _dedicated, SumForThisProcess(dedicated));
                        Interlocked.Exchange(ref _shared, SumForThisProcess(shared));
                    }

                    Thread.Sleep(IntervalMs);
                }
            }
            catch (Exception)
            {
                // No pdh.dll / counters (old Windows, Wine): VRAM just shows as unknown.
                Failed = true;
            }
            finally
            {
                if (query != IntPtr.Zero)
                {
                    PdhCloseQuery(query);
                }
            }
        }

        private long SumForThisProcess(IntPtr counter)
        {
            uint size = 0;
            uint status = PdhGetFormattedCounterArray(counter, PdhFmtLarge, ref size, out uint count, IntPtr.Zero);
            if (status != PdhMoreData || size == 0)
            {
                return -1;
            }

            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArray(counter, PdhFmtLarge, ref size, out count, buffer) != 0)
                {
                    return -1;
                }

                long total = 0;
                bool found = false;
                int stride = Marshal.SizeOf(typeof(PdhFmtCounterValueItem));
                for (int i = 0; i < count; i++)
                {
                    var item = (PdhFmtCounterValueItem)Marshal.PtrToStructure(new IntPtr(buffer.ToInt64() + i * stride), typeof(PdhFmtCounterValueItem));
                    string name = Marshal.PtrToStringUni(item.Name);
                    if (name != null && name.StartsWith(_pidPrefix, StringComparison.Ordinal))
                    {
                        total += item.LargeValue;
                        found = true;
                    }
                }

                return found ? total : -1;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }
}

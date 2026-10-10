using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace CactusPie.RamCleanerInterval
{
    /// <summary>
    /// Reads video memory from the same Windows performance counters Task Manager shows, per graphics card:
    /// "GPU Process Memory" (this game only) and "GPU Adapter Memory" (every program on the card).
    /// Counter instances carry the card's LUID ("luid_0x..._0x..._phys_0"), so a second card - e.g. the one running
    /// Lossless Scaling frame generation in a dual-GPU setup - is kept apart instead of being added to the game's card.
    /// The game's card is the one where this process has the most dedicated memory. Card names and sizes come from the
    /// display-adapter registry keys (no LUID there, so a name is attached only when it is unambiguous).
    /// Runs on its own background thread every few seconds because enumerating the counter instances can take tens of
    /// milliseconds - never on the game's main thread.
    /// </summary>
    internal sealed class VramMonitor : IDisposable
    {
        private const uint PdhFmtLarge = 0x00000400;
        private const uint PdhMoreData = 0x800007D2;
        private const int IntervalMs = 5000;
        private const long MinShownBytes = 64L * 1024 * 1024; // smaller "other card" amounts are just bookkeeping

        private readonly string _pidPrefix;
        private readonly string _renderName;
        private readonly long _renderTotal;
        private readonly Thread _thread;
        private volatile bool _stop;
        private volatile Reading _reading = new Reading();
        private List<KeyValuePair<string, long>> _registryCards;

        /// <summary>One measurement. Bytes, -1 = unknown.</summary>
        internal sealed class Reading
        {
            public long Game = -1;          // this game, dedicated, on its own card
            public long GameShared = -1;    // this game, shared (system RAM used as video memory - what spilled over), on its card
            public long GameOnOthers;       // this game's dedicated memory on other cards (frame copies to the display card)
            public long CardUsed = -1;      // every program on the game's card
            public long CardTotal = -1;
            public List<Gpu> Others = new List<Gpu>();
        }

        internal sealed class Gpu
        {
            public string Name;  // null when it can't be told apart
            public long Used;    // every program on that card
            public long Total;   // -1 when unknown
        }

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

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegGetValueW(IntPtr hkey, string subKey, string value, uint flags, out uint type, byte[] data, ref uint size);

        private static readonly IntPtr HkeyLocalMachine = new IntPtr(unchecked((int)0x80000002));
        private const uint RrfRtAny = 0x0000ffff;
        private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\";

        /// <param name="renderName">SystemInfo.graphicsDeviceName (read on the main thread).</param>
        /// <param name="renderTotal">SystemInfo.graphicsMemorySize in bytes.</param>
        public VramMonitor(string renderName, long renderTotal)
        {
            _pidPrefix = "pid_" + Process.GetCurrentProcess().Id + "_";
            _renderName = renderName ?? string.Empty;
            _renderTotal = renderTotal > 0 ? renderTotal : -1;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "RamCleaner VRAM monitor",
                Priority = ThreadPriority.BelowNormal,
            };
            _thread.Start();
        }

        /// <summary>Dedicated VRAM this game uses on its own card, in bytes, -1 if unknown.</summary>
        public long Dedicated => _reading.Game;

        /// <summary>Shared (system RAM used as video memory) for this game on its card, in bytes, -1 if unknown.</summary>
        public long Shared => _reading.GameShared;

        /// <summary>The latest full reading (replaced as a whole, safe to read from any thread).</summary>
        public Reading Last => _reading;

        public bool Failed { get; private set; }

        public void Dispose()
        {
            _stop = true;
        }

        /// <summary>
        /// "game card: 15.3 / 16.0 GB (other programs 0.4 GB) · other card RTX 3060: 2.1 / 12.0 GB" style summary, or empty
        /// when there's nothing beyond the game's own number. Korean/English by Loc.
        /// </summary>
        public string CardSummary()
        {
            Reading r = _reading;
            var sb = new StringBuilder();
            if (r.CardUsed >= 0 && r.CardTotal > 0)
            {
                sb.Append(Loc.L("게임 그래픽카드 전체: ", "game's card, all programs: ")).Append(MemoryStats.Gb(r.CardUsed)).Append(" / ")
                  .Append(MemoryStats.Gb(r.CardTotal)).Append(" GB");
                long others = r.Game >= 0 ? r.CardUsed - r.Game : -1;
                if (others >= MinShownBytes)
                {
                    sb.Append(Loc.L($" (다른 프로그램 {MemoryStats.Gb(others)} GB)", $" (other programs {MemoryStats.Gb(others)} GB)"));
                }
            }

            foreach (Gpu gpu in r.Others)
            {
                if (sb.Length > 0)
                {
                    sb.Append(" · ");
                }

                sb.Append(Loc.L("다른 그래픽카드", "other card")).Append(gpu.Name != null ? " " + gpu.Name : string.Empty).Append(": ")
                  .Append(MemoryStats.Gb(gpu.Used)).Append(gpu.Total > 0 ? " / " + MemoryStats.Gb(gpu.Total) : string.Empty).Append(" GB");
            }

            if (r.GameOnOthers >= MinShownBytes)
            {
                sb.Append(Loc.L($" (이 게임이 다른 카드에 {MemoryStats.Gb(r.GameOnOthers)} GB — 화면 복사용, 위 VRAM에선 뺌)",
                                $" (this game holds {MemoryStats.Gb(r.GameOnOthers)} GB on another card — frame copies, left out of VRAM above)"));
            }

            return sb.ToString();
        }

        /// <summary>English one-liner for the [mem] log line ("" when there is nothing beyond the game's own number).</summary>
        public string LogText()
        {
            Reading r = _reading;
            var sb = new StringBuilder();
            if (r.CardUsed >= 0)
            {
                sb.Append($", card all programs {MemoryStats.Gb(r.CardUsed)}/{MemoryStats.Gb(r.CardTotal)} GB");
            }

            foreach (Gpu gpu in r.Others)
            {
                sb.Append($", other card{(gpu.Name != null ? " " + gpu.Name : string.Empty)} {MemoryStats.Gb(gpu.Used)}{(gpu.Total > 0 ? "/" + MemoryStats.Gb(gpu.Total) : string.Empty)} GB");
            }

            if (r.GameOnOthers >= MinShownBytes)
            {
                sb.Append($", game on other card {MemoryStats.Gb(r.GameOnOthers)} GB");
            }

            return sb.ToString();
        }

        private void Run()
        {
            IntPtr query = IntPtr.Zero;
            try
            {
                try
                {
                    _registryCards = ReadRegistryCards();
                }
                catch (Exception)
                {
                    _registryCards = new List<KeyValuePair<string, long>>();
                }

                if (PdhOpenQuery(null, IntPtr.Zero, out query) != 0 ||
                    PdhAddEnglishCounter(query, @"\GPU Process Memory(*)\Dedicated Usage", IntPtr.Zero, out IntPtr dedicated) != 0 ||
                    PdhAddEnglishCounter(query, @"\GPU Process Memory(*)\Shared Usage", IntPtr.Zero, out IntPtr shared) != 0)
                {
                    Failed = true;
                    return;
                }

                // Whole-card usage (Windows 10 1709+). Without it the game's own numbers still work.
                bool haveAdapter = PdhAddEnglishCounter(query, @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out IntPtr adapter) == 0;

                while (!_stop)
                {
                    if (PdhCollectQueryData(query) == 0)
                    {
                        _reading = Measure(dedicated, shared, haveAdapter ? adapter : IntPtr.Zero);
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

        private Reading Measure(IntPtr dedicatedCounter, IntPtr sharedCounter, IntPtr adapterCounter)
        {
            return Combine(PerCard(dedicatedCounter, true), PerCard(sharedCounter, true),
                adapterCounter != IntPtr.Zero ? PerCard(adapterCounter, false) : null);
        }

        /// <summary>Turns per-card counter sums (keyed "luid_..._phys_N") into one reading. Separate from PDH so it can be tested.</summary>
        private Reading Combine(Dictionary<string, long> gameDedicated, Dictionary<string, long> gameShared, Dictionary<string, long> cards)
        {
            var reading = new Reading();
            if (gameDedicated == null || gameDedicated.Count == 0)
            {
                return reading;
            }

            // The game's card: where it keeps the most. A display card in a dual-GPU setup only holds frame copies.
            string gameCard = gameDedicated.OrderByDescending(kv => kv.Value).First().Key;
            reading.Game = gameDedicated[gameCard];
            reading.GameOnOthers = gameDedicated.Where(kv => kv.Key != gameCard).Sum(kv => kv.Value);

            reading.GameShared = gameShared != null && gameShared.TryGetValue(gameCard, out long sharedBytes) ? sharedBytes : -1;

            reading.CardTotal = _renderTotal;
            if (cards != null)
            {
                reading.CardUsed = cards.TryGetValue(gameCard, out long used) ? used : -1;

                // Registry cards other than the game's (by name) with a real size; a name is attached only if exactly one fits.
                List<KeyValuePair<string, long>> spare = _registryCards
                    .Where(c => !string.Equals(c.Key, _renderName, StringComparison.OrdinalIgnoreCase) && c.Value >= 1024L * 1024 * 1024)
                    .ToList();
                List<KeyValuePair<string, long>> otherCards = cards.Where(kv => kv.Key != gameCard && kv.Value >= MinShownBytes).ToList();
                foreach (KeyValuePair<string, long> card in otherCards)
                {
                    bool named = otherCards.Count == 1 && spare.Count == 1;
                    reading.Others.Add(new Gpu
                    {
                        Name = named ? spare[0].Key : null,
                        Used = card.Value,
                        Total = named ? spare[0].Value : -1,
                    });
                }
            }

            return reading;
        }

        /// <summary>Counter values summed per card ("luid_..._phys_N"); process counters keep only this process. Null on failure.</summary>
        private Dictionary<string, long> PerCard(IntPtr counter, bool thisProcessOnly)
        {
            uint size = 0;
            uint status = PdhGetFormattedCounterArray(counter, PdhFmtLarge, ref size, out uint count, IntPtr.Zero);
            if (status != PdhMoreData || size == 0)
            {
                return null;
            }

            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArray(counter, PdhFmtLarge, ref size, out count, buffer) != 0)
                {
                    return null;
                }

                var result = new Dictionary<string, long>();
                int stride = Marshal.SizeOf(typeof(PdhFmtCounterValueItem));
                for (int i = 0; i < count; i++)
                {
                    var item = (PdhFmtCounterValueItem)Marshal.PtrToStructure(new IntPtr(buffer.ToInt64() + i * stride), typeof(PdhFmtCounterValueItem));
                    string name = Marshal.PtrToStringUni(item.Name);
                    if (name == null || (thisProcessOnly && !name.StartsWith(_pidPrefix, StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    int luid = name.IndexOf("luid_", StringComparison.Ordinal);
                    string card = luid >= 0 ? name.Substring(luid) : name;
                    result.TryGetValue(card, out long sum);
                    result[card] = sum + item.LargeValue;
                }

                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>(name, VRAM bytes) of every display adapter in the registry ("0000".."0015" under the display class key).</summary>
        private static List<KeyValuePair<string, long>> ReadRegistryCards()
        {
            var cards = new List<KeyValuePair<string, long>>();
            for (int i = 0; i < 16; i++)
            {
                string key = DisplayClassKey + i.ToString("0000");
                byte[] desc = ReadValue(key, "DriverDesc");
                if (desc == null)
                {
                    continue;
                }

                string name = Encoding.Unicode.GetString(desc).TrimEnd('\0');
                byte[] size = ReadValue(key, "HardwareInformation.qwMemorySize") ?? ReadValue(key, "HardwareInformation.MemorySize");
                long bytes = size == null ? -1 : size.Length >= 8 ? BitConverter.ToInt64(size, 0) : size.Length >= 4 ? BitConverter.ToUInt32(size, 0) : -1;
                cards.Add(new KeyValuePair<string, long>(name, bytes));
            }

            return cards;
        }

        private static byte[] ReadValue(string key, string value)
        {
            uint size = 0;
            if (RegGetValueW(HkeyLocalMachine, key, value, RrfRtAny, out _, null, ref size) != 0 || size == 0)
            {
                return null;
            }

            var data = new byte[size];
            return RegGetValueW(HkeyLocalMachine, key, value, RrfRtAny, out _, data, ref size) == 0 ? data : null;
        }
    }
}

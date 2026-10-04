using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Logging;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace CactusPie.RamCleanerInterval
{
    /// <summary>
    /// The SPT server as the game sees it. Works with any server mod or none - it only looks from the outside:
    /// <list type="bullet">
    ///   <item><b>Memory</b> of the SPT server process (it runs on the same PC and shares the same RAM).</item>
    ///   <item><b>Bot generation</b> round trips (<c>ClientBackendSession.LoadBots</c>, i.e. /client/game/bot/generate):
    ///     a slow answer here is a late spawn in raid, and heavy bot mods (APBS gear) show up as rising times.</item>
    ///   <item><b>Main-thread server waits</b>: mods that call SPT's synchronous <c>RequestHandler.GetJson/PostJson/
    ///     PutJson/GetData</c> freeze the game until the server answers. The hitch detector uses this to name the cause
    ///     ("server wait /orbit/config") instead of blaming the mod's code or "the game".</item>
    /// </list>
    /// Patches are installed once, in the main menu, by type name (no compile-time dependency on spt-common).
    /// </summary>
    internal sealed class ServerMonitor
    {
        private const int ProcessQueryLimitedInformation = 0x1000;
        private const uint StillActive = 259;
        private const float SearchIntervalSeconds = 30f;

        private static ServerMonitor s_instance;

        private readonly ManualLogSource _log;
        private readonly object _gate = new object();
        private int _pid = -1;
        private int _searching;
        private float _nextSearch;
        private bool _installed;

        // Bot generation (this raid)
        private int _botCalls;
        private int _botCount;
        private double _botTotalMs;
        private double _botMaxMs;
        private double _botLastMs = -1;
        private int _botFailures;

        // Main-thread waits (this raid)
        private int _waitCount;
        private double _waitMaxMs;
        private string _waitMaxPath;
        private readonly Dictionary<string, int> _waitPaths = new Dictionary<string, int>();

        // Accumulated per frame, read by the hitch check of the next frame.
        private int _frameWaitFrame = -1;
        private double _frameWaitMs;
        private string _frameWaitPath;

        private static int s_mainThreadId;

        /// <summary>Frame number source; Unity's in game, replaceable so the logic can be exercised outside Unity.</summary>
        internal static Func<int> FrameCounter = () => Time.frameCount;

        public ServerMonitor(ManualLogSource log)
        {
            _log = log;
            s_instance = this;
            s_mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        public long PrivateBytes { get; private set; } = -1;

        public long WorkingSet { get; private set; } = -1;

        public string ProcessName { get; private set; }

        public bool Found => _pid > 0;

        private string _patchStatus;

        public string PatchStatus { get => _patchStatus ?? Loc.L("아직 설치 안 함 (메인 메뉴에서 설치)", "not installed yet (installs in the main menu)"); private set => _patchStatus = value; }

        // ---------------------------------------------------------------- process memory

        /// <summary>Call once per second. Finding the process (enumerating every process) runs on the thread pool.</summary>
        public void Sample(float now)
        {
            int pid = _pid;
            if (pid <= 0)
            {
                PrivateBytes = WorkingSet = -1;
                if (now >= _nextSearch && Interlocked.Exchange(ref _searching, 1) == 0)
                {
                    _nextSearch = now + SearchIntervalSeconds;
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        {
                            Find();
                        }
                        finally
                        {
                            Interlocked.Exchange(ref _searching, 0);
                        }
                    });
                }

                return;
            }

            if (!Read(pid, out long workingSet, out long privateBytes))
            {
                _log.LogInfo($"[server] SPT server process {ProcessName} ({pid}) is gone - searching again");
                _pid = -1;
                PrivateBytes = WorkingSet = -1;
                return;
            }

            WorkingSet = workingSet;
            PrivateBytes = privateBytes;
        }

        private void Find()
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcesses();
            }
            catch (Exception)
            {
                return;
            }

            int best = -1;
            string bestName = null;
            foreach (Process process in processes)
            {
                try
                {
                    string name = process.ProcessName;
                    if (name.Equals("SPT.Server", StringComparison.OrdinalIgnoreCase))
                    {
                        best = process.Id;
                        bestName = name;
                        break;
                    }

                    if (best < 0 && name.IndexOf("spt", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        name.IndexOf("server", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        best = process.Id;
                        bestName = name;
                    }
                }
                catch (Exception)
                {
                    // protected or already exited
                }
                finally
                {
                    process.Dispose();
                }
            }

            if (best > 0 && best != _pid)
            {
                ProcessName = bestName;
                _pid = best;
                _log.LogInfo($"[server] found SPT server process: {bestName} ({best})");
            }
        }

        private static bool Read(int pid, out long workingSet, out long privateBytes)
        {
            workingSet = privateBytes = -1;
            IntPtr handle = IntPtr.Zero;
            try
            {
                handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
                if (handle == IntPtr.Zero)
                {
                    return false;
                }

                if (GetExitCodeProcess(handle, out uint code) && code != StillActive)
                {
                    return false;
                }

                var counters = new ProcessMemoryCountersEx { cb = (uint)Marshal.SizeOf(typeof(ProcessMemoryCountersEx)) };
                if (!GetProcessMemoryInfo(handle, ref counters, counters.cb))
                {
                    return false;
                }

                workingSet = (long)counters.WorkingSetSize.ToUInt64();
                privateBytes = (long)counters.PrivateUsage.ToUInt64();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                if (handle != IntPtr.Zero)
                {
                    CloseHandle(handle);
                }
            }
        }

        // ---------------------------------------------------------------- patches

        /// <summary>Install the request patches once (main menu: SPT's assemblies are loaded by then).</summary>
        public void Install()
        {
            if (_installed)
            {
                return;
            }

            _installed = true;
            var harmony = new Harmony("com.cactuspie.ramcleanerinterval.server");
            var parts = new List<string>();
            bool botsHooked = false;

            try
            {
                MethodBase loadBots = AccessTools.Method(typeof(ClientBackendSession), nameof(ClientBackendSession.LoadBots));
                if (loadBots != null)
                {
                    harmony.Patch(loadBots,
                        prefix: new HarmonyMethod(AccessTools.Method(typeof(ServerMonitor), nameof(StampPrefix))),
                        postfix: new HarmonyMethod(AccessTools.Method(typeof(ServerMonitor), nameof(LoadBotsPostfix))));
                    botsHooked = true;
                    parts.Add(Loc.L("봇 생성 응답", "bot generation response"));
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning($"[server] could not watch bot generation: {ex.Message}");
            }

            Type requestHandler = AccessTools.TypeByName("SPT.Common.Http.RequestHandler");
            int sync = 0;
            if (requestHandler != null)
            {
                foreach (string name in new[] { "GetJson", "PostJson", "PutJson", "GetData" })
                {
                    try
                    {
                        MethodInfo method = AccessTools.Method(requestHandler, name);
                        if (method == null)
                        {
                            continue;
                        }

                        harmony.Patch(method,
                            prefix: new HarmonyMethod(AccessTools.Method(typeof(ServerMonitor), nameof(StampPrefix))),
                            finalizer: new HarmonyMethod(AccessTools.Method(typeof(ServerMonitor), nameof(SyncFinalizer))));
                        sync++;
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning($"[server] could not watch RequestHandler.{name}: {ex.Message}");
                    }
                }
            }

            if (sync > 0)
            {
                parts.Add(Loc.L($"모드의 동기 요청 {sync}종", $"{sync} kinds of synchronous mod requests"));
            }

            PatchStatus = parts.Count > 0 ? Loc.L("설치됨: ", "installed: ") + string.Join(", ", parts) : Loc.L("설치 실패 (대상 없음)", "install failed (nothing to hook)");
            _log.LogInfo($"[server] request watch installed: bot generation {(botsHooked ? "yes" : "no")}, synchronous RequestHandler calls {sync}");
        }

        private static void StampPrefix(out long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        private static void LoadBotsPostfix(long __state, Task<Profile[]> __result, List<CountTypeBotWave> conditions)
        {
            ServerMonitor self = s_instance;
            if (self == null || __result == null)
            {
                return;
            }

            int asked = 0;
            try
            {
                asked = conditions?.Sum(c => c.Limit) ?? 0;
            }
            catch (Exception)
            {
                // informational only
            }

            __result.ContinueWith(task => self.RecordBots(task, __state, asked), TaskContinuationOptions.ExecuteSynchronously);
        }

        private void RecordBots(Task<Profile[]> task, long start, int asked)
        {
            double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            lock (_gate)
            {
                if (task.Status != TaskStatus.RanToCompletion)
                {
                    _botFailures++;
                    return;
                }

                _botCalls++;
                _botCount += task.Result?.Length ?? 0;
                _botTotalMs += ms;
                _botMaxMs = Math.Max(_botMaxMs, ms);
                _botLastMs = ms;
            }

            if (ms >= 1000)
            {
                _log.LogInfo($"[server] bot generation took {ms:0} ms ({task.Result?.Length ?? 0} bots, asked {asked})");
            }
        }

        private static Exception SyncFinalizer(Exception __exception, long __state, object[] __args)
        {
            try
            {
                s_instance?.RecordSync(__state, __args != null && __args.Length > 0 ? __args[0] as string : null);
            }
            catch (Exception)
            {
                // never interfere with the request itself
            }

            return __exception;
        }

        private void RecordSync(long start, string path)
        {
            if (Thread.CurrentThread.ManagedThreadId != s_mainThreadId)
            {
                return; // off the main thread nothing freezes
            }

            double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            int frame = FrameCounter();
            if (frame != _frameWaitFrame)
            {
                _frameWaitFrame = frame;
                _frameWaitMs = 0;
                _frameWaitPath = null;
            }

            _frameWaitMs += ms;
            _frameWaitPath = _frameWaitPath ?? path;

            if (ms < 5)
            {
                return;
            }

            path = path ?? "?";
            _waitCount++;
            _waitPaths[path] = (_waitPaths.TryGetValue(path, out int n) ? n : 0) + 1;
            if (ms > _waitMaxMs)
            {
                _waitMaxMs = ms;
                _waitMaxPath = path;
            }
        }

        /// <summary>Main-thread server wait inside <paramref name="frame"/>, in ms (0 if none).</summary>
        public double WaitInFrame(int frame, out string path)
        {
            path = _frameWaitFrame == frame ? _frameWaitPath : null;
            return _frameWaitFrame == frame ? _frameWaitMs : 0;
        }

        // ---------------------------------------------------------------- per raid

        public void ResetRaid()
        {
            lock (_gate)
            {
                _botCalls = _botCount = _botFailures = 0;
                _botTotalMs = _botMaxMs = 0;
                _botLastMs = -1;
            }

            _waitCount = 0;
            _waitMaxMs = 0;
            _waitMaxPath = null;
            _waitPaths.Clear();
        }

        public int BotCalls
        {
            get
            {
                lock (_gate)
                {
                    return _botCalls;
                }
            }
        }

        public double BotAverageMs
        {
            get
            {
                lock (_gate)
                {
                    return _botCalls > 0 ? _botTotalMs / _botCalls : -1;
                }
            }
        }

        public double BotMaxMs
        {
            get
            {
                lock (_gate)
                {
                    return _botMaxMs;
                }
            }
        }

        public int WaitCount => _waitCount;

        public double WaitMaxMs => _waitMaxMs;

        /// <summary>One Korean line about bot generation this raid, or null when there were no requests.</summary>
        public string DescribeBots()
        {
            lock (_gate)
            {
                if (_botCalls == 0 && _botFailures == 0)
                {
                    return null;
                }

                double avg = _botCalls > 0 ? _botTotalMs / _botCalls : 0;
                return Loc.L($"봇 생성 응답: 평균 {avg:0}ms · 최대 {_botMaxMs:0}ms · 마지막 {Math.Max(0, _botLastMs):0}ms ({_botCalls}회, 봇 {_botCount}명",
                             $"bot generation: avg {avg:0} ms · max {_botMaxMs:0} ms · last {Math.Max(0, _botLastMs):0} ms ({_botCalls} requests, {_botCount} bots") +
                       (_botFailures > 0 ? Loc.L($", 실패 {_botFailures}", $", {_botFailures} failed") : string.Empty) + ")";
            }
        }

        /// <summary>One Korean line about main-thread waits this raid, or null when there were none.</summary>
        public string DescribeWaits()
        {
            if (_waitCount == 0)
            {
                return null;
            }

            string top = string.Join(", ", _waitPaths.OrderByDescending(kv => kv.Value).Take(3).Select(kv => kv.Key + " " + kv.Value + Loc.L("회", "x")));
            return Loc.L($"서버 응답 대기로 멈춤: {_waitCount}회, 최대 {_waitMaxMs:0}ms ({_waitMaxPath}) · {top}",
                         $"froze waiting on the server: {_waitCount}x, worst {_waitMaxMs:0} ms ({_waitMaxPath}) · {top}");
        }

        public string DescribeForLog()
        {
            string memory = PrivateBytes > 0 ? $"server {MemoryStats.Gb(PrivateBytes)} GB" : "server ?";
            lock (_gate)
            {
                return memory + (_botCalls > 0 ? $", bot gen avg {_botTotalMs / _botCalls:0} ms max {_botMaxMs:0} ms ({_botCalls}x)" : string.Empty) +
                       (_waitCount > 0 ? $", main-thread server waits {_waitCount}x max {_waitMaxMs:0} ms ({_waitMaxPath})" : string.Empty);
            }
        }

        // ---------------------------------------------------------------- Win32

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

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inherit, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCountersEx counters, uint size);
    }
}

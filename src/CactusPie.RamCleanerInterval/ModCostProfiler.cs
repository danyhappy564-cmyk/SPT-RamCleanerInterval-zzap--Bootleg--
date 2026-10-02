using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace CactusPie.RamCleanerInterval
{
    /// <summary>
    /// Measures how much main-thread time each mod costs per frame, so the overlay can say "SAIN 3.1 ms".
    ///
    /// What is timed (the places mod code actually runs every frame):
    ///  - every Harmony prefix/postfix/finalizer other mods patched into the game,
    ///  - Update / LateUpdate / FixedUpdate / OnGUI of MonoBehaviours in mod assemblies,
    ///  - BigBrain layer/logic overrides in other mods (so SAIN's bot brain counts as SAIN, not BigBrain).
    /// Times are "self" times: when mod A's code triggers mod B's patch, B's time is taken out of A's.
    ///
    /// The wrappers are installed once, in the main menu (patching ~1-3k methods takes a moment), and stay
    /// cheap while not measuring: one static bool check per call. Measuring runs in short windows.
    /// </summary>
    internal sealed class ModCostProfiler
    {
        private const string HarmonyId = "com.cactuspie.ramcleanerinterval.profiler";
        private const int MaxDepth = 256;

        private static readonly Dictionary<MethodBase, int> s_methodMod = new Dictionary<MethodBase, int>();
        private static readonly long[] s_selfTicks = new long[ModRegistry.MaxMods];
        private static readonly long[] s_startStack = new long[MaxDepth];
        private static readonly long[] s_childStack = new long[MaxDepth];
        private static readonly long[] s_selfAlloc = new long[ModRegistry.MaxMods];
        private static readonly long[] s_allocStartStack = new long[MaxDepth];
        private static readonly long[] s_allocChildStack = new long[MaxDepth];
        private static Func<long> s_allocReader = () => 0;

        // Per-frame self time, for "which mod caused this long frame". Swapped at the end of every frame.
        private static readonly long[] s_frameTicks = new long[ModRegistry.MaxMods];
        private static readonly long[] s_lastFrameTicks = new long[ModRegistry.MaxMods];
        private static int s_depth;
        private static int s_mainThreadId;
        private static volatile bool s_measuring;

        private readonly ManualLogSource _log;
        private Queue<KeyValuePair<MethodBase, int>> _toPatch;
        private Harmony _harmony;
        private int _patched;
        private int _failed;
        private int _windowFrames;
        private float _windowStart;
        private float[] _firstWindowMs;
        private float[] _firstWindowAlloc;

        public ModCostProfiler(ManualLogSource log)
        {
            _log = log;
            s_mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        public enum State
        {
            NotInstalled,
            Installing,
            Ready,
        }

        public State Status { get; private set; } = State.NotInstalled;

        public bool Measuring => s_measuring;

        public float WindowElapsed => s_measuring ? Time.realtimeSinceStartup - _windowStart : 0f;

        /// <summary>Last finished window: per-mod ms per frame, highest first.</summary>
        public List<KeyValuePair<string, float>> LastResult { get; private set; } = new List<KeyValuePair<string, float>>();

        public float LastFrameMs { get; private set; }

        public float LastResultTime { get; private set; } = -1f;

        public string Suspect { get; private set; }

        public string LastSummary { get; private set; } = "아직 측정 안 함";

        /// <summary>Last window: managed memory each mod's code allocated, MB per minute, highest first.</summary>
        public List<KeyValuePair<string, float>> LastAlloc { get; private set; } = new List<KeyValuePair<string, float>>();

        public float LastAllocTotal { get; private set; }

        public string AllocSuspect { get; private set; }

        /// <summary>Which counter the allocation numbers come from (exact per-thread counter, or heap-size deltas).</summary>
        public string AllocMethod { get; private set; } = "?";

        // ------------------------------------------------------------------ install

        /// <summary>Collects what to wrap. Call in the main menu; then call <see cref="InstallStep"/> every frame.</summary>
        public void BeginInstall()
        {
            if (Status != State.NotInstalled)
            {
                return;
            }

            Status = State.Installing;
            ModRegistry.Build();
            var targets = new Dictionary<MethodBase, int>();

            try
            {
                CollectHarmonyPatches(targets);
                CollectUnityMessages(targets);
                CollectBigBrainOverrides(targets);
            }
            catch (Exception ex)
            {
                _log.LogError($"Profiler: collecting methods failed: {ex}");
            }

            DetectAllocReader();
            _toPatch = new Queue<KeyValuePair<MethodBase, int>>(targets);
            _harmony = new Harmony(HarmonyId);
            _log.LogInfo($"Profiler: wrapping {targets.Count} mod methods across {ModRegistry.Count} mods (in the menu, spread over frames)");
        }

        /// <summary>Patches a batch. Returns true when everything is installed.</summary>
        public bool InstallStep(int batch)
        {
            if (Status != State.Installing)
            {
                return Status == State.Ready;
            }

            var prefix = new HarmonyMethod(typeof(ModCostProfiler).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic));
            var finalizer = new HarmonyMethod(typeof(ModCostProfiler).GetMethod(nameof(Finalizer), BindingFlags.Static | BindingFlags.NonPublic));

            for (int i = 0; i < batch && _toPatch.Count > 0; i++)
            {
                KeyValuePair<MethodBase, int> target = _toPatch.Dequeue();
                try
                {
                    _harmony.Patch(target.Key, prefix: prefix, finalizer: finalizer);
                    s_methodMod[target.Key] = target.Value;
                    _patched++;
                }
                catch (Exception)
                {
                    _failed++;
                }
            }

            if (_toPatch.Count > 0)
            {
                return false;
            }

            Status = State.Ready;
            _log.LogInfo($"Profiler: ready, {_patched} methods wrapped ({_failed} skipped)");
            return true;
        }

        /// <summary>
        /// Picks how to read "bytes allocated so far". GC.GetAllocatedBytesForCurrentThread is exact but may be
        /// missing or return 0 on Unity's Boehm GC; then fall back to the used-heap size, which only moves in
        /// heap-block steps but, summed over thousands of calls, still attributes allocations to the right mods
        /// (the GC is off during raids, so the heap only grows while mods run).
        /// </summary>
        private void DetectAllocReader()
        {
            try
            {
                if (TestPerThreadCounter())
                {
                    s_allocReader = ReadPerThread;
                    AllocMethod = "per-thread counter";
                    _log.LogInfo("Profiler: allocations measured with GC.GetAllocatedBytesForCurrentThread");
                    return;
                }
            }
            catch (Exception)
            {
                // missing in this runtime
            }

            s_allocReader = () => GC.GetTotalMemory(false);
            AllocMethod = "heap size";
            _log.LogInfo("Profiler: allocations measured from heap-size deltas (approximate, block-sized steps)");
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static bool TestPerThreadCounter()
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            byte[] probe = new byte[64 * 1024];
            long after = GC.GetAllocatedBytesForCurrentThread();
            GC.KeepAlive(probe);
            return after - before >= 60 * 1024;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static long ReadPerThread()
        {
            return GC.GetAllocatedBytesForCurrentThread();
        }

        private static void CollectHarmonyPatches(Dictionary<MethodBase, int> targets)
        {
            foreach (MethodBase original in Harmony.GetAllPatchedMethods().ToList())
            {
                Patches info = Harmony.GetPatchInfo(original);
                if (info == null)
                {
                    continue;
                }

                foreach (Patch patch in info.Prefixes.Concat(info.Postfixes).Concat(info.Finalizers))
                {
                    if (patch.owner == HarmonyId)
                    {
                        continue;
                    }

                    AddTarget(targets, patch.PatchMethod);
                }
            }
        }

        private static readonly string[] s_unityMessages = { "Update", "LateUpdate", "FixedUpdate", "OnGUI" };

        private static void CollectUnityMessages(Dictionary<MethodBase, int> targets)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (ModRegistry.IndexOf(assembly) < 0)
                {
                    continue;
                }

                foreach (Type type in SafeTypes(assembly))
                {
                    if (type.IsAbstract || type.ContainsGenericParameters || !typeof(MonoBehaviour).IsAssignableFrom(type))
                    {
                        continue;
                    }

                    foreach (string name in s_unityMessages)
                    {
                        MethodInfo method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                            null, Type.EmptyTypes, null);
                        AddTarget(targets, method);
                    }
                }
            }
        }

        private static void CollectBigBrainOverrides(Dictionary<MethodBase, int> targets)
        {
            var bases = new List<Type>();
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (string name in new[] { "DrakiaXYZ.BigBrain.Brains.CustomLayer", "DrakiaXYZ.BigBrain.Brains.CustomLogic" })
                {
                    Type type = assembly.GetType(name, false);
                    if (type != null)
                    {
                        bases.Add(type);
                    }
                }
            }

            if (bases.Count == 0)
            {
                return;
            }

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (ModRegistry.IndexOf(assembly) < 0 || bases.Any(b => b.Assembly == assembly))
                {
                    continue;
                }

                foreach (Type type in SafeTypes(assembly))
                {
                    if (type.IsAbstract || type.ContainsGenericParameters || !bases.Any(b => b.IsAssignableFrom(type)))
                    {
                        continue;
                    }

                    foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (method.IsVirtual && method.GetBaseDefinition().DeclaringType != type)
                        {
                            AddTarget(targets, method);
                        }
                    }
                }
            }
        }

        private static void AddTarget(Dictionary<MethodBase, int> targets, MethodInfo method)
        {
            if (method == null || method.IsAbstract || method.ContainsGenericParameters || method.GetMethodBody() == null)
            {
                return;
            }

            int mod = ModRegistry.IndexOf(method.DeclaringType?.Assembly);
            if (mod >= 0 && !targets.ContainsKey(method))
            {
                targets[method] = mod;
            }
        }

        private static IEnumerable<Type> SafeTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(t => t != null);
            }
            catch (Exception)
            {
                return Enumerable.Empty<Type>();
            }
        }

        // ------------------------------------------------------------------ timing wrappers

        private static void Prefix(out long __state)
        {
            if (!s_measuring || s_depth >= MaxDepth || Thread.CurrentThread.ManagedThreadId != s_mainThreadId)
            {
                __state = 0;
                return;
            }

            s_allocStartStack[s_depth] = s_allocReader();
            s_allocChildStack[s_depth] = 0;
            long now = Stopwatch.GetTimestamp();
            s_startStack[s_depth] = now;
            s_childStack[s_depth] = 0;
            s_depth++;
            __state = now;
        }

        private static void Finalizer(long __state, MethodBase __originalMethod)
        {
            if (__state == 0 || s_depth == 0)
            {
                return;
            }

            long now = Stopwatch.GetTimestamp();
            s_depth--;
            long elapsed = now - s_startStack[s_depth];
            long self = elapsed - s_childStack[s_depth];
            long allocated = Math.Max(0, s_allocReader() - s_allocStartStack[s_depth]);
            long selfAllocated = allocated - s_allocChildStack[s_depth];
            if (s_depth > 0)
            {
                s_childStack[s_depth - 1] += elapsed;
                s_allocChildStack[s_depth - 1] += allocated;
            }

            if (s_methodMod.TryGetValue(__originalMethod, out int mod))
            {
                s_selfTicks[mod] += Math.Max(0, self);
                s_frameTicks[mod] += Math.Max(0, self);
                s_selfAlloc[mod] += Math.Max(0, selfAllocated);
            }
        }

        // ------------------------------------------------------------------ per-frame attribution

        /// <summary>Call at the very end of every frame (WaitForEndOfFrame): freezes this frame's per-mod times.</summary>
        public void EndFrame()
        {
            int count = ModRegistry.Count;
            if (count == 0)
            {
                return;
            }

            Array.Copy(s_frameTicks, s_lastFrameTicks, count);
            Array.Clear(s_frameTicks, 0, count);
        }

        /// <summary>The mod that spent the most main-thread time in the last finished frame, and all mods' total (ms).</summary>
        public bool LastFrameTop(out string mod, out float modMs, out float totalMs)
        {
            mod = null;
            modMs = 0f;
            totalMs = 0f;
            if (!s_measuring)
            {
                return false;
            }

            double toMs = 1000.0 / Stopwatch.Frequency;
            long best = 0;
            int bestIndex = -1;
            long total = 0;
            for (int i = 0; i < ModRegistry.Count; i++)
            {
                long ticks = s_lastFrameTicks[i];
                total += ticks;
                if (ticks > best)
                {
                    best = ticks;
                    bestIndex = i;
                }
            }

            totalMs = (float)(total * toMs);
            if (bestIndex >= 0)
            {
                mod = ModRegistry.Name(bestIndex);
                modMs = (float)(best * toMs);
            }

            return true;
        }

        // ------------------------------------------------------------------ measuring windows

        // The "got slower / allocates more than early in the raid" comparison needs a baseline taken once bots are
        // actually doing things: the very first window (right at the countdown) read SAIN 0 MB/min, which made every
        // later window look like "growing" (2026-10-02 log).
        private const float BaselineAfterSeconds = 180f;
        private const float MinBaselineMs = 0.5f;
        private const float MinBaselineAllocMb = 5f;
        private float _raidResetTime;

        public void ResetRaid()
        {
            _raidResetTime = Time.realtimeSinceStartup;
            _firstWindowMs = null;
            _firstWindowAlloc = null;
            Suspect = null;
            AllocSuspect = null;
        }

        public void StartWindow()
        {
            if (Status != State.Ready || s_measuring)
            {
                return;
            }

            Array.Clear(s_selfTicks, 0, s_selfTicks.Length);
            Array.Clear(s_selfAlloc, 0, s_selfAlloc.Length);
            s_depth = 0;
            _windowFrames = 0;
            _windowStart = Time.realtimeSinceStartup;
            s_measuring = true;
        }

        /// <summary>Call once per frame while measuring.</summary>
        public void CountFrame()
        {
            if (s_measuring)
            {
                _windowFrames++;
            }
        }

        public void StopWindow(float suspectMs, float suspectShare, float allocSuspectMbPerMin = 50f)
        {
            if (!s_measuring)
            {
                return;
            }

            s_measuring = false;
            float seconds = Time.realtimeSinceStartup - _windowStart;
            if (_windowFrames < 10 || seconds <= 0f)
            {
                return;
            }

            LastFrameMs = seconds * 1000f / _windowFrames;
            double toMs = 1000.0 / Stopwatch.Frequency;
            var perMod = new float[ModRegistry.Count];
            var result = new List<KeyValuePair<string, float>>();
            for (int i = 0; i < ModRegistry.Count; i++)
            {
                perMod[i] = (float)(s_selfTicks[i] * toMs / _windowFrames);
                if (perMod[i] >= 0.01f)
                {
                    result.Add(new KeyValuePair<string, float>(ModRegistry.Name(i), perMod[i]));
                }
            }

            result.Sort((a, b) => b.Value.CompareTo(a.Value));
            LastResult = result;
            LastResultTime = Time.realtimeSinceStartup;

            // Suspect = the top mod when it is both big in absolute terms and a real share of the frame,
            // or a mod that got much slower since the first window of this raid (something piling up).
            Suspect = null;
            if (result.Count > 0 && result[0].Value >= suspectMs && result[0].Value >= LastFrameMs * suspectShare)
            {
                Suspect = $"{result[0].Key} — 프레임의 {result[0].Value / LastFrameMs * 100f:0}% ({result[0].Value:0.0}ms)";
            }

            bool baselineReady = Time.realtimeSinceStartup - _raidResetTime >= BaselineAfterSeconds;
            if (_firstWindowMs == null)
            {
                if (baselineReady)
                {
                    _firstWindowMs = perMod;
                }
            }
            else
            {
                for (int i = 0; i < Math.Min(perMod.Length, _firstWindowMs.Length); i++)
                {
                    float before = Math.Max(_firstWindowMs[i], MinBaselineMs);
                    if (perMod[i] >= 1f && perMod[i] >= before * 2f && perMod[i] - before >= 1f)
                    {
                        string grow = $"{ModRegistry.Name(i)} — 레이드 3분 시점 {before:0.0}ms → 지금 {perMod[i]:0.0}ms (점점 느려짐)";
                        Suspect = Suspect == null ? grow : Suspect + " / " + grow;
                        break;
                    }
                }
            }

            // Managed memory each mod allocated during the window, as MB per minute of play.
            double toMbPerMin = 60.0 / seconds / (1024.0 * 1024.0);
            var perModAlloc = new float[ModRegistry.Count];
            var alloc = new List<KeyValuePair<string, float>>();
            float allocTotal = 0f;
            for (int i = 0; i < ModRegistry.Count; i++)
            {
                perModAlloc[i] = (float)(s_selfAlloc[i] * toMbPerMin);
                allocTotal += perModAlloc[i];
                if (perModAlloc[i] >= 0.1f)
                {
                    alloc.Add(new KeyValuePair<string, float>(ModRegistry.Name(i), perModAlloc[i]));
                }
            }

            alloc.Sort((a, b) => b.Value.CompareTo(a.Value));
            LastAlloc = alloc;
            LastAllocTotal = allocTotal;
            AllocSuspect = null;
            if (alloc.Count > 0 && alloc[0].Value >= allocSuspectMbPerMin && alloc[0].Value >= allocTotal * 0.4f)
            {
                AllocSuspect = $"{alloc[0].Key} — 모드 코드가 만드는 메모리의 {alloc[0].Value / Math.Max(0.01f, allocTotal) * 100f:0}% ({alloc[0].Value:0}MB/분)";
            }

            if (_firstWindowAlloc == null)
            {
                if (baselineReady)
                {
                    _firstWindowAlloc = perModAlloc;
                }
            }
            else
            {
                for (int i = 0; i < Math.Min(perModAlloc.Length, _firstWindowAlloc.Length); i++)
                {
                    float before = Math.Max(_firstWindowAlloc[i], MinBaselineAllocMb);
                    if (perModAlloc[i] >= allocSuspectMbPerMin / 2f && perModAlloc[i] >= before * 2f && perModAlloc[i] - before >= allocSuspectMbPerMin / 2f)
                    {
                        string grow = $"{ModRegistry.Name(i)} — 메모리 생성 레이드 3분 시점 {before:0}MB/분 → 지금 {perModAlloc[i]:0}MB/분 (점점 늘어남)";
                        AllocSuspect = AllocSuspect == null ? grow : AllocSuspect + " / " + grow;
                        break;
                    }
                }
            }

            string top = string.Join(", ", result.Take(8).Select(kv => $"{kv.Key} {kv.Value:0.00}ms"));
            string topAlloc = string.Join(", ", alloc.Take(8).Select(kv => $"{kv.Key} {kv.Value:0.0}"));
            _log.LogInfo($"[mods] {seconds:0}s window, {_windowFrames} frames, frame {LastFrameMs:0.0}ms ({1000f / LastFrameMs:0} fps) | " +
                         $"main-thread self time per frame: {(top.Length > 0 ? top : "(none measurable)")}" +
                         (Suspect != null ? $" | SUSPECT: {Suspect}" : string.Empty) +
                         $" | managed allocations MB/min ({AllocMethod}, total {allocTotal:0}): {(topAlloc.Length > 0 ? topAlloc : "(none)")}" +
                         (AllocSuspect != null ? $" | MEMORY SUSPECT: {AllocSuspect}" : string.Empty));
            LastSummary = $"{DateTime.Now:HH:mm:ss} 측정 — " + (Suspect != null ? "의심: " + Suspect : "뚜렷한 의심 모드 없음") +
                          (result.Count > 0 ? $" (1위 {result[0].Key} {result[0].Value:0.0}ms / 프레임 {LastFrameMs:0.0}ms)" : string.Empty);
        }
    }
}

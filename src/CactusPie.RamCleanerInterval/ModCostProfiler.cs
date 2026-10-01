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
            if (s_depth > 0)
            {
                s_childStack[s_depth - 1] += elapsed;
            }

            if (s_methodMod.TryGetValue(__originalMethod, out int mod))
            {
                s_selfTicks[mod] += Math.Max(0, self);
            }
        }

        // ------------------------------------------------------------------ measuring windows

        public void ResetRaid()
        {
            _firstWindowMs = null;
            Suspect = null;
        }

        public void StartWindow()
        {
            if (Status != State.Ready || s_measuring)
            {
                return;
            }

            Array.Clear(s_selfTicks, 0, s_selfTicks.Length);
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

        public void StopWindow(float suspectMs, float suspectShare)
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

            if (_firstWindowMs == null)
            {
                _firstWindowMs = perMod;
            }
            else
            {
                for (int i = 0; i < Math.Min(perMod.Length, _firstWindowMs.Length); i++)
                {
                    float before = _firstWindowMs[i];
                    if (perMod[i] >= 1f && perMod[i] >= before * 2f && perMod[i] - before >= 1f)
                    {
                        string grow = $"{ModRegistry.Name(i)} — 레이드 초반 {before:0.0}ms → 지금 {perMod[i]:0.0}ms (점점 느려짐)";
                        Suspect = Suspect == null ? grow : Suspect + " / " + grow;
                        break;
                    }
                }
            }

            string top = string.Join(", ", result.Take(8).Select(kv => $"{kv.Key} {kv.Value:0.00}ms"));
            _log.LogInfo($"[mods] {seconds:0}s window, {_windowFrames} frames, frame {LastFrameMs:0.0}ms ({1000f / LastFrameMs:0} fps) | " +
                         $"main-thread self time per frame: {(top.Length > 0 ? top : "(none measurable)")}" +
                         (Suspect != null ? $" | SUSPECT: {Suspect}" : string.Empty));
            LastSummary = $"{DateTime.Now:HH:mm:ss} 측정 — " + (Suspect != null ? "의심: " + Suspect : "뚜렷한 의심 모드 없음") +
                          (result.Count > 0 ? $" (1위 {result[0].Key} {result[0].Value:0.0}ms / 프레임 {LastFrameMs:0.0}ms)" : string.Empty);
        }
    }
}

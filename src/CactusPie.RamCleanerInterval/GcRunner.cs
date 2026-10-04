using System;
using System.Diagnostics;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Scripting;

namespace CactusPie.RamCleanerInterval
{
    /// <summary>
    /// Runs a managed garbage collection spread over many frames.
    ///
    /// Why this exists: when the PC has 12 GB+ of RAM, EFT turns the Unity GC completely off for the
    /// whole raid (BaseLocalGame.PrepareSession -> InGameMemoryManagement.GCEnabled = false). Every
    /// allocation made during the raid (bots, SAIN, other mods) then just piles up until the raid ends.
    /// In Disabled mode even GC.Collect() is ignored, so the collection has to switch the GC to Manual,
    /// do the work in small slices with GarbageCollector.CollectIncremental, and put the mode back.
    /// </summary>
    internal sealed class GcRunner
    {
        private const float MaxRunSeconds = 300f;

        private readonly ManualLogSource _log;
        private readonly Stopwatch _slice = new Stopwatch();

        private GarbageCollector.Mode _previousMode;
        private GarbageCollector.Mode _modeWeSet;
        private bool _changedMode;
        private long _usedBefore;
        private float _startTime;
        private int _frames;
        private double _workMs;
        private double _maxSliceMs;
        private int _maxSliceFrame;
        private int _longSlices;
        private string _reason;

        public GcRunner(ManualLogSource log)
        {
            _log = log;
        }

        public bool Running { get; private set; }

        /// <summary>True when the running collection was started by the growth trigger, not a button.</summary>
        public bool IsAuto { get; private set; }

        public float Elapsed => Running ? Time.realtimeSinceStartup - _startTime : 0f;

        /// <summary>Human readable result of the last finished collection, for the F12 status box.</summary>
        private string _lastResult;

        public string LastResult { get => _lastResult ?? Loc.L("아직 없음", "none yet"); private set => _lastResult = value; }

        /// <summary>Mono "used" bytes right after the last collection finished (-1 = none yet).</summary>
        public long LastUsedAfter { get; private set; } = -1;

        public float LastFinishTime { get; private set; } = -1f;

        /// <summary>Mono "used" bytes when the last finished collection started.</summary>
        public long LastUsedBefore { get; private set; } = -1;

        /// <summary>Longest single frame of the last finished collection (ms). For a blocking GC: the whole freeze.</summary>
        public double LastMaxSliceMs { get; private set; }

        /// <summary>Time spent inside CollectIncremental during the most recent Tick (ms), and the frame it ran on.
        /// Lets the hitch detector tell "this long frame was our GC" from "something else".</summary>
        public double LastTickMs { get; private set; }

        public int LastTickFrame { get; private set; } = -1;

        /// <summary>Raised on the main thread when a collection ends (normally or aborted).</summary>
        public event Action Finished;

        /// <summary>
        /// Starts a collection. Returns false when nothing was started (already running, or the game
        /// was built without incremental GC and a blocking full collection is not allowed).
        /// </summary>
        public bool Start(string reason, bool allowBlockingFallback, bool isAuto)
        {
            if (Running)
            {
                return false;
            }

            IsAuto = isAuto;

            if (!GarbageCollector.isIncremental)
            {
                if (!allowBlockingFallback)
                {
                    return false;
                }

                RunBlocking(reason);
                return true;
            }

            _reason = reason;
            _usedBefore = MemoryStats.MonoUsed();
            _startTime = Time.realtimeSinceStartup;
            _frames = 0;
            _workMs = 0;
            _maxSliceMs = 0;
            _maxSliceFrame = 0;
            _longSlices = 0;
            _changedMode = false;
            _previousMode = GarbageCollector.GCMode;

            // Only touch the mode when the game has the GC switched off. If it is already Enabled
            // (menus) or Manual, CollectIncremental works as-is.
            if (_previousMode == GarbageCollector.Mode.Disabled)
            {
                if (SetMode(GarbageCollector.Mode.Manual))
                {
                    _modeWeSet = GarbageCollector.Mode.Manual;
                }
                else if (SetMode(GarbageCollector.Mode.Enabled))
                {
                    _modeWeSet = GarbageCollector.Mode.Enabled;
                }
                else
                {
                    _log.LogWarning("GC is disabled by the game and could not be switched on; skipping collection");
                    return false;
                }

                _changedMode = true;
            }

            Running = true;
            _log.LogInfo($"GC start ({_reason}): used {MemoryStats.Gb(_usedBefore)} GB, mode {_previousMode}" +
                         (_changedMode ? $" -> {_modeWeSet}" : string.Empty));
            return true;
        }

        /// <summary>Does one slice of work. Call once per frame while <see cref="Running"/>.</summary>
        public void Tick(float sliceMs, bool inGame)
        {
            if (!Running)
            {
                return;
            }

            bool moreWork;
            _slice.Restart();
            try
            {
                moreWork = GarbageCollector.CollectIncremental((ulong)(Mathf.Max(0.1f, sliceMs) * 1000000f));
            }
            catch (Exception ex)
            {
                _log.LogError($"CollectIncremental failed: {ex.Message}");
                Finish(inGame, Loc.L("오류로 중단", "stopped by an error"));
                return;
            }

            _slice.Stop();
            double ms = _slice.Elapsed.TotalMilliseconds;
            LastTickMs = ms;
            LastTickFrame = Time.frameCount;
            _frames++;
            _workMs += ms;
            if (ms > _maxSliceMs)
            {
                _maxSliceMs = ms;
                _maxSliceFrame = _frames;
            }

            if (ms > 16.7)
            {
                _longSlices++;
            }

            if (!moreWork)
            {
                Finish(inGame, null);
            }
            else if (Elapsed > MaxRunSeconds)
            {
                Finish(inGame, Loc.L($"{MaxRunSeconds:0}초 넘어서 중단", $"stopped after {MaxRunSeconds:0} s"));
            }
        }

        /// <summary>Stops a running collection (raid ended, feature switched off).</summary>
        public void Abort(bool inGame, string why)
        {
            if (Running)
            {
                Finish(inGame, why);
            }
        }

        private void Finish(bool inGame, string abortReason)
        {
            Running = false;
            RestoreMode(inGame);

            long usedAfter = MemoryStats.MonoUsed();
            float seconds = Time.realtimeSinceStartup - _startTime;
            LastUsedAfter = usedAfter;
            LastUsedBefore = _usedBefore;
            LastMaxSliceMs = _maxSliceMs;
            LastFinishTime = Time.realtimeSinceStartup;

            string status = abortReason == null ? Loc.L("완료", "done") : abortReason;
            LastResult = $"{DateTime.Now:HH:mm:ss} {status} — {MemoryStats.Gb(_usedBefore)} → {MemoryStats.Gb(usedAfter)} GB, " +
                         Loc.L($"{seconds:0.0}초 동안 {_frames}프레임에 나눠 처리 (가장 긴 프레임 {_maxSliceMs:0}ms, 16ms 넘은 프레임 {_longSlices}개)",
                               $"spread over {_frames} frames in {seconds:0.0} s (longest frame {_maxSliceMs:0} ms, {_longSlices} frames over 16 ms)");

            _log.LogInfo($"GC {(abortReason == null ? "done" : "aborted")} ({_reason}): {MemoryStats.Gb(_usedBefore)} -> " +
                         $"{MemoryStats.Gb(usedAfter)} GB in {seconds:0.0}s, {_frames} frames, work {_workMs:0}ms, " +
                         $"max slice {_maxSliceMs:0.00}ms at frame {_maxSliceFrame}/{_frames}, slices over 16.7ms: {_longSlices}, " +
                         $"mode now {GarbageCollector.GCMode}");

            Finished?.Invoke();
        }

        private void RestoreMode(bool inGame)
        {
            if (!_changedMode)
            {
                return;
            }

            _changedMode = false;

            // If the game changed the mode behind our back (raid ended -> menu turns the GC on), leave it.
            if (GarbageCollector.GCMode != _modeWeSet)
            {
                return;
            }

            // Still in the raid: give the game back its "GC off". Raid already over: menus run with GC on.
            SetMode(inGame ? _previousMode : GarbageCollector.Mode.Enabled);
        }

        private void RunBlocking(string reason)
        {
            // Game built without incremental GC: the only option is one full, blocking collection.
            // On a multi-GB heap this can freeze the game for a second or more, hence opt-in only.
            long before = MemoryStats.MonoUsed();
            GarbageCollector.Mode previous = GarbageCollector.GCMode;
            var watch = Stopwatch.StartNew();

            if (previous == GarbageCollector.Mode.Disabled)
            {
                SetMode(GarbageCollector.Mode.Enabled);
            }

            try
            {
                GC.Collect();
            }
            finally
            {
                if (previous == GarbageCollector.Mode.Disabled && GarbageCollector.GCMode == GarbageCollector.Mode.Enabled)
                {
                    SetMode(previous);
                }
            }

            watch.Stop();
            long after = MemoryStats.MonoUsed();
            LastUsedAfter = after;
            LastUsedBefore = before;
            LastMaxSliceMs = watch.Elapsed.TotalMilliseconds;
            LastTickMs = LastMaxSliceMs;
            LastTickFrame = Time.frameCount;
            LastFinishTime = Time.realtimeSinceStartup;
            LastResult = Loc.L($"{DateTime.Now:HH:mm:ss} 전체 GC(한 번에) — {MemoryStats.Gb(before)} → {MemoryStats.Gb(after)} GB, 게임 멈춤 {watch.Elapsed.TotalMilliseconds:0}ms",
                               $"{DateTime.Now:HH:mm:ss} full GC (in one go) — {MemoryStats.Gb(before)} → {MemoryStats.Gb(after)} GB, game frozen {watch.Elapsed.TotalMilliseconds:0} ms");
            _log.LogInfo($"GC blocking ({reason}): {MemoryStats.Gb(before)} -> {MemoryStats.Gb(after)} GB, " +
                         $"froze {watch.Elapsed.TotalMilliseconds:0}ms");
            Finished?.Invoke();
        }

        /// <summary>True while this class itself is switching the GC mode (lets the plugin tell our
        /// switches apart from the game's in the GCModeChanged log).</summary>
        public static bool ChangingMode { get; private set; }

        private bool SetMode(GarbageCollector.Mode mode)
        {
            ChangingMode = true;
            try
            {
                GarbageCollector.GCMode = mode;
                return GarbageCollector.GCMode == mode;
            }
            catch (Exception ex)
            {
                _log.LogWarning($"Could not set GC mode {mode}: {ex.Message}");
                return false;
            }
            finally
            {
                ChangingMode = false;
            }
        }
    }
}

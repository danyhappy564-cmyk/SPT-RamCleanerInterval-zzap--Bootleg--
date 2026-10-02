using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using UnityEngine;
using UnityEngine.Scripting;

namespace CactusPie.RamCleanerInterval
{
    [BepInPlugin("com.cactuspie.ramcleanerinterval", "RAM 클리너 (RamCleanerInterval)", "2.8.0")]
    public partial class CustomRamCleanerIntervalPlugin : BaseUnityPlugin
    {
        private const float TrimMaxDeferSeconds = 60f;
        private const float RaidStartFallbackSeconds = 120f;
        private const float UnloadMeasureDelaySeconds = 6f;
        private const double WeakUnloadGb = 0.5;
        private const float LeakFirstSnapshotDelay = 60f;
        private const float LeakMaxDeferSeconds = 60f;

        // Survives raids (not the game): once auto unload proved useless, don't hitch every raid to re-learn it.
        private static bool s_autoUnloadUselessThisSession;

        private enum AssetPhase
        {
            Idle,
            WaitingForGc,
            Unloading,
            Measuring,
        }

        private readonly ConcurrentQueue<Action> _mainThread = new ConcurrentQueue<Action>();

        private GcRunner _gc;
        private CombatTracker _combat;
        private VramMonitor _vram;
        private LeakTracker _leak;
        private HitchMonitor _hitch;
        private RaidReport _report;
        private MemoryWarnings _warnings;
        private Func<string> _hitchContext;
        private int _leakFrame = -1;
        private double _leakMs;
        private int _assetActiveFrame = -1;
        private string _lastReport = "아직 없음";

        private ModCostProfiler _profiler;
        private ModObjectCounter _objects;
        private FrameStats _frames;
        private PerformanceHistory _history;
        private float _profilerNext = -1f;
        private float _objectsNext = -1f;
        private float _objectsPendingSince = -1f;
        private float _lastSpawnTime = float.NegativeInfinity;
        private string _map;

        // Memory suspects: managed memory still alive after each of our GCs (a rising floor = a real managed leak).
        private readonly List<KeyValuePair<float, long>> _gcFloors = new List<KeyValuePair<float, long>>();
        private readonly List<string> _memSuspects = new List<string>();
        private bool _hitchSuspectNotified;
        private readonly OverlayPanel _panel = new OverlayPanel();
        private SessionLog _sessionLog;
        private float _diagStartedAt = -1f;
        private readonly WaitForEndOfFrame _endOfFrame = new WaitForEndOfFrame();
        private string _lastMemSuspectKey = string.Empty;
        private readonly HashSet<string> _memNotifiedThisRaid = new HashSet<string>();
        private float _leakNext = -1f;
        private float _leakPendingSince = -1f;

        private MemorySnapshot _snapshot;
        private float _nextSample;
        private float _nextLog;
        private bool _inGame;
        private bool _evaluateNow;

        // GC
        private long _gcBaseline = -1;
        private float _gcPendingSince = -1f;
        private bool _warnedNoIncremental;

        // Working set trim (runs on a thread-pool thread)
        private int _trimRunning;
        private float _lastTrim;
        private float _trimPendingSince = -1f;
        private string _trimPendingReason;

        // Asset unload
        private AssetPhase _assetPhase = AssetPhase.Idle;
        private string _assetReason;
        private bool _assetAuto;
        private AsyncOperation _assetOperation;
        private MemorySnapshot _assetBefore;
        private long _assetVramBefore;
        private float _assetStart;
        private float _assetDone;
        private float _assetLongestFrame;
        private float _raidStartedAt = -1f;
        private float _inGameSince;

        // Memory per dead bot: the number that actually tells whether a raid's growth is normal
        // (2026-10-01: 280-340 MB/death with every mod item allowed, ~60-75 MB once APBS limits mod items per raid).
        private readonly List<KeyValuePair<int, long>> _deathSamples =
            new List<KeyValuePair<int, long>>();
        private int _alive = -1;
        private int _dead = -1;
        private int _deadAtStart;
        private long _nativeAtStart = -1;
        private bool _startUnloadDone;
        private long _nativeBaseline = -1;
        private float _lastUnload = float.NegativeInfinity;
        private float _unloadPendingSince = -1f;
        private int _weakUnloads;
        private bool _autoUnloadStopped;

        private string _lastTrimResult = "아직 없음";
        private string _lastAssetResult = "아직 없음";
        private string _statusText = "측정 중...";

        internal void Awake()
        {
            _gc = new GcRunner(Logger);
            _gc.Finished += OnGcFinished;
            _combat = new CombatTracker();
            _combat.InventoryOpened += () => _evaluateNow = true;
            _vram = new VramMonitor();
            _leak = new LeakTracker(Logger);
            _hitch = new HitchMonitor(Logger);
            _report = new RaidReport();
            _warnings = new MemoryWarnings();
            _profiler = new ModCostProfiler(Logger);
            _objects = new ModObjectCounter(Logger);
            _frames = new FrameStats();
            _history = new PerformanceHistory(Logger);
            _hitchContext = () => $"players alive {_alive}, dead {_dead}, GC {GarbageCollector.GCMode}" +
                                  (Time.realtimeSinceStartup - _lastSpawnTime < 1.5f ? ", a bot spawned within the last second" : string.Empty) +
                                  (_profiler.Measuring ? ", mod profiler measuring" : string.Empty);

            BindSettings();
            StartCoroutine(EndOfFrameLoop());

            _sessionLog = new SessionLog(Logger);
            BepInEx.Logging.Logger.Listeners.Add(_sessionLog);
            _diagMode.SettingChanged += (_, __) => OnDiagChanged();
            if (_diagMode.Value)
            {
                _diagStartedAt = 0f;
            }
            _unloadAuto.SettingChanged += (_, __) => s_autoUnloadUselessThisSession = false;

            GarbageCollector.GCModeChanged += OnGcModeChanged;

            _lastTrim = Time.realtimeSinceStartup;
            Logger.LogInfo($"Loaded. incremental GC supported={GarbageCollector.isIncremental}, " +
                           $"slice default {GarbageCollector.incrementalTimeSliceNanoseconds / 1000000f:0.0}ms, " +
                           $"system RAM {SystemInfo.systemMemorySize} MB, VRAM {SystemInfo.graphicsMemorySize} MB, GC mode {GarbageCollector.GCMode}");
        }

        internal void OnDestroy()
        {
            GarbageCollector.GCModeChanged -= OnGcModeChanged;
            _combat.Unbind();
            _vram.Dispose();
            if (_sessionLog != null)
            {
                BepInEx.Logging.Logger.Listeners.Remove(_sessionLog);
                _sessionLog.Dispose();
            }
        }

        internal void Update()
        {
            float now = Time.realtimeSinceStartup;

            while (_mainThread.TryDequeue(out Action action))
            {
                action();
            }

            CheckHitch(now);
            if (_diagHotkey.Value.IsDown())
            {
                _diagMode.Value = !_diagMode.Value;
            }
            _frames.Tick(_inGame && _raidStartedAt >= 0f && now - _raidStartedAt > 5f);
            _profiler.CountFrame();
            TickProfilerInstall(now);

            if (_gc.Running)
            {
                if (!_gcEnabled.Value && _gc.IsAuto)
                {
                    _gc.Abort(_inGame, "설정에서 꺼서 중단");
                }
                else
                {
                    _gc.Tick(_gcSliceMs.Value, _inGame);
                }
            }

            TickAssetUnload(now);

            if (now < _nextSample && !_evaluateNow)
            {
                return;
            }

            _evaluateNow = false;
            _nextSample = now + 1f;

            bool inGame = GameHelper.IsInGame();
            if (inGame != _inGame)
            {
                _inGame = inGame;
                OnRaidStateChanged(now);
            }

            _combat.Poll();
            _snapshot = MemoryStats.Sample();
            SampleWorld();
            if (_inGame)
            {
                _report.Sample(_snapshot, _vram.Dedicated, _dead);
            }

            EvaluateWarnings(now);
            EvaluateMemorySuspects();
            EvaluateHitchSuspect();
            bool autoActive = _inGame || !_onlyInRaid.Value;

            if (_inGame)
            {
                EvaluateRaidStart(now);
            }

            if (autoActive)
            {
                EvaluateGc(now);
                EvaluateAssets(now);
                EvaluateTrim(now);
                EvaluateLeak(now);
                EvaluateLog(now);
            }

            if (_inGame)
            {
                EvaluateProfiler(now);
                EvaluateModObjects(now);
            }

            BuildTexts(now);
        }

        private void OnRaidStateChanged(float now)
        {
            _gcBaseline = -1;
            _gcPendingSince = -1f;
            _lastTrim = now;
            _trimPendingSince = -1f;
            _nextLog = now;
            _raidStartedAt = -1f;
            _inGameSince = now;
            _startUnloadDone = false;
            _nativeBaseline = -1;
            _unloadPendingSince = -1f;
            _weakUnloads = 0;
            _autoUnloadStopped = false;
            _leak.Reset();
            _leakNext = -1f;
            _leakPendingSince = -1f;

            if (_inGame)
            {
                _warnedNoIncremental = false;
                Logger.LogInfo($"Raid loading: GC mode {GarbageCollector.GCMode}, incremental={GarbageCollector.isIncremental}, " +
                               $"mono used {MemoryStats.Gb(MemoryStats.MonoUsed())} GB");
            }
            else
            {
                if (_assetPhase == AssetPhase.WaitingForGc)
                {
                    // Raid is being torn down; the game unloads assets itself on the way to the menu.
                    _assetPhase = AssetPhase.Idle;
                    _lastAssetResult = $"{DateTime.Now:HH:mm:ss} 레이드가 끝나서 취소";
                }

                _gc.Abort(false, "레이드가 끝나서 중단");
                _combat.Unbind();
                Logger.LogInfo("Left raid");
                _profiler.StopWindow(_profilerSuspectMs.Value, _profilerSuspectShare.Value / 100f, _allocSuspectMbPerMin.Value);
                FinishRaidReport();
                FinishFpsHistory();
            }
        }

        /// <summary>
        /// "Raid started" = the countdown is over (AbstractGame.Status == Started). That is the moment
        /// SPTVRAMCleaner used (PreloaderUI.ShowRaidStartInfo); polling the status needs no Harmony patch.
        /// </summary>
        private void EvaluateRaidStart(float now)
        {
            if (_raidStartedAt >= 0f)
            {
                return;
            }

            AbstractGame game = Singleton<AbstractGame>.Instance;
            bool started = game != null && game.Status == GameStatus.Started;
            if (!started && now - _inGameSince < RaidStartFallbackSeconds)
            {
                return;
            }

            _raidStartedAt = now;
            _nativeBaseline = _snapshot.Native;
            _nativeAtStart = _snapshot.Native;
            _deadAtStart = Math.Max(0, _dead);
            _deathSamples.Clear();
            _hitch.Reset();
            _warnings.ResetRaid();
            _frames.Reset();
            _profiler.ResetRaid();
            _gcFloors.Clear();
            _memSuspects.Clear();
            _memNotifiedThisRaid.Clear();
            _lastMemSuspectKey = string.Empty;
            _objects.Reset();
            _profilerNext = HitchModTrackingOn ? now : now + 60f;
            _hitchSuspectNotified = false;
            _objectsNext = now + 60f;
            _objectsPendingSince = -1f;
            try
            {
                _map = Singleton<GameWorld>.Instance?.LocationId;
            }
            catch (Exception)
            {
                _map = null;
            }
            _report.Begin(_snapshot, _dead);
            _leakNext = now + LeakFirstSnapshotDelay;
            Logger.LogInfo($"Raid started{(started ? string.Empty : " (status timeout)")}: {DescribeForLog(_snapshot)}");
            if (_unloadAuto.Value && s_autoUnloadUselessThisSession)
            {
                Logger.LogInfo("Auto asset unload stays off: it freed nothing in an earlier raid this session " +
                               "(toggle '레이드 중 자동 정리' in F12 to try again)");
            }
        }

        private bool IsQuietNow(float pendingSince, float maxDefer, float now)
        {
            if (!_waitForQuiet.Value)
            {
                return true;
            }

            if (_combat.IsQuiet(_quietSec.Value))
            {
                return true;
            }

            return maxDefer > 0f && now - pendingSince >= maxDefer;
        }

        // ---------------------------------------------------------------- GC

        private void EvaluateGc(float now)
        {
            if (!_gcEnabled.Value || _gc.Running)
            {
                _gcPendingSince = -1f;
                return;
            }

            long used = _snapshot.MonoUsed;
            if (_gcBaseline < 0 || used < _gcBaseline)
            {
                // First sample of the raid, or memory went down on its own (the game collected): new floor.
                _gcBaseline = used;
                return;
            }

            long growth = used - _gcBaseline;
            if (growth < (long)(_gcGrowthGb.Value * MemoryStats.BytesPerGb) ||
                (_gc.LastFinishTime >= 0 && now - _gc.LastFinishTime < _gcCooldownSec.Value))
            {
                _gcPendingSince = -1f;
                return;
            }

            if (_gcPendingSince < 0f)
            {
                _gcPendingSince = now;
            }

            if (!IsQuietNow(_gcPendingSince, _gcMaxDeferSec.Value, now))
            {
                return;
            }

            string waited = now - _gcPendingSince >= 1f ? $", waited {now - _gcPendingSince:0}s" : string.Empty;
            _gcPendingSince = -1f;
            if (_gc.Start($"auto, +{MemoryStats.Gb(growth)} GB{waited}{QuietTag()}", _gcAllowBlocking.Value, true))
            {
                return;
            }

            // Could not start (no incremental GC and blocking not allowed): say it once, then stop
            // re-checking every second until memory grows another step.
            _gcBaseline = used;
            if (!_warnedNoIncremental)
            {
                _warnedNoIncremental = true;
                Logger.LogWarning("Auto GC skipped: this game build has no incremental GC and the blocking fallback is off " +
                                  "(F12: '증분 GC 미지원 시 전체 GC 허용')");
            }
        }

        private void OnGcFinished()
        {
            _gcBaseline = _gc.LastUsedAfter;
            if (_inGame)
            {
                _report.AddGc(_gc.LastUsedBefore - _gc.LastUsedAfter, _gc.LastMaxSliceMs);
                if (_gc.LastUsedAfter > 0 && _raidStartedAt >= 0f)
                {
                    _gcFloors.Add(new KeyValuePair<float, long>(Time.realtimeSinceStartup, _gc.LastUsedAfter));
                }
            }

            if (_assetPhase == AssetPhase.WaitingForGc)
            {
                BeginUnload();
            }

            if (_trimAfterGc.Value)
            {
                StartTrim("after GC");
            }
        }

        private string QuietTag()
        {
            if (!_waitForQuiet.Value)
            {
                return string.Empty;
            }

            return _combat.InventoryOpen ? ", inventory open" : $", quiet {Mathf.Min(_combat.SecondsSinceCombat, 999f):0}s";
        }

        // ---------------------------------------------------------------- Asset unload

        private void EvaluateAssets(float now)
        {
            if (_assetPhase != AssetPhase.Idle)
            {
                return;
            }

            if (_inGame && _unloadAtStart.Value && !_startUnloadDone && _raidStartedAt >= 0f &&
                now - _raidStartedAt >= _unloadStartDelaySec.Value)
            {
                _startUnloadDone = true;
                // The game ran a full GC while loading, so no GC pass is needed first here.
                StartAssetUnload("raid start", false, false);
                return;
            }

            long native = _snapshot.Native;
            if (!_inGame || !_unloadAuto.Value || _autoUnloadStopped || s_autoUnloadUselessThisSession ||
                _raidStartedAt < 0f || native < 0)
            {
                _unloadPendingSince = -1f;
                return;
            }

            if (_nativeBaseline < 0 || native < _nativeBaseline)
            {
                _nativeBaseline = native;
                return;
            }

            long growth = native - _nativeBaseline;
            if (growth < (long)(_unloadNativeGrowthGb.Value * MemoryStats.BytesPerGb) ||
                now - _lastUnload < _unloadCooldownMin.Value * 60f)
            {
                _unloadPendingSince = -1f;
                return;
            }

            if (_unloadPendingSince < 0f)
            {
                _unloadPendingSince = now;
            }

            // Asset unload can hitch for seconds, so unlike GC it has no "max wait": quiet moment only.
            if (!IsQuietNow(_unloadPendingSince, 0f, now))
            {
                return;
            }

            string waited = now - _unloadPendingSince >= 1f ? $", waited {now - _unloadPendingSince:0}s" : string.Empty;
            _unloadPendingSince = -1f;
            StartAssetUnload($"auto, native +{MemoryStats.Gb(growth)} GB{waited}{QuietTag()}", _unloadGcFirst.Value, true);
        }

        private void StartAssetUnload(string reason, bool gcFirst, bool isAuto)
        {
            if (_assetPhase != AssetPhase.Idle)
            {
                return;
            }

            _assetReason = reason;
            _assetAuto = isAuto;
            _assetBefore = MemoryStats.Sample();
            _assetVramBefore = _vram.Dedicated;
            _assetStart = Time.realtimeSinceStartup;
            _assetLongestFrame = 0f;

            if (gcFirst && (_gc.Running || _gc.Start($"before asset unload ({reason})", false, false)))
            {
                _assetPhase = AssetPhase.WaitingForGc;
                _lastAssetResult = $"{DateTime.Now:HH:mm:ss} GC 먼저 진행 중...";
                return;
            }

            BeginUnload();
        }

        private void BeginUnload()
        {
            _assetPhase = AssetPhase.Unloading;
            _lastAssetResult = $"{DateTime.Now:HH:mm:ss} 에셋 내리는 중...";
            Logger.LogInfo($"UnloadUnusedAssets start ({_assetReason})");
            _assetOperation = Resources.UnloadUnusedAssets();
            _assetActiveFrame = Time.frameCount;
        }

        private void TickAssetUnload(float now)
        {
            if (_assetPhase == AssetPhase.Idle || _assetPhase == AssetPhase.WaitingForGc)
            {
                return;
            }

            // Longest frame while the unload runs = the hitch the player actually felt.
            _assetLongestFrame = Mathf.Max(_assetLongestFrame, Time.unscaledDeltaTime);
            _assetActiveFrame = Time.frameCount;

            if (_assetPhase == AssetPhase.Unloading)
            {
                if (_assetOperation != null && !_assetOperation.isDone)
                {
                    return;
                }

                _assetOperation = null;
                _assetPhase = AssetPhase.Measuring;
                _assetDone = now;
                return;
            }

            // Measuring: the VRAM counter refreshes every 5 s, so wait a moment before reading "after".
            if (now - _assetDone < UnloadMeasureDelaySeconds)
            {
                return;
            }

            FinishAssetUnload(now);
        }

        private void FinishAssetUnload(float now)
        {
            _assetPhase = AssetPhase.Idle;
            _report.AddAssetUnload();
            _lastUnload = now;

            MemorySnapshot after = MemoryStats.Sample();
            long vramAfter = _vram.Dedicated;
            long nativeFreed = _assetBefore.Native >= 0 && after.Native >= 0 ? _assetBefore.Native - after.Native : 0;
            long vramFreed = _assetVramBefore >= 0 && vramAfter >= 0 ? _assetVramBefore - vramAfter : 0;
            float unloadSeconds = _assetDone - _assetStart;

            if (after.Native >= 0)
            {
                _nativeBaseline = after.Native;
            }

            string vramText = vramAfter >= 0 ? $", VRAM {MemoryStats.Gb(_assetVramBefore)} → {MemoryStats.Gb(vramAfter)} GB" : string.Empty;
            _lastAssetResult = $"{DateTime.Now:HH:mm:ss} ({(_assetAuto ? "자동" : _assetReason == "raid start" ? "레이드 시작" : "수동")}) " +
                               $"네이티브 {MemoryStats.Gb(_assetBefore.Native)} → {MemoryStats.Gb(after.Native)} GB{vramText}, " +
                               $"가장 긴 프레임 {_assetLongestFrame * 1000f:0}ms";

            Logger.LogInfo($"UnloadUnusedAssets done ({_assetReason}) in {unloadSeconds:0.0}s: native {MemoryStats.Gb(_assetBefore.Native)} -> " +
                           $"{MemoryStats.Gb(after.Native)} GB (freed {MemoryStats.Gb(nativeFreed)}), VRAM {MemoryStats.Gb(_assetVramBefore)} -> " +
                           $"{MemoryStats.Gb(vramAfter)} GB (freed {MemoryStats.Gb(vramFreed)}), working set {MemoryStats.Gb(_assetBefore.WorkingSet)} -> " +
                           $"{MemoryStats.Gb(after.WorkingSet)} GB, longest frame {_assetLongestFrame * 1000f:0}ms");

            if (!_assetAuto)
            {
                return;
            }

            // Adaptive stop: if unloading assets gives almost nothing back, the native growth is a leak
            // in some mod's native/Unity objects that are still referenced - hitching for it is pointless.
            if (nativeFreed < (long)(WeakUnloadGb * MemoryStats.BytesPerGb))
            {
                _weakUnloads++;
                if (_weakUnloads >= 2)
                {
                    _autoUnloadStopped = true;
                    s_autoUnloadUselessThisSession = true;
                    Logger.LogWarning("Auto asset unload stopped until the game restarts: two unloads in a row freed < 0.5 GB. " +
                                      "The native growth is not unused assets (likely something still referenced by a mod). " +
                                      "Turn on the leak tracker (F12 '누수 추적 켜기') to see what grows.");
                }
            }
            else
            {
                _weakUnloads = 0;
            }
        }

        // ---------------------------------------------------------------- Working set trim

        private void EvaluateTrim(float now)
        {
            if (!_trimEnabled.Value || now - _lastTrim < _trimIntervalSec.Value)
            {
                _trimPendingSince = -1f;
                return;
            }

            string reason;
            if (!_trimOnlyLowRam.Value)
            {
                reason = "interval";
            }
            else if (_snapshot.SystemTotal > 0 && _snapshot.SystemAvailablePercent < _trimLowRamPercent.Value)
            {
                reason = $"low RAM {_snapshot.SystemAvailablePercent:0}%";
            }
            else
            {
                _trimPendingSince = -1f;
                return;
            }

            if (_trimPendingSince < 0f)
            {
                _trimPendingSince = now;
                _trimPendingReason = reason;
            }

            if (!IsQuietNow(_trimPendingSince, TrimMaxDeferSeconds, now))
            {
                return;
            }

            _trimPendingSince = -1f;
            StartTrim(_trimPendingReason + QuietTag());
        }

        /// <summary>
        /// EmptyWorkingSet on a 40 GB working set took 3.5 s in a real log (2026-09-29) and froze the game
        /// for all of it because v2.0.0 called it from Update. The call does not need the main thread.
        /// </summary>
        private void StartTrim(string reason)
        {
            if (Interlocked.CompareExchange(ref _trimRunning, 1, 0) != 0)
            {
                return;
            }

            _lastTrim = Time.realtimeSinceStartup;
            _lastTrimResult = $"{DateTime.Now:HH:mm:ss} 진행 중... ({reason})";

            ThreadPool.QueueUserWorkItem(_ =>
            {
                long before = MemoryStats.ReadWorkingSet();
                var watch = Stopwatch.StartNew();
                bool ok = MemoryStats.EmptyWorkingSet();
                watch.Stop();
                long after = MemoryStats.ReadWorkingSet();

                _mainThread.Enqueue(() =>
                {
                    Interlocked.Exchange(ref _trimRunning, 0);
                    _report.AddTrim();
                    _lastTrimResult = ok
                        ? $"{DateTime.Now:HH:mm:ss} 워킹셋 {MemoryStats.Gb(before)} → {MemoryStats.Gb(after)} GB ({reason})"
                        : $"{DateTime.Now:HH:mm:ss} 실패 (윈도우 API 호출 불가)";
                    Logger.LogInfo($"Working set trim ({reason}): ok={ok}, {MemoryStats.Gb(before)} -> {MemoryStats.Gb(after)} GB, " +
                                   $"background call {watch.Elapsed.TotalMilliseconds:0}ms");
                });
            });
        }

        // ---------------------------------------------------------------- Hitch detector / raid report / warnings

        private void CheckHitch(float now)
        {
            int previousFrame = Time.frameCount - 1;
            if (_gc.LastTickFrame == previousFrame)
            {
                _hitch.NoteGc(_gc.LastTickMs);
            }

            if (_leakFrame == previousFrame)
            {
                _hitch.NoteLeakSnapshot(_leakMs);
            }

            if (_assetActiveFrame >= previousFrame)
            {
                _hitch.NoteAssetUnload();
            }

            if (_trimRunning != 0)
            {
                _hitch.NoteTrimRunning();
            }

            // Skip the first seconds after the countdown: spawn waves and streaming make those frames noisy.
            bool active = _hitchEnabled.Value && _inGame && _raidStartedAt >= 0f && now - _raidStartedAt > 5f;
            _hitch.Check(active, _hitchThresholdMs.Value, _hitchContext, HitchModTrackingOn ? _profiler : null,
                Time.realtimeSinceStartup - _lastSpawnTime < 1.5f);
        }

        // ---------------------------------------------------------------- Mod profiler / mod objects / FPS history

        private void TickProfilerInstall(float now)
        {
            if (!_profilerEnabled.Value || _inGame || now < 20f)
            {
                return;
            }

            // Install in the menu only: patching ~1-3k methods is a few seconds of work, spread over frames.
            if (_profiler.Status == ModCostProfiler.State.NotInstalled)
            {
                _profiler.BeginInstall();
            }
            else if (_profiler.Status == ModCostProfiler.State.Installing)
            {
                _profiler.InstallStep(40);
            }
        }

        private void EvaluateProfiler(float now)
        {
            if (!_profilerEnabled.Value || _profiler.Status != ModCostProfiler.State.Ready || _raidStartedAt < 0f)
            {
                if (_profiler.Measuring)
                {
                    _profiler.StopWindow(_profilerSuspectMs.Value, _profilerSuspectShare.Value / 100f, _allocSuspectMbPerMin.Value);
                }

                return;
            }

            if (_profiler.Measuring)
            {
                if (_profiler.WindowElapsed < _profilerWindowSec.Value)
                {
                    return;
                }

                _profiler.StopWindow(_profilerSuspectMs.Value, _profilerSuspectShare.Value / 100f, _allocSuspectMbPerMin.Value);
                // Hitch cause tracking needs per-frame mod times all raid long, so it implies continuous measuring.
                _profilerNext = _profilerContinuous.Value || HitchModTrackingOn ? now : now + _profilerIntervalMin.Value * 60f;
            }

            if (now >= _profilerNext)
            {
                _profiler.StartWindow();
            }
        }

        private void EvaluateModObjects(float now)
        {
            if (!ObjectsOn || _raidStartedAt < 0f || now < _objectsNext)
            {
                _objectsPendingSince = -1f;
                return;
            }

            if (_objectsPendingSince < 0f)
            {
                _objectsPendingSince = now;
            }

            if (!IsQuietNow(_objectsPendingSince, LeakMaxDeferSeconds, now))
            {
                return;
            }

            _objectsPendingSince = -1f;
            _objectsNext = now + _profilerIntervalMin.Value * 60f;
            var watch = Stopwatch.StartNew();
            try
            {
                _objects.Snapshot(_objectsSuspectGrowth.Value);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Mod object count failed: {ex}");
            }

            watch.Stop();
            _leakFrame = Time.frameCount;
            _leakMs = watch.Elapsed.TotalMilliseconds;
        }

        private void FinishFpsHistory()
        {
            if (!_fpsHistoryEnabled.Value || _frames.Frames == 0)
            {
                return;
            }

            string warning = _history.AddAndCompare(_map, (float)(_frames.TotalSeconds / 60.0), _frames.AverageFps,
                _frames.OnePercentLowFps(), Math.Max(0, _dead - _deadAtStart), _fpsDropPercent.Value, out string logLine);
            if (logLine != null)
            {
                Logger.LogInfo(logLine);
            }

            if (warning == null)
            {
                return;
            }

            Logger.LogWarning(warning);
            if (_fpsNotify.Value)
            {
                Notify(warning, true);
            }
        }


        /// <summary>Freezes each frame's per-mod times right after the frame (rendering and OnGUI included) ends.</summary>
        private System.Collections.IEnumerator EndOfFrameLoop()
        {
            while (true)
            {
                yield return _endOfFrame;
                _profiler.EndFrame();
            }
        }

        private bool HitchModTrackingOn => _hitchEnabled.Value && _profilerEnabled.Value && (_hitchModTracking.Value || _diagMode.Value);

        private bool OverlayOn => _showOverlay.Value || _diagMode.Value;

        private bool LeakOn => _leakEnabled.Value || (_diagMode.Value && _diagHeavy.Value);

        private bool ObjectsOn => _objectsEnabled.Value || (_diagMode.Value && _diagHeavy.Value);

        // ---------------------------------------------------------------- Diagnostic mode (one key / one button)

        private void OnDiagChanged()
        {
            float now = Time.realtimeSinceStartup;
            if (_diagMode.Value)
            {
                _diagStartedAt = now;
                if (_inGame && _raidStartedAt >= 0f)
                {
                    _profilerNext = now; // start measuring right away
                    if (LeakOn && _leakNext < 0f)
                    {
                        _leakNext = now;
                    }
                }

                string start = $"=== 원인 추적 시작 {DateTime.Now:HH:mm:ss} (화면 표시 + 모드별 상시 측정 + 끊김 원인 추적" +
                               (_diagHeavy.Value ? " + 누수 추적·오브젝트 수" : string.Empty) + ") ===";
                _sessionLog?.WriteBlock(start);
                Logger.LogInfo("Diagnostic mode ON");
                Notify($"RAM 클리너: 원인 추적 시작 — {_diagHotkey.Value} 로 끄기", false);
                return;
            }

            string summary = BuildDiagSummary(now);
            _sessionLog?.WriteBlock(summary);
            Logger.LogInfo("Diagnostic mode OFF");
            _diagStartedAt = -1f;
            Notify("RAM 클리너: 원인 추적 종료 — 요약은 전용 로그(BepInEx\\RamCleaner)에 저장", false);
        }

        private string BuildDiagSummary(float now)
        {
            var sb = new StringBuilder();
            float minutes = _diagStartedAt >= 0f ? (now - _diagStartedAt) / 60f : 0f;
            sb.Append($"=== 원인 추적 종료 {DateTime.Now:HH:mm:ss} ({minutes:0}분) — 요약 ===\n");
            sb.Append($"FPS: 지금 {_frames.CurrentFps:0}, 레이드 평균 {_frames.AverageFps:0}, 1% 저점 {_frames.OnePercentLowFps():0}\n");
            if (_profiler.LastResult.Count > 0)
            {
                sb.Append("모드별 부하(프레임당 ms): ")
                  .Append(string.Join(", ", _profiler.LastResult.Take(8).Select(kv => $"{kv.Key} {kv.Value:0.00}")))
                  .Append($" / 프레임 {_profiler.LastFrameMs:0.0}ms\n");
            }

            if (_profiler.LastAlloc.Count > 0)
            {
                sb.Append($"모드별 메모리 생성(MB/분, {_profiler.AllocMethod}): ")
                  .Append(string.Join(", ", _profiler.LastAlloc.Take(8).Select(kv => $"{kv.Key} {kv.Value:0}"))).Append('\n');
            }

            List<KeyValuePair<string, KeyValuePair<int, float>>> causes = _hitch.Causes();
            if (causes.Count > 0)
            {
                sb.Append($"끊김 원인({_hitch.Count}회): ")
                  .Append(string.Join(", ", causes.Select(c => $"{c.Key} {c.Value.Key}회(최대 {c.Value.Value:0}ms)"))).Append('\n');
            }

            double slope = GcFloorSlopeMbPerMin();
            if (!double.IsNaN(slope))
            {
                sb.Append($"GC 뒤 남는 관리 메모리: 분당 {slope:+0;-0}MB\n");
            }

            double perDeath = RecentPerDeathMb();
            if (perDeath >= 0)
            {
                sb.Append($"사망 1명당 메모리(최근): {perDeath:0}MB\n");
            }

            sb.Append("의심: ");
            var suspects = new List<string>();
            if (_profiler.Suspect != null)
            {
                suspects.Add("프레임 — " + _profiler.Suspect);
            }

            if (_hitch.Suspect != null)
            {
                suspects.Add("끊김 — " + _hitch.Suspect);
            }

            suspects.AddRange(_memSuspects);
            sb.Append(suspects.Count > 0 ? string.Join(" / ", suspects) : "없음");
            return sb.ToString();
        }

        private void EvaluateHitchSuspect()
        {
            if (_hitch.Suspect == null || _hitchSuspectNotified || !_hitchEnabled.Value)
            {
                return;
            }

            _hitchSuspectNotified = true;
            Logger.LogWarning($"[hitch suspect] {_hitch.Suspect}");
            if (_hitchNotify.Value)
            {
                _report.AddWarning();
                Notify("끊김 의심: " + _hitch.Suspect, true);
            }
        }

        private void FinishRaidReport()
        {
            if (!_report.Active)
            {
                return;
            }

            _report.End(_hitch, _frames.AverageFps, _frames.OnePercentLowFps(), _profiler.Suspect ?? _hitch.Suspect ?? (_memSuspects.Count > 0 ? _memSuspects[0] : null),
                out string logLine, out string notification);
            _lastReport = _report.LastSummaryKorean;
            if (!_reportEnabled.Value)
            {
                return;
            }

            Logger.LogInfo(logLine);
            if (_reportNotify.Value)
            {
                Notify(notification, false);
            }
        }

        private void EvaluateWarnings(float now)
        {
            if (!_warnEnabled.Value)
            {
                return;
            }

            string warning = _warnings.Evaluate(_snapshot, _inGame ? _vram.Dedicated : -1, _warnCommitPercent.Value,
                _warnVram.Value, _warnVramPercent.Value, _warnVramSeconds.Value, now);
            if (warning == null)
            {
                return;
            }

            _report.AddWarning();
            Logger.LogWarning(warning);
            if (_warnNotify.Value)
            {
                Notify(warning, true);
            }
        }

        private void Notify(string text, bool warning)
        {
            try
            {
                ShowGameNotification(text, warning);
            }
            catch (Exception ex)
            {
                // UI not ready (loading screen) or the API changed in a game update: the log line is already written.
                Logger.LogDebug($"Notification not shown: {ex.Message}");
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void ShowGameNotification(string text, bool warning)
        {
            if (warning)
            {
                EFT.Communications.NotificationManager.DisplayWarningNotification(text, EFT.Communications.ENotificationDurationType.Long);
            }
            else
            {
                EFT.Communications.NotificationManager.DisplayMessageNotification(text, EFT.Communications.ENotificationDurationType.Long,
                    EFT.Communications.ENotificationIconType.Note);
            }
        }

        // ---------------------------------------------------------------- Leak tracker

        private void EvaluateLeak(float now)
        {
            if (!_inGame || !LeakOn || _leakNext < 0f || now < _leakNext)
            {
                _leakPendingSince = -1f;
                return;
            }

            if (_leakPendingSince < 0f)
            {
                _leakPendingSince = now;
            }

            if (!IsQuietNow(_leakPendingSince, LeakMaxDeferSeconds, now))
            {
                return;
            }

            _leakPendingSince = -1f;
            _leakNext = now + _leakIntervalMin.Value * 60f;
            RunLeakSnapshot(_leak.Snapshots == 0 ? "baseline" : "interval" + QuietTag());
        }

        private void RunLeakSnapshot(string reason)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                _leak.Snapshot(reason);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Leak snapshot failed: {ex}");
            }

            watch.Stop();
            _leakFrame = Time.frameCount;
            _leakMs = watch.Elapsed.TotalMilliseconds;
        }

        // ---------------------------------------------------------------- Log / status / UI

        private void EvaluateLog(float now)
        {
            int interval = _logIntervalSec.Value;
            if (interval <= 0 || now < _nextLog)
            {
                return;
            }

            _nextLog = now + interval;
            Logger.LogInfo($"[mem] {DescribeForLog(_snapshot)} | {DescribeWorld()} | {DescribePerDeath(false)} | GC {_snapshot.GcMode}{(_gc.Running ? " (collecting)" : string.Empty)} | " +
                           $"combat {(_combat.InventoryOpen ? "inventory" : Mathf.Min(_combat.SecondsSinceCombat, 9999f).ToString("0") + "s ago")}");
        }

        private string DescribeForLog(MemorySnapshot s)
        {
            return $"mono used {MemoryStats.Gb(s.MonoUsed)} / reserved {MemoryStats.Gb(s.MonoReserved)} GB | " +
                   $"native {MemoryStats.Gb(s.Native)} GB | working set {MemoryStats.Gb(s.WorkingSet)} GB | private {MemoryStats.Gb(s.PrivateBytes)} GB | " +
                   $"VRAM {MemoryStats.Gb(_vram.Dedicated)} GB (+shared {MemoryStats.Gb(_vram.Shared)}) | " +
                   $"system free {MemoryStats.Gb(s.SystemAvailable)}/{MemoryStats.Gb(s.SystemTotal)} GB";
        }

        private void SampleWorld()
        {
            int previousAlive = _alive;
            _alive = -1;
            _dead = -1;
            try
            {
                GameWorld world = _inGame ? Singleton<GameWorld>.Instance : null;
                if (world != null)
                {
                    int alive = world.AllAlivePlayersList.Count;
                    if (previousAlive >= 0 && alive > previousAlive)
                    {
                        _lastSpawnTime = Time.realtimeSinceStartup;
                    }

                    _alive = alive;
                    _dead = Math.Max(0, world.AllPlayersEverExisted.Count() - _alive);
                }
            }
            catch (Exception)
            {
                // world being torn down
            }

            if (_raidStartedAt < 0f || _dead < 0 || _snapshot.Native < 0)
            {
                return;
            }

            int last = _deathSamples.Count > 0 ? _deathSamples[_deathSamples.Count - 1].Key : -1;
            if (_dead != last)
            {
                _deathSamples.Add(new KeyValuePair<int, long>(_dead, _snapshot.Native));
            }
        }

        /// <summary>"+X MB per death" over the whole raid and over the last 10 deaths.</summary>
        /// <summary>Recent memory per death in MB (last ~10 deaths, else the raid average); -1 if too few deaths.</summary>
        private double RecentPerDeathMb()
        {
            int deaths = _dead - _deadAtStart;
            if (_nativeAtStart < 0 || deaths < 5)
            {
                return -1;
            }

            for (int i = _deathSamples.Count - 1; i >= 0; i--)
            {
                if (_deathSamples[i].Key <= _dead - 10 && _dead > _deathSamples[i].Key)
                {
                    return (_snapshot.Native - _deathSamples[i].Value) / (1024d * 1024d) / (_dead - _deathSamples[i].Key);
                }
            }

            return (_snapshot.Native - _nativeAtStart) / (1024d * 1024d) / deaths;
        }

        /// <summary>
        /// How fast the managed memory left over after our GCs rises (MB/min). Garbage is gone after a GC, so a
        /// floor that keeps climbing means something keeps references it should drop: a real managed leak.
        /// Needs at least 3 GCs spread over 10+ minutes.
        /// </summary>
        private double GcFloorSlopeMbPerMin()
        {
            if (_gcFloors.Count < 3)
            {
                return double.NaN;
            }

            KeyValuePair<float, long> first = _gcFloors[0];
            KeyValuePair<float, long> last = _gcFloors[_gcFloors.Count - 1];
            float minutes = (last.Key - first.Key) / 60f;
            return minutes < 10f ? double.NaN : (last.Value - first.Value) / (1024d * 1024d) / minutes;
        }

        /// <summary>Collects every memory suspect into _memSuspects (Korean, one line each). Called once per second.</summary>
        private void EvaluateMemorySuspects()
        {
            _memSuspects.Clear();
            if (!_inGame || !_memSuspectEnabled.Value)
            {
                return;
            }

            if (_profiler.AllocSuspect != null)
            {
                _memSuspects.Add("메모리 의심: " + _profiler.AllocSuspect);
            }

            double slope = GcFloorSlopeMbPerMin();
            if (!double.IsNaN(slope) && slope >= _retainedSuspectMbPerMin.Value)
            {
                string top = _profiler.LastAlloc.Count > 0 ? $" — 메모리를 가장 많이 만드는 모드: {_profiler.LastAlloc[0].Key} ({_profiler.LastAlloc[0].Value:0}MB/분)" : string.Empty;
                _memSuspects.Add($"관리 메모리 누수 의심: GC 뒤에도 분당 {slope:0}MB씩 남음{top}");
            }

            double perDeath = RecentPerDeathMb();
            if (perDeath >= _perDeathSuspectMb.Value)
            {
                _memSuspects.Add($"봇 장비 메모리 과다: 사망 1명당 {perDeath:0}MB — 봇 스폰 모드(APBS 등)의 모드 아이템 종류를 줄여 보세요");
            }

            if (_objects.Suspect != null)
            {
                _memSuspects.Add("오브젝트 증가 의심: " + _objects.Suspect);
            }

            // Log/notify when the set of suspects changes, not when their numbers move (per-death MB changes every second).
            string key = string.Join("|", _memSuspects.Select(line => line.Substring(0, Math.Min(6, line.Length)))) +
                         "|" + _profiler.AllocSuspect + "|" + _objects.Suspect;
            if (key == _lastMemSuspectKey)
            {
                return;
            }

            _lastMemSuspectKey = key;
            foreach (string line in _memSuspects)
            {
                string kind = line.Substring(0, Math.Min(6, line.Length));
                Logger.LogWarning($"[mem suspect] {line}");
                if (_memSuspectNotify.Value && _memNotifiedThisRaid.Add(kind))
                {
                    _report.AddWarning();
                    Notify(line, true);
                }
            }
        }

        private string DescribePerDeath(bool korean)
        {
            int deaths = _dead - _deadAtStart;
            if (_nativeAtStart < 0 || deaths < 3)
            {
                return korean ? "사망 1명당 메모리: 아직 표본 부족 (사망 3명 이상부터)" : "per death n/a";
            }

            double total = (_snapshot.Native - _nativeAtStart) / (1024d * 1024d) / deaths;
            string recent = string.Empty;
            KeyValuePair<int, long>? from = null;
            for (int i = _deathSamples.Count - 1; i >= 0; i--)
            {
                if (_deathSamples[i].Key <= _dead - 10)
                {
                    from = _deathSamples[i];
                    break;
                }
            }

            if (from.HasValue && _dead > from.Value.Key)
            {
                double last10 = (_snapshot.Native - from.Value.Value) / (1024d * 1024d) / (_dead - from.Value.Key);
                recent = korean ? $", 최근 {_dead - from.Value.Key}명 기준 {last10:0}MB" : $", last {_dead - from.Value.Key} deaths {last10:0} MB";
            }

            return korean
                ? $"사망 1명당 메모리: 레이드 전체 {total:0}MB{recent} (사망 {deaths}명)"
                : $"per death {total:0} MB over {deaths} deaths{recent}";
        }

        /// <summary>Bots and texture memory, to line the native growth up against what the raid is doing.</summary>
        private string DescribeWorld()
        {
            string bots = _dead >= 0 ? $"players alive {_alive}, dead {_dead}" : "bots ?";
            return $"{bots} | textures {MemoryStats.Gb((long)Texture.currentTextureMemory)} GB " +
                   $"(non-streaming {MemoryStats.Gb((long)Texture.nonStreamingTextureMemory)})";
        }

        private void OnGcModeChanged(GarbageCollector.Mode mode)
        {
            if (!GcRunner.ChangingMode)
            {
                Logger.LogInfo($"Game switched GC mode to {mode}");
            }
        }

        private void BuildTexts(float now)
        {
            MemorySnapshot s = _snapshot;
            string gcState = _gc.Running ? $"정리 중 {_gc.Elapsed:0}초" : MemoryStats.ModeName(s.GcMode);
            long vram = _vram.Dedicated;

            var sb = new StringBuilder(768);
            sb.Append("관리 메모리(Mono 힙): 사용 ").Append(MemoryStats.Gb(s.MonoUsed))
              .Append(" GB / 확보 ").Append(MemoryStats.Gb(s.MonoReserved)).Append(" GB\n");
            if (s.SystemTotal > 0)
            {
                sb.Append("네이티브 메모리(에셋·엔진·모드): ").Append(MemoryStats.Gb(s.Native)).Append(" GB");
                if (_nativeBaseline >= 0 && _inGame)
                {
                    sb.Append(" (마지막 기준점 대비 +").Append(MemoryStats.Gb(Math.Max(0, s.Native - _nativeBaseline))).Append(" GB)");
                }

                sb.Append('\n');
                sb.Append("게임 전체: 실제 RAM(워킹셋) ").Append(MemoryStats.Gb(s.WorkingSet))
                  .Append(" GB / 커밋 ").Append(MemoryStats.Gb(s.PrivateBytes)).Append(" GB\n");
                sb.Append("시스템 여유 RAM: ").Append(MemoryStats.Gb(s.SystemAvailable)).Append(" / ")
                  .Append(MemoryStats.Gb(s.SystemTotal)).Append(" GB (").Append(s.SystemAvailablePercent.ToString("0"))
                  .Append("%)\n");
            }

            sb.Append("VRAM(이 게임): ");
            if (vram >= 0)
            {
                sb.Append("전용 ").Append(MemoryStats.Gb(vram)).Append(" GB / 공유 ").Append(MemoryStats.Gb(_vram.Shared))
                  .Append(" GB (그래픽카드 ").Append((SystemInfo.graphicsMemorySize / 1024f).ToString("0.0")).Append(" GB)\n");
            }
            else
            {
                sb.Append(_vram.Failed ? "측정 불가 (윈도우 성능 카운터 없음)\n" : "측정 중...\n");
            }

            if (_inGame && _raidStartedAt >= 0f)
            {
                sb.Append(DescribePerDeath(true)).Append('\n');
            }

            sb.Append("GC 상태: ").Append(gcState)
              .Append(" · 나눠서 하는 GC(증분): ").Append(GarbageCollector.isIncremental ? "지원" : "미지원").Append('\n');

            if (_inGame)
            {
                sb.Append("전투 상태: ");
                if (_combat.InventoryOpen)
                {
                    sb.Append("인벤토리 열림 (정리하기 좋은 순간)");
                }
                else if (_combat.IsQuiet(_quietSec.Value))
                {
                    sb.Append("조용함");
                }
                else
                {
                    sb.Append("전투 중 (마지막 활동 ").Append(_combat.SecondsSinceCombat.ToString("0")).Append("초 전)");
                }

                sb.Append('\n');
            }

            AppendPending(sb, now);

            if (_gcBaseline >= 0 && !_gc.Running)
            {
                sb.Append("다음 자동 GC: 사용량 ")
                  .Append(MemoryStats.Gb(_gcBaseline + (long)(_gcGrowthGb.Value * MemoryStats.BytesPerGb))).Append(" GB 도달 시\n");
            }

            if (_autoUnloadStopped || s_autoUnloadUselessThisSession)
            {
                sb.Append("레이드 중 자동 에셋 정리: 게임 끌 때까지 중단됨 (정리해도 거의 안 줄어서 — 모드 누수 의심, '누수 추적'을 켜 보세요)\n");
            }

            sb.Append("마지막 GC 정리: ").Append(_gc.LastResult).Append('\n');
            sb.Append("마지막 워킹셋 정리: ").Append(_lastTrimResult).Append('\n');
            sb.Append("마지막 에셋 정리: ").Append(_lastAssetResult).Append('\n');
            sb.Append("FPS: 지금 ").Append(_frames.CurrentFps.ToString("0"));
            if (_frames.Frames > 0)
            {
                sb.Append(" · 레이드 평균 ").Append(_frames.AverageFps.ToString("0"))
                  .Append(" · 1% 저점 ").Append(_frames.OnePercentLowFps().ToString("0"));
            }

            sb.Append('\n');
            if (_profilerEnabled.Value)
            {
                sb.Append("모드별 부하: ").Append(DescribeProfilerState()).Append('\n');
                if (_profiler.LastResult.Count > 0)
                {
                    sb.Append("  ");
                    for (int i = 0; i < Math.Min(6, _profiler.LastResult.Count); i++)
                    {
                        if (i > 0)
                        {
                            sb.Append(", ");
                        }

                        sb.Append(_profiler.LastResult[i].Key).Append(' ').Append(_profiler.LastResult[i].Value.ToString("0.0")).Append("ms");
                    }

                    sb.Append('\n');
                }
            }

            if (_profilerEnabled.Value && _profiler.LastAlloc.Count > 0)
            {
                sb.Append("모드별 메모리 생성(MB/분, ").Append(_profiler.AllocMethod).Append("): ");
                for (int i = 0; i < Math.Min(6, _profiler.LastAlloc.Count); i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }

                    sb.Append(_profiler.LastAlloc[i].Key).Append(' ').Append(_profiler.LastAlloc[i].Value.ToString("0"));
                }

                sb.Append('\n');
            }

            double floorSlope = GcFloorSlopeMbPerMin();
            if (!double.IsNaN(floorSlope))
            {
                sb.Append("GC 뒤에도 남는 관리 메모리: 분당 ").Append(floorSlope.ToString("+0;-0")).Append("MB (").Append(_gcFloors.Count).Append("회 기준)\n");
            }

            foreach (string suspectLine in _memSuspects)
            {
                sb.Append("-> ").Append(suspectLine).Append('\n');
            }

            if (ObjectsOn)
            {
                sb.Append("모드별 오브젝트: ").Append(_objects.LastSummary).Append('\n');
            }

            if (_fpsHistoryEnabled.Value)
            {
                sb.Append("이전 레이드 비교: ").Append(_history.LastComparison).Append('\n');
            }

            if (_hitchEnabled.Value)
            {
                sb.Append("끊김(이번 레이드): ").Append(_hitch.Count).Append("회, 그중 이 모드 ").Append(_hitch.Ours)
                  .Append("회, 최대 ").Append(_hitch.MaxMs.ToString("0")).Append("ms · 마지막: ").Append(_hitch.LastText).Append('\n');
                List<KeyValuePair<string, KeyValuePair<int, float>>> causes = _hitch.Causes();
                if (causes.Count > 0)
                {
                    sb.Append("  끊김 원인: ");
                    for (int i = 0; i < Math.Min(6, causes.Count); i++)
                    {
                        if (i > 0)
                        {
                            sb.Append(", ");
                        }

                        sb.Append(causes[i].Key).Append(' ').Append(causes[i].Value.Key).Append("회(최대 ")
                          .Append(causes[i].Value.Value.ToString("0")).Append("ms)");
                    }

                    sb.Append('\n');
                }

                if (_hitch.Suspect != null)
                {
                    sb.Append("-> 끊김 의심: ").Append(_hitch.Suspect).Append('\n');
                }
            }

            if (_warnEnabled.Value)
            {
                sb.Append("마지막 경고: ").Append(_warnings.LastText).Append('\n');
            }

            if (_reportEnabled.Value)
            {
                sb.Append("마지막 레이드 결산: ").Append(_lastReport).Append('\n');
            }

            sb.Append("누수 추적: ").Append(LeakOn || _leak.Snapshots > 0 ? _leak.LastSummary : "꺼짐").Append('\n');
            sb.Append("원인 추적 모드: ").Append(_diagMode.Value ? $"켜짐 ({(Time.realtimeSinceStartup - Math.Max(0f, _diagStartedAt)) / 60f:0}분째)" : "꺼짐")
              .Append($" — 단축키 {_diagHotkey.Value}\n");
            sb.Append("전용 로그: ").Append(_sessionLog?.FilePath ?? "만들 수 없음");
            _statusText = sb.ToString();

            if (OverlayOn)
            {
                BuildOverlay(now, s, gcState, vram);
            }
            else
            {
                _panel.Begin();
            }
        }

        private void AppendPending(StringBuilder sb, float now)
        {
            if (_gcPendingSince >= 0f)
            {
                sb.Append("대기 중: GC (전투가 끝나길 기다리는 중, ").Append((now - _gcPendingSince).ToString("0")).Append("초)\n");
            }

            if (_unloadPendingSince >= 0f)
            {
                sb.Append("대기 중: 에셋 정리 (조용한 순간 또는 인벤토리 열 때, ").Append((now - _unloadPendingSince).ToString("0")).Append("초)\n");
            }

            if (_trimPendingSince >= 0f)
            {
                sb.Append("대기 중: 워킹셋 정리 (").Append((now - _trimPendingSince).ToString("0")).Append("초)\n");
            }
        }

        private string DescribeProfilerState()
        {
            switch (_profiler.Status)
            {
                case ModCostProfiler.State.NotInstalled:
                    return "메인 메뉴에서 측정 장치 설치 대기";
                case ModCostProfiler.State.Installing:
                    return "측정 장치 설치 중...";
            }

            if (_profiler.Measuring)
            {
                return $"측정 중 {_profiler.WindowElapsed:0}/{_profilerWindowSec.Value}초";
            }

            return _profiler.LastSummary;
        }

        /// <summary>Rebuilds the top-left panel (once per second): one titled section per topic.</summary>
        private void BuildOverlay(float now, MemorySnapshot s, string gcState, long vram)
        {
            _panel.Begin();

            if (_diagMode.Value)
            {
                float minutes = (now - Math.Max(0f, _diagStartedAt)) / 60f;
                _panel.Header($"● 원인 추적 중 — {_diagHotkey.Value} 로 끄기", OverlayPanel.Yellow, $"{minutes:0}분째");
            }

            // --- memory
            _panel.Header("메모리", OverlayPanel.Blue, $"GC {gcState}");
            string vramText = vram >= 0 ? MemoryStats.Gb(vram) : "?";
            _panel.Text($"힙 {MemoryStats.Gb(s.MonoUsed)} · 네이티브 {MemoryStats.Gb(s.Native)} · VRAM {vramText} · 시스템 여유 {MemoryStats.Gb(s.SystemAvailable)} (GB)");
            if (_inGame)
            {
                double slope = GcFloorSlopeMbPerMin();
                double perDeath = RecentPerDeathMb();
                string extra = (perDeath >= 0 ? $"사망당 {perDeath:0}MB" : "사망당 -") +
                               (!double.IsNaN(slope) ? $" · GC 뒤 남는 양 {slope:+0;-0}MB/분" : string.Empty);
                _panel.Text(extra, OverlayPanel.Dim);
            }

            for (int i = 0; i < _memSuspects.Count; i++)
            {
                _panel.Text(_memSuspects[i], OverlayPanel.Red);
            }

            // --- frames
            if (_overlayFps.Value)
            {
                _panel.Header("프레임", OverlayPanel.Green, _hitchEnabled.Value && _inGame ? $"끊김 {_hitch.Count}회" : null);
                _panel.Text($"FPS {_frames.CurrentFps:0}" +
                            (_frames.Frames > 0 ? $"  ·  평균 {_frames.AverageFps:0}  ·  1% 저점 {_frames.OnePercentLowFps():0}" : string.Empty));
            }

            if (!_profilerEnabled.Value)
            {
                return;
            }

            // --- per-mod frame cost
            if (_overlayMods.Value)
            {
                string when = _profiler.Measuring && _profiler.LastResultTime < 0f
                    ? $"측정 중 {_profiler.WindowElapsed:0}/{_profilerWindowSec.Value}초"
                    : _profiler.LastResultTime >= 0f
                        ? ((now - _profiler.LastResultTime) < 60f ? "방금" : $"{(now - _profiler.LastResultTime) / 60f:0}분 전") + $" · 프레임 {_profiler.LastFrameMs:0.0}ms"
                        : DescribeProfilerState();
                _panel.Header("모드별 부하 (프레임당 ms)", OverlayPanel.Yellow, when);
                float scale = 4f;
                int count = Math.Min(_overlayModCount.Value, _profiler.LastResult.Count);
                for (int i = 0; i < count; i++)
                {
                    scale = Math.Max(scale, _profiler.LastResult[i].Value);
                }

                for (int i = 0; i < count; i++)
                {
                    KeyValuePair<string, float> bar = _profiler.LastResult[i];
                    float share = _profiler.LastFrameMs > 0f ? bar.Value / _profiler.LastFrameMs : 0f;
                    bool suspect = _profiler.Suspect != null && _profiler.Suspect.StartsWith(bar.Key, StringComparison.Ordinal);
                    _panel.Bar(bar.Key, bar.Value / scale, $"{bar.Value:0.0}ms ({share * 100f:0}%)",
                        suspect ? OverlayPanel.Red : share >= 0.08f ? OverlayPanel.Yellow : OverlayPanel.Green);
                }

                if (_profiler.Suspect != null)
                {
                    _panel.Text("의심: " + _profiler.Suspect, OverlayPanel.Red);
                }
            }

            // --- per-mod memory creation
            if (_overlayMem.Value && _memSuspectEnabled.Value && _profiler.LastAlloc.Count > 0)
            {
                _panel.Header("모드별 메모리 생성 (MB/분)", OverlayPanel.Blue, $"합계 {_profiler.LastAllocTotal:0}MB/분");
                float scale = 50f;
                int count = Math.Min(_overlayModCount.Value, _profiler.LastAlloc.Count);
                for (int i = 0; i < count; i++)
                {
                    scale = Math.Max(scale, _profiler.LastAlloc[i].Value);
                }

                float total = Math.Max(0.01f, _profiler.LastAllocTotal);
                for (int i = 0; i < count; i++)
                {
                    KeyValuePair<string, float> bar = _profiler.LastAlloc[i];
                    bool suspect = _profiler.AllocSuspect != null && _profiler.AllocSuspect.StartsWith(bar.Key, StringComparison.Ordinal);
                    _panel.Bar(bar.Key, bar.Value / scale, $"{bar.Value:0}MB/분",
                        suspect ? OverlayPanel.Red : bar.Value / total >= 0.25f ? OverlayPanel.Yellow : OverlayPanel.Blue);
                }
            }

            // --- hitch causes
            if (_overlayHitch.Value && _hitchEnabled.Value && _inGame && _hitch.Count > 0)
            {
                _panel.Header("끊김 원인 (이번 레이드)", OverlayPanel.Purple,
                    $"{_hitch.Count}회 · {_hitchThresholdMs.Value}ms 이상" + (HitchModTrackingOn ? string.Empty : " · 모드 추적 꺼짐"));
                List<KeyValuePair<string, KeyValuePair<int, float>>> causes = _hitch.Causes();
                int most = 1;
                foreach (KeyValuePair<string, KeyValuePair<int, float>> cause in causes)
                {
                    most = Math.Max(most, cause.Value.Key);
                }

                for (int i = 0; i < Math.Min(_overlayModCount.Value, causes.Count); i++)
                {
                    KeyValuePair<string, KeyValuePair<int, float>> cause = causes[i];
                    bool suspect = _hitch.Suspect != null && _hitch.Suspect.StartsWith(cause.Key, StringComparison.Ordinal);
                    _panel.Bar(cause.Key, cause.Value.Key / (float)most, $"{cause.Value.Key}회 · 최대 {cause.Value.Value:0}ms",
                        suspect ? OverlayPanel.Red : cause.Key == HitchMonitor.CauseGame ? OverlayPanel.Gray : OverlayPanel.Purple);
                }

                if (_hitch.Suspect != null)
                {
                    _panel.Text("의심: " + _hitch.Suspect, OverlayPanel.Red);
                }
            }
        }

        private void ManualButtonsDrawer(ConfigEntryBase entry)
        {
            if (GUILayout.Button(_gc.Running ? "GC 정리 중..." : "GC 정리", GUILayout.ExpandWidth(true)) && !_gc.Running)
            {
                if (!_gc.Start("manual", true, false))
                {
                    Logger.LogWarning("Manual GC could not start");
                }
            }

            if (GUILayout.Button(_trimRunning != 0 ? "워킹셋 정리 중..." : "워킹셋 정리", GUILayout.ExpandWidth(true)))
            {
                StartTrim("manual");
            }

            if (GUILayout.Button(_assetPhase != AssetPhase.Idle ? "에셋 정리 중..." : "에셋 정리", GUILayout.ExpandWidth(true)))
            {
                StartAssetUnload("manual", _unloadGcFirst.Value, false);
            }

            if (GUILayout.Button("누수 추적 기록", GUILayout.ExpandWidth(true)))
            {
                RunLeakSnapshot("manual");
            }

            if (GUILayout.Button(_diagMode.Value ? "원인 추적 끄기" : "원인 추적 켜기", GUILayout.ExpandWidth(true)))
            {
                _diagMode.Value = !_diagMode.Value;
            }
        }

        private void StatusDrawer(ConfigEntryBase entry)
        {
            GUILayout.Label(_statusText, GUILayout.ExpandWidth(true));
        }

        internal void OnGUI()
        {
            if (!OverlayOn || Event.current.type != EventType.Repaint)
            {
                return;
            }

            _panel.Draw(6f, 6f);
        }
    }
}

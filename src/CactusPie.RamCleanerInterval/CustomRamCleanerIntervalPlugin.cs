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
    [BepInPlugin("com.cactuspie.ramcleanerinterval", "RAM 클리너 (RamCleanerInterval)", "2.4.0")]
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
        private string _overlayText = string.Empty;
        private GUIStyle _overlayStyle;
        private string _overlaySizeFor;
        private Vector2 _overlaySize;

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
            _hitchContext = () => $"players alive {_alive}, dead {_dead}, GC {GarbageCollector.GCMode}";

            BindSettings();
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
        }

        internal void Update()
        {
            float now = Time.realtimeSinceStartup;

            while (_mainThread.TryDequeue(out Action action))
            {
                action();
            }

            CheckHitch(now);

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
                FinishRaidReport();
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
            _hitch.Check(active, _hitchThresholdMs.Value, _hitchContext);
        }

        private void FinishRaidReport()
        {
            if (!_report.Active)
            {
                return;
            }

            _report.End(_hitch, out string logLine, out string notification);
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
            if (!_inGame || !_leakEnabled.Value || _leakNext < 0f || now < _leakNext)
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
            _alive = -1;
            _dead = -1;
            try
            {
                GameWorld world = _inGame ? Singleton<GameWorld>.Instance : null;
                if (world != null)
                {
                    _alive = world.AllAlivePlayersList.Count;
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
            if (_hitchEnabled.Value)
            {
                sb.Append("끊김(이번 레이드): ").Append(_hitch.Count).Append("회, 그중 이 모드 ").Append(_hitch.Ours)
                  .Append("회, 최대 ").Append(_hitch.MaxMs.ToString("0")).Append("ms · 마지막: ").Append(_hitch.LastText).Append('\n');
            }

            if (_warnEnabled.Value)
            {
                sb.Append("마지막 경고: ").Append(_warnings.LastText).Append('\n');
            }

            if (_reportEnabled.Value)
            {
                sb.Append("마지막 레이드 결산: ").Append(_lastReport).Append('\n');
            }

            sb.Append("누수 추적: ").Append(_leakEnabled.Value || _leak.Snapshots > 0 ? _leak.LastSummary : "꺼짐");
            _statusText = sb.ToString();

            if (_showOverlay.Value)
            {
                string vramText = vram >= 0 ? MemoryStats.Gb(vram) + "GB" : "?";
                _overlayText = $"RAM 클리너 | 힙 {MemoryStats.Gb(s.MonoUsed)}GB · 네이티브 {MemoryStats.Gb(s.Native)}GB · " +
                               $"VRAM {vramText} · 여유 {MemoryStats.Gb(s.SystemAvailable)}GB · GC {gcState}";
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
        }

        private void StatusDrawer(ConfigEntryBase entry)
        {
            GUILayout.Label(_statusText, GUILayout.ExpandWidth(true));
        }

        internal void OnGUI()
        {
            if (!_showOverlay.Value || _overlayText.Length == 0)
            {
                return;
            }

            if (_overlayStyle == null)
            {
                _overlayStyle = new GUIStyle(GUI.skin.box)
                {
                    fontSize = 13,
                    alignment = TextAnchor.MiddleLeft,
                    wordWrap = false,
                };
                _overlayStyle.normal.textColor = Color.white;
            }

            if (!ReferenceEquals(_overlaySizeFor, _overlayText))
            {
                _overlaySizeFor = _overlayText;
                _overlaySize = _overlayStyle.CalcSize(new GUIContent(_overlayText));
            }

            GUI.Box(new Rect(6f, 6f, _overlaySize.x + 8f, _overlaySize.y + 2f), _overlayText, _overlayStyle);
        }
    }
}

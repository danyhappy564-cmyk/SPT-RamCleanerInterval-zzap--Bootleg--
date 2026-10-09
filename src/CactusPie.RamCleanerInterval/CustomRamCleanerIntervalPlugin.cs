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
    [BepInPlugin("com.cactuspie.ramcleanerinterval", "RAM 클리너 (RamCleanerInterval)", "2.13.0")]
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
        private string _lastReport;

        private ModCostProfiler _profiler;
        private ModObjectCounter _objects;
        private FrameStats _frames;
        private PerformanceHistory _history;
        private ServerMonitor _server;
        private MemoryForecast _forecast;
        private HeavyItemTracker _heavy;
        private SessionReport _sessionReport;
        private long _privateBeforeRaid = -1;
        private float _restartEvalAt = -1f;
        private bool _runwayWarnedThisRaid;
        private float _nextReportSample = -1f;
        private float _nextReportSave = -1f; // the raid in progress is saved once a minute (survives Alt+F4 / crash)
        private int _reportFramesAt;
        private double _reportSecondsAt;
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
        private int _gcRunningFrame = -1;

        // After-raid cleanup in the menu: GC -> asset unload -> working set trim, so Windows gets the memory back now.
        private enum PostRaidPhase
        {
            None,
            Waiting,
            Gc,
            Assets,
            Trim,
        }

        private PostRaidPhase _postRaid = PostRaidPhase.None;
        private float _postRaidAt = -1f;
        private float _postRaidLogUntil = -1f;
        private float _postRaidNextLog = -1f;
        private long _postRaidFreeBefore;
        private readonly WaitForEndOfFrame _endOfFrame = new WaitForEndOfFrame();
        private readonly HashSet<string> _memNotifiedThisRaid = new HashSet<string>();
        private readonly HashSet<string> _memLoggedThisRaid = new HashSet<string>();

        // Managed / native memory when the raid started loading, to show what is still held after the raid.
        private long _monoBeforeRaid = -1;
        private long _nativeBeforeRaid = -1;
        private string _keptAfterRaid;
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

        private string _lastTrimResult;
        private string _lastAssetResult;

        private static string NoneYet => Loc.L("아직 없음", "none yet");
        private string _statusText = Loc.L("측정 중...", "measuring...");

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
            _server = new ServerMonitor(Logger);
            _forecast = new MemoryForecast();
            _heavy = new HeavyItemTracker(Logger);
            _sessionReport = new SessionReport(Logger, System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "RamCleaner"));
            _hitchContext = () => $"players alive {_alive}, dead {_dead}, GC {GarbageCollector.GCMode}" +
                                  (Time.realtimeSinceStartup - _lastSpawnTime < 1.5f ? ", a bot spawned within the last second" : string.Empty) +
                                  (_profiler.Measuring ? ", mod profiler measuring" : string.Empty);

            BindSettings();
            StartWebServer();
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

        /// <summary>Alt+F4 / closing the game during a raid: save what the raid recorded so far.</summary>
        internal void OnApplicationQuit()
        {
            if (_sessionReport != null && _sessionReportEnabled.Value && _inGame && _raidStartedAt >= 0f)
            {
                SaveUnfinishedRaid(Time.realtimeSinceStartup, true);
            }
        }

        private void SaveUnfinishedRaid(float now, bool quitting)
        {
            try
            {
                if (_heavyEnabled.Value)
                {
                    _sessionReport.SetHeavy(_heavy.TopMods(10), _heavy.TopBundles(15));
                }

                List<string> suspects = CurrentSuspects();
                _sessionReport.Checkpoint(r => FillSessionRaid(r, suspects, (now - _raidStartedAt) / 60.0), quitting);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[report] could not save the raid in progress: {ex.Message}");
            }
        }

        internal void OnDestroy()
        {
            GarbageCollector.GCModeChanged -= OnGcModeChanged;
            _web?.Stop();
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
                    _gc.Abort(_inGame, Loc.L("설정에서 꺼서 중단", "stopped: turned off in settings"));
                }
                else
                {
                    _gc.Tick(_gcSliceMs.Value, _inGame);
                }

                _gcRunningFrame = Time.frameCount;
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
            EvaluateServerAndForecast(now);
            TickWeb(now);
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

            EvaluatePostRaid(now);
            BuildTexts(now);
        }

        private void EvaluatePostRaid(float now)
        {
            if (_inGame)
            {
                return;
            }

            if (_postRaidLogUntil > 0f && now >= _postRaidNextLog && now <= _postRaidLogUntil)
            {
                _postRaidNextLog = now + 15f;
                Logger.LogInfo($"[mem after raid] {DescribeForLog(_snapshot)} | GC {_snapshot.GcMode}");
            }

            if (_postRaid != PostRaidPhase.Waiting || now < _postRaidAt)
            {
                return;
            }

            // By now the game has finished its own return-to-menu cleanup. In the menu a hitch costs nothing,
            // so do the full sequence: GC (incl. the blocking fallback), unused assets, then the working set.
            _postRaidFreeBefore = _snapshot.SystemAvailable;
            Logger.LogInfo($"After-raid cleanup start: system free {MemoryStats.Gb(_snapshot.SystemAvailable)} GB, " +
                           $"game working set {MemoryStats.Gb(_snapshot.WorkingSet)} GB");
            _postRaid = PostRaidPhase.Gc;
            if (!_gc.Start("after raid", true, false))
            {
                PostRaidAssets();
            }
        }

        /// <summary>
        /// After the raid's objects are gone and a full GC ran, the managed heap should be back near its pre-raid size.
        /// What is still there is held by something that outlives the raid (typically a static reference in a mod):
        /// it adds up raid after raid. 2026-10-02: +0.8 GB after a 55-minute SAIN sim.
        /// </summary>
        private void ReportKeptAfterRaid()
        {
            if (_monoBeforeRaid <= 0 || _gc.LastUsedAfter <= 0)
            {
                return;
            }

            long kept = _gc.LastUsedAfter - _monoBeforeRaid;
            string sign = kept >= 0 ? "+" : string.Empty;
            _keptAfterRaid = Loc.L($"{DateTime.Now:HH:mm} 관리 메모리 레이드 전보다 {sign}{MemoryStats.Gb(kept)}GB",
                                   $"{DateTime.Now:HH:mm} managed memory {sign}{MemoryStats.Gb(kept)} GB vs before the raid");
            Logger.LogInfo($"[after raid] managed heap after GC {MemoryStats.Gb(_gc.LastUsedAfter)} GB vs {MemoryStats.Gb(_monoBeforeRaid)} GB " +
                           $"before the raid ({(kept >= 0 ? "+" : "")}{MemoryStats.Gb(kept)} GB kept)");
            if (kept >= (long)(_keptAfterRaidSuspectMb.Value * 1024L * 1024L))
            {
                string warning = Loc.L($"레이드가 끝났는데도 관리 메모리 {MemoryStats.Gb(kept)}GB가 안 풀림 — 어떤 모드가 지난 레이드 데이터를 붙잡고 있음. ", $"{MemoryStats.Gb(kept)} GB of managed memory still not freed after the raid — some mod is holding on to the last raid's data. ") +
                                 Loc.L("레이드를 반복할수록 쌓이니 긴 세션이면 가끔 게임 재시작을 권장", "It piles up raid after raid, so restart the game now and then in long sessions");
                _keptAfterRaid += Loc.L(" (의심)", " (suspect)");
                Logger.LogWarning($"[mem suspect] {warning}");
                if (_memSuspectNotify.Value)
                {
                    Notify(warning, true);
                }
            }
        }

        private void PostRaidAssets()
        {
            _postRaid = PostRaidPhase.Assets;
            StartAssetUnload("after raid", false, false);
            if (_assetPhase == AssetPhase.Idle)
            {
                PostRaidTrim(); // an unload was already running: skip this step rather than stall
            }
        }

        private void PostRaidTrim()
        {
            _postRaid = PostRaidPhase.Trim;
            if (!StartTrim("after raid"))
            {
                _postRaid = PostRaidPhase.None;
            }
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
                _postRaid = PostRaidPhase.None;
                _postRaidLogUntil = -1f;
                _warnedNoIncremental = false;
                _monoBeforeRaid = MemoryStats.MonoUsed();
                _nativeBeforeRaid = _snapshot.Native;
                _privateBeforeRaid = _snapshot.PrivateBytes;
                _server.ResetRaid();
                _forecast.ResetRaid();
                _heavy.ResetRaid();
                _runwayWarnedThisRaid = false;
                _restartEvalAt = -1f;
                Logger.LogInfo($"Raid loading: GC mode {GarbageCollector.GCMode}, incremental={GarbageCollector.isIncremental}, " +
                               $"mono used {MemoryStats.Gb(MemoryStats.MonoUsed())} GB");
            }
            else
            {
                if (_assetPhase == AssetPhase.WaitingForGc)
                {
                    // Raid is being torn down; the game unloads assets itself on the way to the menu.
                    _assetPhase = AssetPhase.Idle;
                    _lastAssetResult = Loc.L($"{DateTime.Now:HH:mm:ss} 레이드가 끝나서 취소", $"{DateTime.Now:HH:mm:ss} cancelled: raid ended");
                }

                _gc.Abort(false, Loc.L("레이드가 끝나서 중단", "stopped: raid ended"));
                _combat.Unbind();
                Logger.LogInfo("Left raid");
                if (_postRaidCleanup.Value)
                {
                    _postRaid = PostRaidPhase.Waiting;
                    _postRaidAt = now + _postRaidDelaySec.Value;
                }

                _postRaidLogUntil = now + 180f;
                _postRaidNextLog = now;
                _profiler.StopWindow(_profilerSuspectMs.Value, _profilerSuspectShare.Value / 100f, _allocSuspectMbPerMin.Value);
                FinishRaidReport();
                FinishFpsHistory();
                FinishSessionReportRaid();
                _restartEvalAt = now + (_postRaidCleanup.Value ? _postRaidDelaySec.Value : 0) + 15f;
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
            _memLoggedThisRaid.Clear();
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
            _sessionReport.BeginRaid(_map, _server.PrivateBytes > 0 ? _server.PrivateBytes / MemoryStats.BytesPerGb : -1);
            _nextReportSample = now;
            _nextReportSave = now + 60f;
            _reportFramesAt = 0;
            _reportSecondsAt = 0;
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
            if (_postRaid == PostRaidPhase.Gc)
            {
                ReportKeptAfterRaid();
                PostRaidAssets();
            }

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
                _lastAssetResult = Loc.L($"{DateTime.Now:HH:mm:ss} GC 먼저 진행 중...", $"{DateTime.Now:HH:mm:ss} running GC first...");
                return;
            }

            BeginUnload();
        }

        private void BeginUnload()
        {
            _assetPhase = AssetPhase.Unloading;
            _lastAssetResult = Loc.L($"{DateTime.Now:HH:mm:ss} 에셋 내리는 중...", $"{DateTime.Now:HH:mm:ss} unloading assets...");
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
            if (_postRaid == PostRaidPhase.Assets)
            {
                PostRaidTrim();
            }

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
            string kind = _assetAuto ? Loc.L("자동", "auto") : _assetReason == "raid start" ? Loc.L("레이드 시작", "raid start") : Loc.L("수동", "manual");
            _lastAssetResult = $"{DateTime.Now:HH:mm:ss} ({kind}) " +
                               Loc.L($"네이티브 {MemoryStats.Gb(_assetBefore.Native)} → {MemoryStats.Gb(after.Native)} GB{vramText}, ", $"native {MemoryStats.Gb(_assetBefore.Native)} → {MemoryStats.Gb(after.Native)} GB{vramText}, ") +
                               Loc.L($"가장 긴 프레임 {_assetLongestFrame * 1000f:0}ms", $"longest frame {_assetLongestFrame * 1000f:0} ms");

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
        private bool StartTrim(string reason)
        {
            if (Interlocked.CompareExchange(ref _trimRunning, 1, 0) != 0)
            {
                return false;
            }

            _lastTrim = Time.realtimeSinceStartup;
            _lastTrimResult = Loc.L($"{DateTime.Now:HH:mm:ss} 진행 중... ({reason})", $"{DateTime.Now:HH:mm:ss} running... ({reason})");

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
                    if (_postRaid == PostRaidPhase.Trim && reason == "after raid")
                    {
                        _postRaid = PostRaidPhase.None;
                        MemorySnapshot now = MemoryStats.Sample();
                        Logger.LogInfo($"After-raid cleanup done: system free {MemoryStats.Gb(_postRaidFreeBefore)} -> {MemoryStats.Gb(now.SystemAvailable)} GB, " +
                                       $"game working set {MemoryStats.Gb(now.WorkingSet)} GB, private {MemoryStats.Gb(now.PrivateBytes)} GB");
                    }
                    _lastTrimResult = ok
                        ? Loc.L($"{DateTime.Now:HH:mm:ss} 워킹셋 {MemoryStats.Gb(before)} → {MemoryStats.Gb(after)} GB ({reason})", $"{DateTime.Now:HH:mm:ss} working set {MemoryStats.Gb(before)} → {MemoryStats.Gb(after)} GB ({reason})")
                        : Loc.L($"{DateTime.Now:HH:mm:ss} 실패 (윈도우 API 호출 불가)", $"{DateTime.Now:HH:mm:ss} failed (Windows API not available)");
                    Logger.LogInfo($"Working set trim ({reason}): ok={ok}, {MemoryStats.Gb(before)} -> {MemoryStats.Gb(after)} GB, " +
                                   $"background call {watch.Elapsed.TotalMilliseconds:0}ms");
                });
            });
            return true;
        }

        // ---------------------------------------------------------------- Hitch detector / raid report / warnings

        // ---------------------------------------------------------------- Server · forecast · session report · heavy items

        private void EvaluateServerAndForecast(float now)
        {
            // Install the request/bundle watchers once, out of raid (main menu), a few seconds after start.
            if (!_inGame && now > 10f)
            {
                if (_serverEnabled.Value)
                {
                    _server.Install();
                }

                if (_heavyEnabled.Value)
                {
                    _heavy.Install();
                }
            }

            if (_serverEnabled.Value)
            {
                _server.Sample(now);
            }

            _heavy.Active = _heavyEnabled.Value && _inGame && _raidStartedAt >= 0f && now - _raidStartedAt > 5f;
            _heavy.Drain();

            if (_inGame && _raidStartedAt >= 0f)
            {
                if (_forecastEnabled.Value)
                {
                    _forecast.Sample(now, _snapshot, CommitFloor(_snapshot), PhysFloor(_snapshot));
                    if (!_runwayWarnedThisRaid && !double.IsNaN(_forecast.MinutesLeft) && _forecast.MinutesLeft < _forecastWarnMinutes.Value)
                    {
                        _runwayWarnedThisRaid = true;
                        string line = _forecast.DescribeRunway(RecentPerDeathMb(), _snapshot.CommitAvailable, CommitFloor(_snapshot));
                        Logger.LogWarning($"[runway] {line}");
                        if (_forecastNotify.Value)
                        {
                            _report.AddWarning();
                            Notify(Loc.L($"메모리 여유 부족 예상 — {line}. 이번 레이드를 마무리하는 걸 권장합니다.", $"Memory running low — {line}. Consider wrapping up this raid."), true);
                        }
                    }
                }

                if (_sessionReportEnabled.Value && now >= _nextReportSample)
                {
                    _nextReportSample = now + 10f;
                    double seconds = _frames.TotalSeconds - _reportSecondsAt;
                    double fps = seconds > 0.5 ? (_frames.Frames - _reportFramesAt) / seconds : _frames.CurrentFps;
                    _reportFramesAt = _frames.Frames;
                    _reportSecondsAt = _frames.TotalSeconds;
                    _sessionReport.Sample((now - _raidStartedAt) / 60.0, _snapshot.PrivateBytes / MemoryStats.BytesPerGb,
                        _snapshot.SystemAvailable / MemoryStats.BytesPerGb, _server.PrivateBytes > 0 ? _server.PrivateBytes / MemoryStats.BytesPerGb : -1, fps);
                }

                if (_sessionReportEnabled.Value && now >= _nextReportSave)
                {
                    _nextReportSave = now + 60f;
                    SaveUnfinishedRaid(now, false);
                }
            }

            EvaluateRestart(now);
        }

        /// <summary>Same limit as the commit warning (09): the configured percentage of the limit, at least 2 GB.</summary>
        private long CommitFloor(MemorySnapshot s) =>
            Math.Max(2L * 1024 * 1024 * 1024, s.CommitLimit * _warnCommitPercent.Value / 100);

        /// <summary>Physical RAM below which Windows starts paging hard: 5% of RAM, at least 2 GB.</summary>
        private static long PhysFloor(MemorySnapshot s) => Math.Max(2L * 1024 * 1024 * 1024, s.SystemTotal / 20);

        /// <summary>Once per raid, after the post-raid cleanup has finished (or ~15 s after leaving if it is off).</summary>
        private void EvaluateRestart(float now)
        {
            if (_restartEvalAt < 0f || now < _restartEvalAt || _postRaid != PostRaidPhase.None)
            {
                return;
            }

            _restartEvalAt = -1f;
            string restart = null;
            if (_forecastEnabled.Value && _privateBeforeRaid > 0 && _report.PeakPrivate > 0)
            {
                string notify = _forecast.EvaluateRestart(_snapshot.PrivateBytes, _report.PeakPrivate - _privateBeforeRaid,
                    _snapshot.CommitAvailable, CommitFloor(_snapshot));
                restart = _forecast.RestartText;
                Logger.LogInfo($"[restart] {restart}");
                if (notify != null && _restartNotify.Value)
                {
                    Notify(Loc.L("RAM 클리너: ", "RAM Cleaner: ") + notify, true);
                }
            }

            if (_sessionReportEnabled.Value)
            {
                _sessionReport.AfterRaid(_keptAfterRaid, restart);
            }
        }

        private void FinishSessionReportRaid()
        {
            if (_heavyEnabled.Value && _heavy.Loads > 0)
            {
                Logger.LogInfo($"[heavy items] {_heavy.DescribeForLog(8)}");
            }

            if (!_sessionReportEnabled.Value)
            {
                return;
            }

            if (_heavyEnabled.Value)
            {
                _sessionReport.SetHeavy(_heavy.TopMods(10), _heavy.TopBundles(15));
            }

            List<string> suspects = CurrentSuspects();
            _sessionReport.EndRaid(r => FillSessionRaid(r, suspects, _report.LastMinutes));
        }

        private List<string> CurrentSuspects()
        {
            var suspects = new List<string>();
            if (_profiler.Suspect != null)
            {
                suspects.Add(Loc.L("프레임 의심: ", "frame suspect: ") + _profiler.Suspect);
            }

            if (_hitch.Suspect != null)
            {
                suspects.Add(Loc.L("끊김 의심: ", "stutter suspect: ") + _hitch.Suspect);
            }

            suspects.AddRange(_memSuspects);
            return suspects;
        }

        /// <summary>Copies this raid's numbers into the session report (at raid end, or live for the web page).</summary>
        private void FillSessionRaid(SessionReport.Raid r, List<string> suspects, double minutes)
        {
            r.Minutes = (float)minutes;
            r.AvgFps = _frames.AverageFps;
            r.LowFps = _frames.OnePercentLowFps();
            r.Deaths = _report.Deaths;
            r.PerDeathMb = _report.PerDeathMb;
            r.PeakGameGb = _report.PeakPrivate / MemoryStats.BytesPerGb;
            r.MinSystemFreeGb = _report.MinSystemAvailable >= 0 ? _report.MinSystemAvailable / MemoryStats.BytesPerGb : -1;
            r.Hitches = _hitch.Count;
            r.WorstHitchMs = _hitch.MaxMs;
            r.Causes = _hitch.Causes();
            r.ServerEndGb = _server.PrivateBytes > 0 ? _server.PrivateBytes / MemoryStats.BytesPerGb : -1;
            r.Bots = _server.DescribeBots();
            r.Waits = _server.DescribeWaits();
            r.LowestRunwayMin = _forecast.LowestMinutes;
            r.Suspects = suspects;
        }

        private void OpenSessionReport()
        {
            if (!_sessionReport.Written || !System.IO.File.Exists(_sessionReport.FilePath))
            {
                Notify(Loc.L("세션 보고서는 레이드가 한 판 끝나면 만들어집니다.", "The session report is written once a raid has finished."), false);
                return;
            }

            Application.OpenURL("file:///" + _sessionReport.FilePath.Replace('\\', '/'));
        }

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

            double serverWait = _server.WaitInFrame(previousFrame, out string serverPath);
            if (serverWait > 0)
            {
                _hitch.NoteServerWait(serverWait, serverPath);
            }

            // Skip the first seconds after the countdown: spawn waves and streaming make those frames noisy.
            bool active = _hitchEnabled.Value && _inGame && _raidStartedAt >= 0f && now - _raidStartedAt > 5f;
            _hitch.Check(active, _hitchThresholdMs.Value, _hitchContext, HitchModTrackingOn ? _profiler : null,
                Time.realtimeSinceStartup - _lastSpawnTime < 1.5f, _gcRunningFrame >= previousFrame);
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

                string start = Loc.L($"=== 원인 추적 시작 {DateTime.Now:HH:mm:ss} (화면 표시 + 모드별 상시 측정 + 끊김 원인 추적", $"=== Diagnostic mode started {DateTime.Now:HH:mm:ss} (overlay + per-mod measuring all the time + stutter causes") +
                               (_diagHeavy.Value ? Loc.L(" + 누수 추적·오브젝트 수", " + leak tracker · object counts") : string.Empty) + ") ===";
                _sessionLog?.WriteBlock(start);
                Logger.LogInfo("Diagnostic mode ON");
                Notify(Loc.L($"RAM 클리너: 원인 추적 시작 — {_diagHotkey.Value} 로 끄기", $"RAM Cleaner: diagnostic mode on — {_diagHotkey.Value} to turn off"), false);
                return;
            }

            string summary = BuildDiagSummary(now);
            _sessionLog?.WriteBlock(summary);
            Logger.LogInfo("Diagnostic mode OFF");
            _diagStartedAt = -1f;
            Notify(Loc.L("RAM 클리너: 원인 추적 종료 — 요약은 전용 로그(BepInEx\\RamCleaner)에 저장", "RAM Cleaner: diagnostic mode off — summary saved to the dedicated log (BepInEx\\RamCleaner)"), false);
        }

        private string BuildDiagSummary(float now)
        {
            var sb = new StringBuilder();
            float minutes = _diagStartedAt >= 0f ? (now - _diagStartedAt) / 60f : 0f;
            sb.Append(Loc.L($"=== 원인 추적 종료 {DateTime.Now:HH:mm:ss} ({minutes:0}분) — 요약 ===\n", $"=== Diagnostic mode ended {DateTime.Now:HH:mm:ss} ({minutes:0} min) — summary ===\n"));
            sb.Append(Loc.L($"FPS: 지금 {_frames.CurrentFps:0}, 레이드 평균 {_frames.AverageFps:0}, 1% 저점 {_frames.OnePercentLowFps():0}\n", $"FPS: now {_frames.CurrentFps:0}, raid average {_frames.AverageFps:0}, 1% low {_frames.OnePercentLowFps():0}\n"));
            if (_profiler.LastResult.Count > 0)
            {
                sb.Append(Loc.L("모드별 부하(프레임당 ms): ", "per-mod cost (ms per frame): "))
                  .Append(string.Join(", ", _profiler.LastResult.Take(8).Select(kv => $"{kv.Key} {kv.Value:0.00}")))
                  .Append(Loc.L($" / 프레임 {_profiler.LastFrameMs:0.0}ms\n", $" / frame {_profiler.LastFrameMs:0.0} ms\n"));
            }

            if (_profiler.LastAlloc.Count > 0)
            {
                sb.Append(Loc.L($"모드별 메모리 생성(MB/분, {_profiler.AllocMethod}): ", $"per-mod memory creation (MB/min, {_profiler.AllocMethod}): "))
                  .Append(string.Join(", ", _profiler.LastAlloc.Take(8).Select(kv => $"{kv.Key} {kv.Value:0}"))).Append('\n');
            }

            List<KeyValuePair<string, KeyValuePair<int, float>>> causes = _hitch.Causes();
            if (causes.Count > 0)
            {
                sb.Append(Loc.L($"끊김 원인({_hitch.Count}회): ", $"stutter causes ({_hitch.Count}): "))
                  .Append(string.Join(", ", causes.Select(c => Loc.L($"{c.Key} {c.Value.Key}회(최대 {c.Value.Value:0}ms)", $"{c.Key} {c.Value.Key}x (worst {c.Value.Value:0} ms)")))).Append('\n');
            }

            double slope = GcFloorSlopeMbPerMin();
            if (!double.IsNaN(slope))
            {
                sb.Append(Loc.L($"GC 뒤 남는 관리 메모리: 분당 {slope:+0;-0}MB\n", $"managed memory left after GC: {slope:+0;-0} MB/min\n"));
            }

            double perDeath = RecentPerDeathMb();
            if (perDeath >= 0)
            {
                sb.Append(Loc.L($"사망 1명당 메모리(최근): {perDeath:0}MB\n", $"memory per death (recent): {perDeath:0} MB\n"));
            }

            sb.Append(Loc.L("의심: ", "suspect: "));
            var suspects = new List<string>();
            if (_profiler.Suspect != null)
            {
                suspects.Add(Loc.L("프레임 — ", "frame — ") + _profiler.Suspect);
            }

            if (_hitch.Suspect != null)
            {
                suspects.Add(Loc.L("끊김 — ", "stutter — ") + _hitch.Suspect);
            }

            suspects.AddRange(_memSuspects);
            sb.Append(suspects.Count > 0 ? string.Join(" / ", suspects) : Loc.L("없음", "none"));
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
                Notify(Loc.L("끊김 의심: ", "stutter suspect: ") + _hitch.Suspect, true);
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
                           $"combat {(_combat.InventoryOpen ? "inventory" : Mathf.Min(_combat.SecondsSinceCombat, 9999f).ToString("0") + "s ago")}" +
                           (_serverEnabled.Value ? " | " + _server.DescribeForLog() : string.Empty) +
                           (_forecastEnabled.Value && !double.IsNaN(_forecast.MinutesLeft)
                               ? $" | runway {_forecast.MinutesLeft:0} min to {(_forecast.Limit == "RAM" ? "RAM floor" : "commit floor")} at -{_forecast.DropMbPerMin:0} MB/min"
                               : string.Empty));
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
                _memSuspects.Add(Loc.L("메모리 의심: ", "memory suspect: ") + _profiler.AllocSuspect);
            }

            double slope = GcFloorSlopeMbPerMin();
            if (!double.IsNaN(slope) && slope >= _retainedSuspectMbPerMin.Value)
            {
                string top = _profiler.LastAlloc.Count > 0 ? Loc.L($" — 메모리를 가장 많이 만드는 모드: {_profiler.LastAlloc[0].Key} ({_profiler.LastAlloc[0].Value:0}MB/분)", $" — mod creating the most memory: {_profiler.LastAlloc[0].Key} ({_profiler.LastAlloc[0].Value:0} MB/min)") : string.Empty;
                _memSuspects.Add(Loc.L($"관리 메모리 누수 의심: GC 뒤에도 분당 {slope:0}MB씩 남음{top}", $"managed memory leak suspect: {slope:0} MB/min stays after every GC{top}"));
            }

            double perDeath = RecentPerDeathMb();
            if (perDeath >= _perDeathSuspectMb.Value)
            {
                _memSuspects.Add(Loc.L($"봇 장비 메모리 과다: 사망 1명당 {perDeath:0}MB — 봇 스폰 모드(APBS 등)의 모드 아이템 종류를 줄여 보세요", $"too much bot gear memory: {perDeath:0} MB per death — try fewer modded items in the bot spawn mod (APBS etc.)"));
            }

            if (_objects.Suspect != null)
            {
                _memSuspects.Add(Loc.L("오브젝트 증가 의심: ", "object growth suspect: ") + _objects.Suspect);
            }

            // Log each suspect once per raid: same kind + same mod, numbers ignored. Suspects that flicker on and off
            // around a threshold were logged 118 times in one raid otherwise (2026-10-02 log); the overlay and F12
            // still show the live value.
            foreach (string line in _memSuspects)
            {
                string kind = line.Substring(0, Math.Min(6, line.Length));
                string identity = System.Text.RegularExpressions.Regex.Replace(
                    line.Split(new[] { " — " }, StringSplitOptions.None)[0], @"[0-9.]+", "#");
                if (!_memLoggedThisRaid.Add(identity))
                {
                    continue;
                }

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
                return korean ? Loc.L("사망 1명당 메모리: 아직 표본 부족 (사망 3명 이상부터)", "memory per death: not enough deaths yet (from 3)") : "per death n/a";
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
                recent = korean ? Loc.L($", 최근 {_dead - from.Value.Key}명 기준 {last10:0}MB", $", last {_dead - from.Value.Key} deaths {last10:0} MB") : $", last {_dead - from.Value.Key} deaths {last10:0} MB";
            }

            return korean
                ? Loc.L($"사망 1명당 메모리: 레이드 전체 {total:0}MB{recent} (사망 {deaths}명)", $"memory per death: {total:0} MB over the raid{recent} ({deaths} deaths)")
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
            string gcState = _gc.Running ? Loc.L($"정리 중 {_gc.Elapsed:0}초", $"cleaning {_gc.Elapsed:0} s") : MemoryStats.ModeName(s.GcMode);
            long vram = _vram.Dedicated;

            var sb = new StringBuilder(768);
            sb.Append(Loc.L("관리 메모리(Mono 힙): 사용 ", "managed memory (Mono heap): used ")).Append(MemoryStats.Gb(s.MonoUsed))
              .Append(Loc.L(" GB / 확보 ", " GB / reserved ")).Append(MemoryStats.Gb(s.MonoReserved)).Append(" GB\n");
            if (s.SystemTotal > 0)
            {
                sb.Append(Loc.L("네이티브 메모리(에셋·엔진·모드): ", "native memory (assets · engine · mods): ")).Append(MemoryStats.Gb(s.Native)).Append(" GB");
                if (_nativeBaseline >= 0 && _inGame)
                {
                    sb.Append(Loc.L(" (마지막 기준점 대비 +", " (since the last baseline +")).Append(MemoryStats.Gb(Math.Max(0, s.Native - _nativeBaseline))).Append(" GB)");
                }

                sb.Append('\n');
                sb.Append(Loc.L("게임 전체: 실제 RAM(워킹셋) ", "whole game: RAM in use (working set) ")).Append(MemoryStats.Gb(s.WorkingSet))
                  .Append(Loc.L(" GB / 커밋 ", " GB / commit ")).Append(MemoryStats.Gb(s.PrivateBytes)).Append(" GB\n");
                sb.Append(Loc.L("시스템 여유 RAM: ", "system free RAM: ")).Append(MemoryStats.Gb(s.SystemAvailable)).Append(" / ")
                  .Append(MemoryStats.Gb(s.SystemTotal)).Append(" GB (").Append(s.SystemAvailablePercent.ToString("0"))
                  .Append("%)\n");
            }

            sb.Append(Loc.L("VRAM(이 게임): ", "VRAM (this game): "));
            if (vram >= 0)
            {
                sb.Append(Loc.L("전용 ", "dedicated ")).Append(MemoryStats.Gb(vram)).Append(Loc.L(" GB / 공유 ", " GB / shared ")).Append(MemoryStats.Gb(_vram.Shared))
                  .Append(Loc.L(" GB (그래픽카드 ", " GB (graphics card ")).Append((SystemInfo.graphicsMemorySize / 1024f).ToString("0.0")).Append(" GB)\n");
            }
            else
            {
                sb.Append(_vram.Failed ? Loc.L("측정 불가 (윈도우 성능 카운터 없음)\n", "can't measure (no Windows performance counter)\n") : Loc.L("측정 중...\n", "measuring...\n"));
            }

            if (_inGame && _raidStartedAt >= 0f)
            {
                sb.Append(DescribePerDeath(true)).Append('\n');
            }

            if (_serverEnabled.Value)
            {
                sb.Append(Loc.L("SPT 서버: ", "SPT server: ")).Append(_server.Found
                    ? Loc.L($"메모리 {MemoryStats.Gb(_server.PrivateBytes)} GB (커밋) / {MemoryStats.Gb(_server.WorkingSet)} GB (실제 RAM) — {_server.ProcessName}", $"memory {MemoryStats.Gb(_server.PrivateBytes)} GB (commit) / {MemoryStats.Gb(_server.WorkingSet)} GB (RAM in use) — {_server.ProcessName}")
                    : Loc.L("프로세스를 찾는 중 (30초마다)", "looking for the process (every 30 s)")).Append(Loc.L(" · 측정 장치: ", " · probe: ")).Append(_server.PatchStatus).Append('\n');
                string bots = _server.DescribeBots();
                if (bots != null)
                {
                    sb.Append("  ").Append(bots).Append('\n');
                }

                string waits = _server.DescribeWaits();
                if (waits != null)
                {
                    sb.Append("  ").Append(waits).Append('\n');
                }
            }

            if (_forecastEnabled.Value)
            {
                if (_inGame)
                {
                    sb.Append(_forecast.DescribeRunway(RecentPerDeathMb(), s.CommitAvailable, CommitFloor(s)) ?? Loc.L("여유 예상: 계산 중 (레이드 3분 뒤부터)", "time left: calculating (from 3 min into the raid)")).Append('\n');
                }

                sb.Append(Loc.L("재시작 판단: ", "restart advice: ")).Append(_forecast.RestartText).Append('\n');
            }

            if (_heavyEnabled.Value)
            {
                sb.Append(Loc.L("[실험] 무거운 아이템: ", "[experimental] heavy items: ")).Append(_heavy.Status).Append(Loc.L(", 측정 ", ", measured ")).Append(_heavy.Loads).Append(Loc.L("회\n", "x\n"));
                List<HeavyItemTracker.Stat> heavy = _heavy.TopMods(5);
                if (heavy.Count > 0)
                {
                    sb.Append("  ").Append(string.Join(", ", heavy.Select(x => Loc.L($"{x.Mod} {x.Mb:0}MB({x.Count}개)", $"{x.Mod} {x.Mb:0} MB ({x.Count})")))).Append('\n');
                }
            }

            if (_sessionReportEnabled.Value)
            {
                sb.Append(Loc.L("세션 보고서: ", "session report: ")).Append(_sessionReport.Written ? _sessionReport.FilePath : Loc.L("레이드 1분 뒤부터 1분마다 저장", "saved every minute from 1 min into a raid")).Append('\n');
            }

            sb.Append(Loc.L("웹 페이지: ", "web page: ")).Append(_web.Status);
            if (_launcherPageUrl != null)
            {
                sb.Append(Loc.L(" · 런처 모드 페이지: ", " · launcher mod page: ")).Append(_launcherPageUrl);
            }

            sb.Append('\n');

            sb.Append(Loc.L("GC 상태: ", "GC: ")).Append(gcState)
              .Append(Loc.L(" · 나눠서 하는 GC(증분): ", " · incremental GC: ")).Append(GarbageCollector.isIncremental ? Loc.L("지원", "supported") : Loc.L("미지원", "not supported")).Append('\n');

            if (_inGame)
            {
                sb.Append(Loc.L("전투 상태: ", "combat: "));
                if (_combat.InventoryOpen)
                {
                    sb.Append(Loc.L("인벤토리 열림 (정리하기 좋은 순간)", "inventory open (a good moment to clean up)"));
                }
                else if (_combat.IsQuiet(_quietSec.Value))
                {
                    sb.Append(Loc.L("조용함", "quiet"));
                }
                else
                {
                    sb.Append(Loc.L("전투 중 (마지막 활동 ", "fighting (last action ")).Append(_combat.SecondsSinceCombat.ToString("0")).Append(Loc.L("초 전)", " s ago)"));
                }

                sb.Append('\n');
            }

            AppendPending(sb, now);

            if (_gcBaseline >= 0 && !_gc.Running)
            {
                sb.Append(Loc.L("다음 자동 GC: 사용량 ", "next automatic GC: at "))
                  .Append(MemoryStats.Gb(_gcBaseline + (long)(_gcGrowthGb.Value * MemoryStats.BytesPerGb))).Append(Loc.L(" GB 도달 시\n", " GB used\n"));
            }

            if (_autoUnloadStopped || s_autoUnloadUselessThisSession)
            {
                sb.Append(Loc.L("레이드 중 자동 에셋 정리: 게임 끌 때까지 중단됨 (정리해도 거의 안 줄어서 — 모드 누수 의심, '누수 추적'을 켜 보세요)\n", "automatic asset cleanup in raid: paused until the game closes (it freed almost nothing — possible mod leak, try the 'leak tracker')\n"));
            }

            sb.Append(Loc.L("마지막 GC 정리: ", "last GC: ")).Append(_gc.LastResult).Append('\n');
            sb.Append(Loc.L("마지막 워킹셋 정리: ", "last working set trim: ")).Append(_lastTrimResult ?? NoneYet).Append('\n');
            sb.Append(Loc.L("마지막 에셋 정리: ", "last asset cleanup: ")).Append(_lastAssetResult ?? NoneYet).Append('\n');
            sb.Append(Loc.L("FPS: 지금 ", "FPS: now ")).Append(_frames.CurrentFps.ToString("0"));
            if (_frames.Frames > 0)
            {
                sb.Append(Loc.L(" · 레이드 평균 ", " · raid average ")).Append(_frames.AverageFps.ToString("0"))
                  .Append(Loc.L(" · 1% 저점 ", " · 1% low ")).Append(_frames.OnePercentLowFps().ToString("0"));
            }

            sb.Append('\n');
            if (_profilerEnabled.Value)
            {
                sb.Append(Loc.L("모드별 부하: ", "per-mod cost: ")).Append(DescribeProfilerState()).Append('\n');
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
                sb.Append(Loc.L("모드별 메모리 생성(MB/분, ", "per-mod memory creation (MB/min, ")).Append(_profiler.AllocMethod).Append("): ");
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
                sb.Append(Loc.L("GC 뒤에도 남는 관리 메모리: 분당 ", "managed memory left after GC: ")).Append(floorSlope.ToString("+0;-0")).Append("MB (").Append(_gcFloors.Count).Append(Loc.L("회 기준)\n", " GCs)\n"));
            }

            foreach (string suspectLine in _memSuspects)
            {
                sb.Append("-> ").Append(suspectLine).Append('\n');
            }

            if (ObjectsOn)
            {
                sb.Append(Loc.L("모드별 오브젝트: ", "per-mod objects: ")).Append(_objects.LastSummary).Append('\n');
            }

            if (_fpsHistoryEnabled.Value)
            {
                sb.Append(Loc.L("이전 레이드 비교: ", "vs previous raid: ")).Append(_history.LastComparison).Append('\n');
            }

            if (_hitchEnabled.Value)
            {
                sb.Append(Loc.L("끊김(이번 레이드): ", "stutters (this raid): ")).Append(_hitch.Count).Append(Loc.L("회, 그중 이 모드 ", ", by this mod ")).Append(_hitch.Ours)
                  .Append(Loc.L("회, 최대 ", ", worst ")).Append(_hitch.MaxMs.ToString("0")).Append(Loc.L("ms · 마지막: ", " ms · last: ")).Append(_hitch.LastText).Append('\n');
                List<KeyValuePair<string, KeyValuePair<int, float>>> causes = _hitch.Causes();
                if (causes.Count > 0)
                {
                    sb.Append(Loc.L("  끊김 원인: ", "  stutter causes: "));
                    for (int i = 0; i < Math.Min(6, causes.Count); i++)
                    {
                        if (i > 0)
                        {
                            sb.Append(", ");
                        }

                        sb.Append(causes[i].Key).Append(' ').Append(causes[i].Value.Key).Append(Loc.L("회(최대 ", "x (worst "))
                          .Append(causes[i].Value.Value.ToString("0")).Append("ms)");
                    }

                    sb.Append('\n');
                }

                if (_hitch.Suspect != null)
                {
                    sb.Append(Loc.L("-> 끊김 의심: ", "-> stutter suspect: ")).Append(_hitch.Suspect).Append('\n');
                }
            }

            if (_warnEnabled.Value)
            {
                sb.Append(Loc.L("마지막 경고: ", "last warning: ")).Append(_warnings.LastText).Append('\n');
            }

            if (_reportEnabled.Value)
            {
                sb.Append(Loc.L("마지막 레이드 결산: ", "last raid report: ")).Append(_lastReport ?? NoneYet).Append('\n');
            }

            sb.Append(Loc.L("레이드 후 남은 메모리: ", "memory kept after the raid: ")).Append(_keptAfterRaid ?? NoneYet).Append('\n');

            sb.Append(Loc.L("누수 추적: ", "leak tracker: ")).Append(LeakOn || _leak.Snapshots > 0 ? _leak.LastSummary : Loc.L("꺼짐", "off")).Append('\n');
            sb.Append(Loc.L("원인 추적 모드: ", "diagnostic mode: ")).Append(_diagMode.Value ? Loc.L($"켜짐 ({(Time.realtimeSinceStartup - Math.Max(0f, _diagStartedAt)) / 60f:0}분째)", $"on ({(Time.realtimeSinceStartup - Math.Max(0f, _diagStartedAt)) / 60f:0} min)") : Loc.L("꺼짐", "off"))
              .Append(Loc.L($" — 단축키 {_diagHotkey.Value}\n", $" — hotkey {_diagHotkey.Value}\n"));
            sb.Append(Loc.L("전용 로그: ", "dedicated log: ")).Append(_sessionLog?.FilePath ?? Loc.L("만들 수 없음", "can't be created"));
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
                sb.Append(Loc.L("대기 중: GC (전투가 끝나길 기다리는 중, ", "waiting: GC (until the fighting stops, ")).Append((now - _gcPendingSince).ToString("0")).Append(Loc.L("초)\n", " s)\n"));
            }

            if (_unloadPendingSince >= 0f)
            {
                sb.Append(Loc.L("대기 중: 에셋 정리 (조용한 순간 또는 인벤토리 열 때, ", "waiting: asset cleanup (quiet moment or inventory, ")).Append((now - _unloadPendingSince).ToString("0")).Append(Loc.L("초)\n", " s)\n"));
            }

            if (_trimPendingSince >= 0f)
            {
                sb.Append(Loc.L("대기 중: 워킹셋 정리 (", "waiting: working set trim (")).Append((now - _trimPendingSince).ToString("0")).Append(Loc.L("초)\n", " s)\n"));
            }
        }

        private string DescribeProfilerState()
        {
            switch (_profiler.Status)
            {
                case ModCostProfiler.State.NotInstalled:
                    return Loc.L("메인 메뉴에서 측정 장치 설치 대기", "probe installs in the main menu");
                case ModCostProfiler.State.Installing:
                    return Loc.L("측정 장치 설치 중...", "installing the probe...");
            }

            if (_profiler.Measuring)
            {
                return Loc.L($"측정 중 {_profiler.WindowElapsed:0}/{_profilerWindowSec.Value}초", $"measuring {_profiler.WindowElapsed:0}/{_profilerWindowSec.Value} s");
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
                _panel.Header(Loc.L($"● 원인 추적 중 — {_diagHotkey.Value} 로 끄기", $"● Diagnostic mode — {_diagHotkey.Value} to turn off"), OverlayPanel.Yellow, Loc.L($"{minutes:0}분째", $"{minutes:0} min"));
            }

            // --- memory
            _panel.Header(Loc.L("메모리", "Memory"), OverlayPanel.Blue, $"GC {gcState}");
            string vramText = vram >= 0 ? MemoryStats.Gb(vram) : "?";
            _panel.Text(Loc.L($"힙 {MemoryStats.Gb(s.MonoUsed)} · 네이티브 {MemoryStats.Gb(s.Native)} · VRAM {vramText} · 시스템 여유 {MemoryStats.Gb(s.SystemAvailable)} (GB)", $"heap {MemoryStats.Gb(s.MonoUsed)} · native {MemoryStats.Gb(s.Native)} · VRAM {vramText} · system free {MemoryStats.Gb(s.SystemAvailable)} (GB)"));
            if (_inGame)
            {
                double slope = GcFloorSlopeMbPerMin();
                double perDeath = RecentPerDeathMb();
                string extra = (perDeath >= 0 ? Loc.L($"사망당 {perDeath:0}MB", $"per death {perDeath:0} MB") : Loc.L("사망당 -", "per death -")) +
                               (!double.IsNaN(slope) ? Loc.L($" · GC 뒤 남는 양 {slope:+0;-0}MB/분", $" · left after GC {slope:+0;-0} MB/min") : string.Empty);
                _panel.Text(extra, OverlayPanel.Dim);
            }

            if (_forecastEnabled.Value && _overlayForecast.Value)
            {
                if (_inGame)
                {
                    string runway = _forecast.DescribeRunway(RecentPerDeathMb(), s.CommitAvailable, CommitFloor(s));
                    if (runway != null)
                    {
                        bool low = !double.IsNaN(_forecast.MinutesLeft) && _forecast.MinutesLeft < _forecastWarnMinutes.Value;
                        _panel.Text(runway, low ? OverlayPanel.Red : OverlayPanel.Dim);
                    }
                }
                else
                {
                    _panel.Text(Loc.L("재시작 판단: ", "restart advice: ") + _forecast.RestartText, _forecast.RaidsLeft >= 0 && _forecast.RaidsLeft <= 1 ? OverlayPanel.Red : OverlayPanel.Dim);
                }
            }

            for (int i = 0; i < _memSuspects.Count; i++)
            {
                _panel.Text(_memSuspects[i], OverlayPanel.Red);
            }

            // --- frames
            if (_overlayFps.Value)
            {
                _panel.Header(Loc.L("프레임", "Frames"), OverlayPanel.Green, _hitchEnabled.Value && _inGame ? Loc.L($"끊김 {_hitch.Count}회", $"{_hitch.Count} stutters") : null);
                _panel.Text($"FPS {_frames.CurrentFps:0}" +
                            (_frames.Frames > 0 ? Loc.L($"  ·  평균 {_frames.AverageFps:0}  ·  1% 저점 {_frames.OnePercentLowFps():0}", $"  ·  avg {_frames.AverageFps:0}  ·  1% low {_frames.OnePercentLowFps():0}") : string.Empty));
            }

            // --- SPT server
            if (_serverEnabled.Value && _overlayServer.Value)
            {
                _panel.Header(Loc.L("SPT 서버", "SPT server"), OverlayPanel.Gray, _server.Found ? Loc.L($"메모리 {MemoryStats.Gb(_server.PrivateBytes)} GB", $"memory {MemoryStats.Gb(_server.PrivateBytes)} GB") : Loc.L("프로세스 찾는 중", "looking for the process"));
                string bots = _server.DescribeBots();
                if (bots != null)
                {
                    _panel.Text(bots, _server.BotMaxMs >= 3000 ? OverlayPanel.Yellow : OverlayPanel.Dim);
                }

                string waits = _server.DescribeWaits();
                if (waits != null)
                {
                    _panel.Text(waits, _server.WaitMaxMs >= 100 ? OverlayPanel.Yellow : OverlayPanel.Dim);
                }

                if (bots == null && waits == null)
                {
                    _panel.Text(_inGame ? Loc.L("이번 레이드 서버 요청 기록 없음", "no server requests recorded this raid") : Loc.L("레이드 중 봇 생성 응답·서버 대기를 기록", "records bot generation and server waits in raid"), OverlayPanel.Dim);
                }
            }

            // --- experimental heavy items
            if (_heavyEnabled.Value && _overlayHeavy.Value)
            {
                List<HeavyItemTracker.Stat> heavy = _heavy.TopMods(_overlayModCount.Value);
                _panel.Header(Loc.L("[실험] 처음 로드 메모리 (모드별)", "[Experimental] first-load memory (per mod)"), OverlayPanel.Purple, Loc.L($"측정 {_heavy.Loads}회", $"{_heavy.Loads} measured"));
                if (heavy.Count == 0)
                {
                    _panel.Text(_heavy.Status, OverlayPanel.Dim);
                }
                else
                {
                    double most = Math.Max(1.0, heavy.Max(x => x.Mb));
                    foreach (HeavyItemTracker.Stat item in heavy)
                    {
                        _panel.Bar(item.Mod, (float)(item.Mb / most), Loc.L($"{item.Mb:0}MB · {item.Count}개", $"{item.Mb:0} MB · {item.Count}") + (item.Overlapped > 0 ? Loc.L($" (겹침 {item.Overlapped})", $" ({item.Overlapped} overlapped)") : string.Empty),
                            OverlayPanel.Purple);
                    }
                }
            }

            if (!_profilerEnabled.Value)
            {
                return;
            }

            // --- per-mod frame cost
            if (_overlayMods.Value)
            {
                string when = _profiler.Measuring && _profiler.LastResultTime < 0f
                    ? Loc.L($"측정 중 {_profiler.WindowElapsed:0}/{_profilerWindowSec.Value}초", $"measuring {_profiler.WindowElapsed:0}/{_profilerWindowSec.Value} s")
                    : _profiler.LastResultTime >= 0f
                        ? ((now - _profiler.LastResultTime) < 60f ? Loc.L("방금", "just now") : Loc.L($"{(now - _profiler.LastResultTime) / 60f:0}분 전", $"{(now - _profiler.LastResultTime) / 60f:0} min ago")) + Loc.L($" · 프레임 {_profiler.LastFrameMs:0.0}ms", $" · frame {_profiler.LastFrameMs:0.0} ms")
                        : DescribeProfilerState();
                _panel.Header(Loc.L("모드별 부하 (프레임당 ms)", "Per-mod cost (ms per frame)"), OverlayPanel.Yellow, when);
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
                    _panel.Text(Loc.L("의심: ", "suspect: ") + _profiler.Suspect, OverlayPanel.Red);
                }
            }

            // --- per-mod memory creation
            if (_overlayMem.Value && _memSuspectEnabled.Value && _profiler.LastAlloc.Count > 0)
            {
                _panel.Header(Loc.L("모드별 메모리 생성 (MB/분)", "Per-mod memory creation (MB/min)"), OverlayPanel.Blue, Loc.L($"합계 {_profiler.LastAllocTotal:0}MB/분", $"total {_profiler.LastAllocTotal:0} MB/min"));
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
                    _panel.Bar(bar.Key, bar.Value / scale, Loc.L($"{bar.Value:0}MB/분", $"{bar.Value:0} MB/min"),
                        suspect ? OverlayPanel.Red : bar.Value / total >= 0.25f ? OverlayPanel.Yellow : OverlayPanel.Blue);
                }
            }

            // --- hitch causes
            if (_overlayHitch.Value && _hitchEnabled.Value && _inGame && _hitch.Count > 0)
            {
                _panel.Header(Loc.L("끊김 원인 (이번 레이드)", "Stutter causes (this raid)"), OverlayPanel.Purple,
                    Loc.L($"{_hitch.Count}회 · {_hitchThresholdMs.Value}ms 이상", $"{_hitch.Count} · over {_hitchThresholdMs.Value} ms") + (HitchModTrackingOn ? string.Empty : Loc.L(" · 모드 추적 꺼짐", " · mod tracking off")));
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
                    _panel.Bar(cause.Key, cause.Value.Key / (float)most, Loc.L($"{cause.Value.Key}회 · 최대 {cause.Value.Value:0}ms", $"{cause.Value.Key}x · worst {cause.Value.Value:0} ms"),
                        suspect ? OverlayPanel.Red : cause.Key == HitchMonitor.CauseGame ? OverlayPanel.Gray : OverlayPanel.Purple);
                }

                if (_hitch.Suspect != null)
                {
                    _panel.Text(Loc.L("의심: ", "suspect: ") + _hitch.Suspect, OverlayPanel.Red);
                }
            }
        }

        private void ManualButtonsDrawer(ConfigEntryBase entry)
        {
            if (GUILayout.Button(_gc.Running ? Loc.L("GC 정리 중...", "GC running...") : Loc.L("GC 정리", "Run GC"), GUILayout.ExpandWidth(true)) && !_gc.Running)
            {
                if (!_gc.Start("manual", true, false))
                {
                    Logger.LogWarning("Manual GC could not start");
                }
            }

            if (GUILayout.Button(_trimRunning != 0 ? Loc.L("워킹셋 정리 중...", "Trimming working set...") : Loc.L("워킹셋 정리", "Trim working set"), GUILayout.ExpandWidth(true)))
            {
                StartTrim("manual");
            }

            if (GUILayout.Button(_assetPhase != AssetPhase.Idle ? Loc.L("에셋 정리 중...", "Unloading assets...") : Loc.L("에셋 정리", "Unload assets"), GUILayout.ExpandWidth(true)))
            {
                StartAssetUnload("manual", _unloadGcFirst.Value, false);
            }

            if (GUILayout.Button(Loc.L("누수 추적 기록", "Take a leak snapshot"), GUILayout.ExpandWidth(true)))
            {
                RunLeakSnapshot("manual");
            }

            if (GUILayout.Button(_diagMode.Value ? Loc.L("원인 추적 끄기", "Turn diagnostic mode off") : Loc.L("원인 추적 켜기", "Turn diagnostic mode on"), GUILayout.ExpandWidth(true)))
            {
                _diagMode.Value = !_diagMode.Value;
            }

            if (GUILayout.Button(Loc.L("웹 페이지 열기 (실시간·보고서·설정)", "Open web page (live · report · settings)"), GUILayout.ExpandWidth(true)))
            {
                OpenWebPage();
            }

            if (GUILayout.Button(Loc.L("세션 보고서 파일 열기", "Open session report file"), GUILayout.ExpandWidth(true)))
            {
                OpenSessionReport();
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

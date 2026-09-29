using System;
using System.Diagnostics;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Scripting;

namespace CactusPie.RamCleanerInterval
{
    [BepInPlugin("com.cactuspie.ramcleanerinterval", "RAM 클리너 (RamCleanerInterval)", "2.0.0")]
    public class CustomRamCleanerIntervalPlugin : BaseUnityPlugin
    {
        // Config keys stay English so the .cfg file is stable; everything the player sees in F12
        // (category, name, description) is Korean through ConfigurationManagerAttributes.
        private const string GcSection = "1. Auto GC";
        private const string TrimSection = "2. Working set";
        private const string GeneralSection = "3. General";

        private const string GcCategory = "1. 자동 메모리 정리 (GC) — 추천";
        private const string TrimCategory = "2. 워킹셋 정리 (원본 RAM 클리너 방식)";
        private const string GeneralCategory = "3. 공통 · 수동 실행 · 상태";

        private GcRunner _gc;

        private ConfigEntry<bool> _gcEnabled;
        private ConfigEntry<float> _gcGrowthGb;
        private ConfigEntry<float> _gcSliceMs;
        private ConfigEntry<int> _gcCooldownSec;
        private ConfigEntry<bool> _gcAllowBlocking;

        private ConfigEntry<bool> _trimEnabled;
        private ConfigEntry<bool> _trimOnlyLowRam;
        private ConfigEntry<int> _trimLowRamPercent;
        private ConfigEntry<int> _trimIntervalSec;
        private ConfigEntry<bool> _trimAfterGc;

        private ConfigEntry<bool> _onlyInRaid;
        private ConfigEntry<bool> _showOverlay;
        private ConfigEntry<int> _logIntervalSec;

        private MemorySnapshot _snapshot;
        private float _nextSample;
        private float _nextLog;
        private float _lastTrim;
        private bool _inGame;
        private long _gcBaseline = -1;
        private bool _warnedNoIncremental;

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

            BindGcSettings();
            BindTrimSettings();
            BindGeneralSettings();

            GarbageCollector.GCModeChanged += OnGcModeChanged;

            _lastTrim = Time.realtimeSinceStartup;
            Logger.LogInfo($"Loaded. incremental GC supported={GarbageCollector.isIncremental}, " +
                           $"slice default {GarbageCollector.incrementalTimeSliceNanoseconds / 1000000f:0.0}ms, " +
                           $"system RAM {SystemInfo.systemMemorySize} MB, GC mode {GarbageCollector.GCMode}");
        }

        internal void OnDestroy()
        {
            GarbageCollector.GCModeChanged -= OnGcModeChanged;
        }

        private void BindGcSettings()
        {
            _gcEnabled = Bind(GcSection, GcCategory, "Enabled", "자동 GC 정리 켜기", true,
                "레이드 중에는 게임이 GC(안 쓰는 메모리를 치우는 청소부)를 아예 꺼둡니다(RAM 12GB 이상 PC). " +
                "그래서 레이드가 길어지고 봇이 많을수록 메모리가 계속 늘어납니다. " +
                "이 기능은 메모리가 일정량 늘 때마다 GC를 잠깐 켜서, 여러 프레임에 조금씩 나눠 치웁니다.",
                null, 10);

            _gcGrowthGb = Bind(GcSection, GcCategory, "Growth trigger (GB)", "정리 시작 기준: 증가량 (GB)", 1.5f,
                "마지막 정리(또는 레이드 시작) 이후 관리 메모리가 이만큼 늘면 정리를 시작합니다. " +
                "낮을수록 자주·짧게, 높을수록 드물게·길게 정리합니다.",
                new AcceptableValueRange<float>(0.25f, 16f), 9);

            _gcSliceMs = Bind(GcSection, GcCategory, "Work per frame (ms)", "프레임당 작업 시간 (ms)", 2f,
                "한 프레임에 GC 작업을 최대 몇 ms까지 할지 정합니다. 낮을수록 끊김이 적은 대신 정리가 오래 걸립니다. " +
                "참고: 60fps는 한 프레임이 약 16ms, 144fps는 약 7ms입니다.",
                new AcceptableValueRange<float>(0.5f, 10f), 8);

            _gcCooldownSec = Bind(GcSection, GcCategory, "Minimum gap (s)", "자동 정리 최소 간격 (초)", 60,
                "정리가 끝난 뒤 다음 자동 정리까지 최소 몇 초를 기다릴지 정합니다.",
                new AcceptableValueRange<int>(10, 1800), 7);

            _gcAllowBlocking = Bind(GcSection, GcCategory, "Allow blocking fallback", "증분 GC 미지원 시 전체 GC 허용", false,
                "게임이 '나눠서 하는 GC(증분 GC)'를 지원하지 않을 때만 의미가 있습니다. " +
                "켜면 그 경우 한 번에 전체 GC를 해서, 메모리 양에 따라 1초 이상 멈출 수 있습니다. " +
                "BepInEx 로그의 'incremental GC supported=True'면 신경 쓰지 않아도 됩니다.",
                null, 6);
        }

        private void BindTrimSettings()
        {
            _trimEnabled = Bind(TrimSection, TrimCategory, "Enabled", "워킹셋 정리 켜기", true,
                "게임이 쓰던 메모리를 RAM 밖(대기 메모리/페이지 파일)으로 강제로 내보냅니다. " +
                "메모리를 실제로 비우는 게 아니라 옮기는 것이라, 게임이 다시 쓸 때 읽어오느라 직후에 끊김이 생깁니다 " +
                "(원본 모드에서 끊김이 많았던 이유). 그래서 기본값은 'RAM이 부족할 때만'입니다.",
                null, 10);

            _trimOnlyLowRam = Bind(TrimSection, TrimCategory, "Only when RAM is low", "시스템 RAM 부족할 때만", true,
                "켜면 윈도우 전체의 여유 RAM이 아래 기준보다 적을 때만 정리합니다 (추천). " +
                "끄면 원본 모드처럼 정해진 간격마다 무조건 정리합니다.",
                null, 9);

            _trimLowRamPercent = Bind(TrimSection, TrimCategory, "Low RAM threshold (%)", "여유 RAM 기준 (%)", 10,
                "시스템 여유 RAM이 전체의 몇 % 미만이면 '부족'으로 볼지 정합니다. 64GB에서 10%는 약 6.4GB입니다.",
                new AcceptableValueRange<int>(3, 50), 8);

            _trimIntervalSec = Bind(TrimSection, TrimCategory, "Interval (s)", "간격 (초)", 300,
                "'RAM 부족할 때만'을 끄면 이 간격마다 정리합니다. 켜져 있으면 두 번 정리 사이의 최소 간격입니다.",
                new AcceptableValueRange<int>(30, 1800), 7);

            _trimAfterGc = Bind(TrimSection, TrimCategory, "After GC", "GC 정리 직후에도 실행", false,
                "자동/수동 GC 정리가 끝날 때마다 워킹셋 정리도 같이 합니다. 작업 관리자 숫자는 크게 줄지만 직후 끊김이 생길 수 있습니다.",
                null, 6);
        }

        private void BindGeneralSettings()
        {
            _onlyInRaid = Bind(GeneralSection, GeneralCategory, "Only in raid", "레이드 중에만 자동 실행", true,
                "켜면 은신처·메뉴에서는 자동 정리를 하지 않습니다. 아래 수동 버튼은 언제나 동작합니다.",
                null, 10);

            Bind(GeneralSection, GeneralCategory, "Manual actions", "수동 실행", string.Empty,
                "지금 GC 정리: 위 설정대로 나눠서 정리합니다. 워킹셋 정리: 직후 잠깐 끊길 수 있습니다. " +
                "에셋 정리: 안 쓰는 텍스처·모델을 내립니다(게임 콘솔 UnloadUnusedResources와 같은 기능, 1~3초 멈출 수 있음).",
                null, 9, ManualButtonsDrawer);

            Bind(GeneralSection, GeneralCategory, "Status", "현재 상태", string.Empty,
                "1초마다 갱신되는 메모리 상태입니다.",
                null, 8, StatusDrawer);

            _showOverlay = Bind(GeneralSection, GeneralCategory, "Show overlay", "화면에 메모리 표시", false,
                "화면 왼쪽 위에 메모리 사용량을 한 줄로 띄웁니다. 누수 확인이나 설정 조절할 때 켜 두면 편합니다.",
                null, 7);

            _logIntervalSec = Bind(GeneralSection, GeneralCategory, "Log interval (s)", "로그 기록 간격 (초, 0=끔)", 60,
                "레이드 중 이 간격마다 BepInEx 로그(LogOutput.log)에 메모리 상태를 한 줄씩 남깁니다. 문제 제보할 때 이 로그가 있으면 원인 찾기가 쉽습니다.",
                new AcceptableValueRange<int>(0, 600), 6);
        }

        private ConfigEntry<T> Bind<T>(string section, string category, string key, string displayName, T defaultValue,
            string description, AcceptableValueBase range, int order, Action<ConfigEntryBase> drawer = null)
        {
            var attributes = new ConfigurationManagerAttributes
            {
                Category = category,
                DispName = displayName,
                Order = order,
            };

            if (drawer != null)
            {
                attributes.CustomDrawer = drawer;
                attributes.HideDefaultButton = true;
            }

            return Config.Bind(section, key, defaultValue, new ConfigDescription(description, range, attributes));
        }

        internal void Update()
        {
            float now = Time.realtimeSinceStartup;

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

            if (now < _nextSample)
            {
                return;
            }

            _nextSample = now + 1f;

            bool inGame = GameHelper.IsInGame();
            if (inGame != _inGame)
            {
                _inGame = inGame;
                OnRaidStateChanged(now);
            }

            _snapshot = MemoryStats.Sample();
            bool autoActive = _inGame || !_onlyInRaid.Value;

            if (autoActive)
            {
                EvaluateGc(now);
                EvaluateTrim(now);
                EvaluateLog(now);
            }

            BuildTexts();
        }

        private void OnRaidStateChanged(float now)
        {
            _gcBaseline = -1;
            _lastTrim = now;
            _nextLog = now;

            if (_inGame)
            {
                _warnedNoIncremental = false;
                Logger.LogInfo($"Raid started: GC mode {GarbageCollector.GCMode}, incremental={GarbageCollector.isIncremental}, " +
                               $"mono used {MemoryStats.Gb(MemoryStats.MonoUsed())} GB");
            }
            else
            {
                _gc.Abort(false, "레이드가 끝나서 중단");
                Logger.LogInfo("Left raid");
            }
        }

        private void EvaluateGc(float now)
        {
            if (!_gcEnabled.Value || _gc.Running)
            {
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
            if (growth < (long)(_gcGrowthGb.Value * MemoryStats.BytesPerGb))
            {
                return;
            }

            if (_gc.LastFinishTime >= 0 && now - _gc.LastFinishTime < _gcCooldownSec.Value)
            {
                return;
            }

            if (_gc.Start($"auto, +{MemoryStats.Gb(growth)} GB", _gcAllowBlocking.Value, true))
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
            if (_trimAfterGc.Value)
            {
                Trim("after GC");
            }
        }

        private void EvaluateTrim(float now)
        {
            if (!_trimEnabled.Value || now - _lastTrim < _trimIntervalSec.Value)
            {
                return;
            }

            if (!_trimOnlyLowRam.Value)
            {
                Trim("interval");
                return;
            }

            if (_snapshot.SystemTotal > 0 && _snapshot.SystemAvailablePercent < _trimLowRamPercent.Value)
            {
                Trim($"low RAM {_snapshot.SystemAvailablePercent:0}%");
            }
        }

        private void Trim(string reason)
        {
            _lastTrim = Time.realtimeSinceStartup;
            MemorySnapshot before = MemoryStats.Sample();
            var watch = Stopwatch.StartNew();
            bool ok = MemoryStats.EmptyWorkingSet();
            watch.Stop();
            MemorySnapshot after = MemoryStats.Sample();

            _lastTrimResult = ok
                ? $"{DateTime.Now:HH:mm:ss} 워킹셋 {MemoryStats.Gb(before.WorkingSet)} → {MemoryStats.Gb(after.WorkingSet)} GB ({reason})"
                : $"{DateTime.Now:HH:mm:ss} 실패 (윈도우 API 호출 불가)";
            Logger.LogInfo($"Working set trim ({reason}): ok={ok}, {MemoryStats.Gb(before.WorkingSet)} -> " +
                           $"{MemoryStats.Gb(after.WorkingSet)} GB, call {watch.Elapsed.TotalMilliseconds:0}ms");
        }

        private void UnloadAssets()
        {
            var watch = Stopwatch.StartNew();
            long before = _snapshot.WorkingSet;
            AsyncOperation operation = Resources.UnloadUnusedAssets();
            _lastAssetResult = $"{DateTime.Now:HH:mm:ss} 진행 중...";
            Logger.LogInfo("UnloadUnusedAssets started");

            operation.completed += _ =>
            {
                watch.Stop();
                MemorySnapshot after = MemoryStats.Sample();
                _lastAssetResult = $"{DateTime.Now:HH:mm:ss} 완료 — 워킹셋 {MemoryStats.Gb(before)} → {MemoryStats.Gb(after.WorkingSet)} GB, " +
                                   $"{watch.Elapsed.TotalSeconds:0.0}초";
                Logger.LogInfo($"UnloadUnusedAssets done in {watch.Elapsed.TotalMilliseconds:0}ms, working set " +
                               $"{MemoryStats.Gb(before)} -> {MemoryStats.Gb(after.WorkingSet)} GB");
            };
        }

        private void EvaluateLog(float now)
        {
            int interval = _logIntervalSec.Value;
            if (interval <= 0 || now < _nextLog)
            {
                return;
            }

            _nextLog = now + interval;
            MemorySnapshot s = _snapshot;
            Logger.LogInfo($"[mem] mono used {MemoryStats.Gb(s.MonoUsed)} / reserved {MemoryStats.Gb(s.MonoReserved)} GB | " +
                           $"working set {MemoryStats.Gb(s.WorkingSet)} GB | private {MemoryStats.Gb(s.PrivateBytes)} GB | " +
                           $"system free {MemoryStats.Gb(s.SystemAvailable)}/{MemoryStats.Gb(s.SystemTotal)} GB | " +
                           $"GC {s.GcMode}{(_gc.Running ? " (collecting)" : string.Empty)}");
        }

        private void OnGcModeChanged(GarbageCollector.Mode mode)
        {
            if (!GcRunner.ChangingMode)
            {
                Logger.LogInfo($"Game switched GC mode to {mode}");
            }
        }

        private void BuildTexts()
        {
            MemorySnapshot s = _snapshot;
            string gcState = _gc.Running ? $"정리 중 {_gc.Elapsed:0}초" : MemoryStats.ModeName(s.GcMode);

            var sb = new StringBuilder(512);
            sb.Append("관리 메모리(Mono 힙): 사용 ").Append(MemoryStats.Gb(s.MonoUsed))
              .Append(" GB / 확보 ").Append(MemoryStats.Gb(s.MonoReserved)).Append(" GB\n");
            if (s.SystemTotal > 0)
            {
                sb.Append("게임 전체: 실제 RAM(워킹셋) ").Append(MemoryStats.Gb(s.WorkingSet))
                  .Append(" GB / 커밋 ").Append(MemoryStats.Gb(s.PrivateBytes)).Append(" GB\n");
                sb.Append("시스템 여유 RAM: ").Append(MemoryStats.Gb(s.SystemAvailable)).Append(" / ")
                  .Append(MemoryStats.Gb(s.SystemTotal)).Append(" GB (").Append(s.SystemAvailablePercent.ToString("0"))
                  .Append("%)\n");
            }

            sb.Append("GC 상태: ").Append(gcState)
              .Append(" · 나눠서 하는 GC(증분): ").Append(GarbageCollector.isIncremental ? "지원" : "미지원").Append('\n');
            if (_gcBaseline >= 0 && !_gc.Running)
            {
                sb.Append("다음 자동 정리: 사용량 ")
                  .Append(MemoryStats.Gb(_gcBaseline + (long)(_gcGrowthGb.Value * MemoryStats.BytesPerGb))).Append(" GB 도달 시\n");
            }

            sb.Append("마지막 GC 정리: ").Append(_gc.LastResult).Append('\n');
            sb.Append("마지막 워킹셋 정리: ").Append(_lastTrimResult).Append('\n');
            sb.Append("마지막 에셋 정리: ").Append(_lastAssetResult);
            _statusText = sb.ToString();

            if (_showOverlay.Value)
            {
                _overlayText = $"RAM 클리너 | 힙 {MemoryStats.Gb(s.MonoUsed)}GB · 워킹셋 {MemoryStats.Gb(s.WorkingSet)}GB · " +
                               $"여유 {MemoryStats.Gb(s.SystemAvailable)}GB · GC {gcState}";
            }
        }

        private void ManualButtonsDrawer(ConfigEntryBase entry)
        {
            if (GUILayout.Button(_gc.Running ? "GC 정리 중..." : "지금 GC 정리", GUILayout.ExpandWidth(true)) && !_gc.Running)
            {
                if (!_gc.Start("manual", true, false))
                {
                    Logger.LogWarning("Manual GC could not start");
                }
            }

            if (GUILayout.Button("워킹셋 정리", GUILayout.ExpandWidth(true)))
            {
                Trim("manual");
            }

            if (GUILayout.Button("에셋 정리", GUILayout.ExpandWidth(true)))
            {
                UnloadAssets();
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

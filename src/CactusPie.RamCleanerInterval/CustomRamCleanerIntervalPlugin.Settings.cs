using System;
using System.Collections.Generic;
using BepInEx.Configuration;

namespace CactusPie.RamCleanerInterval
{
    public partial class CustomRamCleanerIntervalPlugin
    {
        // Config keys stay English so the .cfg file is stable; everything the player sees in F12
        // (category, name, description) is Korean through ConfigurationManagerAttributes.
        // Section keys are never renamed (that would reset people's values) - only the categories are.
        private const string ModeSection = "0. Mode";
        private const string GcSection = "1. Auto GC";
        private const string TrimSection = "2. Working set";
        private const string GeneralSection = "3. General";
        private const string AssetSection = "4. Assets";
        private const string TimingSection = "5. Timing";
        private const string LeakSection = "6. Leak tracker";
        private const string HitchSection = "7. Hitch detector";
        private const string ReportSection = "8. Raid report";
        private const string WarningSection = "9. Warnings";
        private const string ProfilerSection = "10. Mod profiler";
        private const string ModObjectsSection = "11. Mod objects";
        private const string FpsSection = "12. FPS history";
        private const string MemSuspectSection = "13. Memory suspects";
        private const string ServerSection = "14. SPT server";
        private const string ForecastSection = "15. Forecast";
        private const string SessionReportSection = "16. Session report";
        private const string HeavyItemsSection = "17. Experimental heavy items";
        private const string WebSection = "18. Web page";
        private const string InternalSection = "Internal";

        // Bumped when a default has to be forced onto existing .cfg files (a saved value beats a new default).
        private const int CurrentConfigVersion = 280;

        private const string ModeCategory = "00. 모드 · 언어";
        private const string GcCategory = "01. 자동 메모리 정리 (GC) — 추천";
        private const string TrimCategory = "02. 워킹셋 정리 (원본 RAM 클리너 방식)";
        private const string AssetCategory = "03. 에셋·VRAM 정리 (SPTVRAMCleaner 개선판)";
        private const string TimingCategory = "04. 전투 중에는 미루기";
        private const string GeneralCategory = "05. 공통 · 수동 실행 · 상태";
        private const string LeakCategory = "06. 누수 추적 (진단용)";
        private const string HitchCategory = "07. 끊김 감지기";
        private const string ReportCategory = "08. 레이드 결산 리포트";
        private const string WarningCategory = "09. 메모리 위험 경고";
        private const string ProfilerCategory = "10. 모드별 부하 분석 (어떤 모드가 프레임을 먹나)";
        private const string ModObjectsCategory = "11. 모드별 오브젝트 증가 (참고용)";
        private const string FpsCategory = "12. 프레임 기록·이전 레이드와 비교";
        private const string MemSuspectCategory = "13. 메모리 누수 의심 판정";
        private const string ServerCategory = "14. SPT 서버 상태 (메모리·응답 시간)";
        private const string ForecastCategory = "15. 메모리 예측 (남은 시간·재시작 권장)";
        private const string SessionReportCategory = "16. 세션 보고서 (그래프 페이지)";
        private const string HeavyItemsCategory = "17. [실험] 무거운 모드 아이템 찾기";
        private const string WebCategory = "18. 웹 페이지 (브라우저로 보기·설정, 127.0.0.1)";

        // Mode / language. Values are stored in the .cfg, so they are fixed bilingual labels.
        private const string LanguageKorean = "한국어";
        private const string LanguageEnglish = "English";
        private const string PresetCustom = "직접 설정 · Custom";
        private const string PresetAuto = "자동 정리 · Auto cleanup";
        private const string PresetQuick = "간단 확인 · Quick view";
        private const string PresetDeep = "집중 분석 · Deep analysis";
        private const string PresetLeak = "누수 추적 · Leak hunt";
        private const string PresetOff = "꺼짐 · Off";

        /// <summary>The order the mode hotkey (Ctrl+F9) walks through; from "custom" it starts at the first one.</summary>
        private static readonly string[] PresetCycle = { PresetAuto, PresetQuick, PresetDeep, PresetLeak, PresetOff };

        private ConfigEntry<string> _language;
        private ConfigEntry<string> _preset;
        private ConfigEntry<string> _presetApplied;
        private ConfigEntry<bool> _trimBeforeOff;

        /// <summary>Every F12 entry with its Korean texts, so the language switch can rewrite what F12 shows.</summary>
        private readonly List<LocalizedEntry> _localized = new List<LocalizedEntry>();

        private sealed class LocalizedEntry
        {
            public ConfigurationManagerAttributes Attributes;
            public string Key;
            public string CategoryKo;
            public string NameKo;
            public string DescriptionKo;
            public ConfigEntryBase Entry;
        }

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

        private ConfigEntry<bool> _unloadAtStart;
        private ConfigEntry<int> _unloadStartDelaySec;
        private ConfigEntry<bool> _unloadAuto;
        private ConfigEntry<float> _unloadNativeGrowthGb;
        private ConfigEntry<int> _unloadCooldownMin;
        private ConfigEntry<bool> _unloadGcFirst;

        private ConfigEntry<bool> _waitForQuiet;
        private ConfigEntry<int> _quietSec;
        private ConfigEntry<int> _gcMaxDeferSec;
        private ConfigEntry<bool> _runOnInventory;

        private ConfigEntry<bool> _leakEnabled;
        private ConfigEntry<int> _leakIntervalMin;
        private ConfigEntry<int> _configVersion;

        private ConfigEntry<bool> _hitchEnabled;
        private ConfigEntry<int> _hitchThresholdMs;
        private ConfigEntry<bool> _hitchModTracking;
        private ConfigEntry<bool> _hitchNotify;
        private ConfigEntry<bool> _overlayHitch;
        private ConfigEntry<bool> _diagMode;
        private ConfigEntry<KeyboardShortcut> _diagHotkey;
        private ConfigEntry<bool> _diagHeavy;
        private ConfigEntry<bool> _postRaidCleanup;
        private ConfigEntry<int> _postRaidDelaySec;
        private ConfigEntry<bool> _reportEnabled;
        private ConfigEntry<bool> _reportNotify;
        private ConfigEntry<bool> _warnEnabled;
        private ConfigEntry<int> _warnCommitPercent;
        private ConfigEntry<bool> _warnVram;
        private ConfigEntry<int> _warnVramPercent;
        private ConfigEntry<int> _warnVramSeconds;
        private ConfigEntry<float> _warnVramSpillGb;
        private ConfigEntry<bool> _warnNotify;

        private ConfigEntry<bool> _profilerEnabled;
        private ConfigEntry<int> _profilerIntervalMin;
        private ConfigEntry<int> _profilerWindowSec;
        private ConfigEntry<bool> _profilerContinuous;
        private ConfigEntry<float> _profilerSuspectMs;
        private ConfigEntry<int> _profilerSuspectShare;
        private ConfigEntry<bool> _objectsEnabled;
        private ConfigEntry<int> _objectsSuspectGrowth;
        private ConfigEntry<bool> _fpsHistoryEnabled;
        private ConfigEntry<int> _fpsDropPercent;
        private ConfigEntry<bool> _fpsNotify;
        private ConfigEntry<bool> _overlayFps;
        private ConfigEntry<bool> _overlayMods;
        private ConfigEntry<int> _overlayModCount;
        private ConfigEntry<bool> _overlayMem;
        private ConfigEntry<bool> _memSuspectEnabled;
        private ConfigEntry<float> _allocSuspectMbPerMin;
        private ConfigEntry<int> _retainedSuspectMbPerMin;
        private ConfigEntry<int> _perDeathSuspectMb;
        private ConfigEntry<bool> _memSuspectNotify;
        private ConfigEntry<int> _keptAfterRaidSuspectMb;

        private ConfigEntry<bool> _serverEnabled;
        private ConfigEntry<bool> _overlayServer;
        private ConfigEntry<bool> _forecastEnabled;
        private ConfigEntry<bool> _overlayForecast;
        private ConfigEntry<int> _forecastWarnMinutes;
        private ConfigEntry<bool> _forecastNotify;
        private ConfigEntry<bool> _restartNotify;
        private ConfigEntry<bool> _sessionReportEnabled;
        private ConfigEntry<bool> _heavyEnabled;
        private ConfigEntry<bool> _overlayHeavy;
        private ConfigEntry<bool> _webEnabled;
        private ConfigEntry<int> _webPort;
        private ConfigEntry<bool> _webLan;

        private ConfigEntry<bool> _onlyInRaid;
        private ConfigEntry<bool> _showOverlay;
        private ConfigEntry<int> _logIntervalSec;

        private void BindSettings()
        {
            BindModeSettings();
            BindGcSettings();
            BindTrimSettings();
            BindAssetSettings();
            BindTimingSettings();
            BindGeneralSettings();
            BindLeakSettings();
            BindHitchSettings();
            BindReportSettings();
            BindWarningSettings();
            BindProfilerSettings();
            BindMemorySuspectSettings();
            BindServerSettings();
            BindForecastSettings();
            BindSessionReportSettings();
            BindHeavyItemSettings();
            BindWebSettings();
            MigrateSettings();
            ApplyLanguage();
            _language.SettingChanged += (_, __) => ApplyLanguage();
            _preset.SettingChanged += (_, __) =>
            {
                ApplyPreset(_preset.Value);
                _presetApplied.Value = _preset.Value;
            };

            _trimBeforeOff = Config.Bind(InternalSection, "Trim before off", true,
                new ConfigDescription("Do not edit.", null, new ConfigurationManagerAttributes { Browsable = false }));

            // The mode can also be changed in the .cfg while the game is closed (the SPT launcher's mod page does that):
            // a value read from the file raises no SettingChanged, so apply it here once.
            _presetApplied = Config.Bind(InternalSection, "Applied preset", _preset.Value,
                new ConfigDescription("Do not edit.", null, new ConfigurationManagerAttributes { Browsable = false }));
            if (_presetApplied.Value != _preset.Value)
            {
                Logger.LogInfo($"Mode changed outside the game ({_presetApplied.Value} -> {_preset.Value}): applying it");
                ApplyPreset(_preset.Value);
                _presetApplied.Value = _preset.Value;
            }
        }

        private void BindModeSettings()
        {
            _language = Bind(ModeSection, ModeCategory, "Language", "언어 (Language)", LanguageKorean,
                "F12, 왼쪽 위 화면 표시, 세션 보고서, 웹 페이지의 언어입니다. F12는 창을 닫았다 다시 열면 바뀐 언어로 보입니다.",
                new AcceptableValueList<string>(LanguageKorean, LanguageEnglish), 100);

            _preset = Bind(ModeSection, ModeCategory, "Preset", "모드", PresetCustom,
                "고르면 아래 설정들을 한 번에 바꿉니다(그 뒤 개별 설정은 자유롭게 바꿔도 됩니다).\n" +
                "• 자동 정리 — 메모리 정리만. 화면 표시 없음, 로그 거의 안 남김. 일반 게임·레이드용, 부담 가장 적음.\n" +
                "• 간단 확인 — 자동 정리 + 왼쪽 위 화면 표시(메모리·FPS·서버·여유 예상), 끊김 횟수, 레이드 결산, 세션 보고서. 아주 가벼움.\n" +
                "• 집중 분석 — 전부 켬: 모드별 부하 상시 측정, 끊김 원인 모드 추적, 원인 추적 로그, [실험] 무거운 아이템 찾기. " +
                "프레임당 0.1~0.5ms 정도 더 들고 로그가 많이 쌓이니 문제를 찾는 동안만 쓰고 돌아오세요.\n" +
                "• 누수 추적 — 판마다 메모리가 쌓이는 원인 찾기: 간단 확인 + 모드별 메모리 생성량, '06. 누수 추적', '11. 모드별 오브젝트 증가', [실험] 무거운 아이템 찾기. " +
                "끊김 원인 추적은 끕니다. 몇 분마다 0.2~1초 끊길 수 있으니 같은 맵 2판 정도만 돌리고 돌아오세요.\n" +
                "• 꺼짐 — 이 모드가 아무것도 안 함: 자동 정리·경고·화면 표시·측정 전부 멈춤(원래 게임 그대로). 웹 페이지와 단축키만 남습니다.\n" +
                "게임 중 Ctrl+F9('모드 바꾸기 단축키')를 누르면 자동 정리 → 간단 확인 → 집중 분석 → 누수 추적 → 꺼짐 순서로 바뀝니다.\n" +
                "• 직접 설정 — 지금 설정을 그대로 둡니다.\n" +
                "참고: 집중 분석에서 다른 모드로 바꿀 때 '모드별 부하 분석'의 측정 장치는 게임을 다시 켜야 완전히 빠집니다(그 전까지는 측정만 멈춤).",
                new AcceptableValueList<string>(PresetCustom, PresetAuto, PresetQuick, PresetDeep, PresetLeak, PresetOff), 99);
        }

        /// <summary>Rewrites what F12 shows (category, name, description) for the chosen language. F12 picks it up when reopened.</summary>
        private void ApplyLanguage()
        {
            Loc.En = _language.Value == LanguageEnglish;
            _cpLanguagePending = true; // CompoundingPerf follows (applied in the next once-a-second tick)
            foreach (LocalizedEntry entry in _localized)
            {
                Loc.Settings.TryGetValue(entry.Key, out string[] english);
                entry.Attributes.Category = Loc.En && Loc.Categories.TryGetValue(entry.CategoryKo, out string category) ? category : entry.CategoryKo;
                entry.Attributes.DispName = Loc.En && english != null ? english[0] : entry.NameKo;
                entry.Attributes.Description = Loc.En && english != null ? english[1] : entry.DescriptionKo;
            }
        }

        /// <summary>
        /// Sets the switches for one of the three modes. Cleaning itself (01-04, after-raid cleanup) and the safety
        /// warnings stay on in every mode; what changes is how much is measured, shown and logged.
        /// </summary>
        private void ApplyPreset(string preset)
        {
            bool auto = preset == PresetAuto;
            bool quick = preset == PresetQuick;
            bool deep = preset == PresetDeep;
            bool leak = preset == PresetLeak;
            bool off = preset == PresetOff;
            if (!auto && !quick && !deep && !leak && !off)
            {
                return;
            }

            bool view = quick || deep || leak;
            bool on = !off;

            // Cleaning + safety: the same in every mode except "off".
            _gcEnabled.Value = on;
            _postRaidCleanup.Value = on;
            _waitForQuiet.Value = true;
            _unloadAtStart.Value = false;
            _unloadAuto.Value = false;
            _warnEnabled.Value = on;
            _warnNotify.Value = on;
            _forecastEnabled.Value = on;
            _forecastNotify.Value = on;
            _restartNotify.Value = on;

            // The other modes leave the working-set trim as the user set it; only "off" stops it, and leaving "off" puts it back.
            bool wasOff = _presetApplied != null && _presetApplied.Value == PresetOff;
            if (off && !wasOff)
            {
                _trimBeforeOff.Value = _trimEnabled.Value;
                _trimEnabled.Value = false;
            }
            else if (!off && wasOff)
            {
                _trimEnabled.Value = _trimBeforeOff.Value;
            }
            _diagHeavy.Value = false;

            // What is measured, shown and logged.
            _showOverlay.Value = view;
            _overlayFps.Value = true;
            _overlayServer.Value = true;
            _overlayForecast.Value = true;
            _overlayHitch.Value = true;
            _overlayMem.Value = true;
            _overlayMods.Value = deep;
            _hitchEnabled.Value = view;
            _hitchNotify.Value = quick || deep; // leak hunt: its own snapshots hitch, don't nag about them
            _hitchModTracking.Value = deep;
            _reportEnabled.Value = view;
            _reportNotify.Value = view;
            _fpsHistoryEnabled.Value = view;
            _fpsNotify.Value = view;
            _memSuspectEnabled.Value = view;
            _memSuspectNotify.Value = view;
            _serverEnabled.Value = view;
            _sessionReportEnabled.Value = view;
            _profilerEnabled.Value = deep || leak; // leak hunt: needed for MB/min per mod, sampled (not continuous)
            _profilerContinuous.Value = deep;
            _heavyEnabled.Value = deep || leak;
            _overlayHeavy.Value = true;
            _logIntervalSec.Value = auto || off ? 0 : quick ? 120 : leak ? 60 : 30;
            _diagMode.Value = deep;

            // Leak hunt: the two snapshot trackers (each hitches 0.2-1 s every few minutes, so never in the others).
            _leakEnabled.Value = leak;
            _objectsEnabled.Value = leak;

            Logger.LogInfo($"Mode applied: {preset}");
        }

        private void MigrateSettings()
        {
            _configVersion = Config.Bind(InternalSection, "Config version", 0,
                new ConfigDescription("Do not edit.", null, new ConfigurationManagerAttributes { Browsable = false }));

            if (_configVersion.Value < 220)
            {
                // v2.1.0 real log: the raid-start unload froze the game 5.6 s and freed 0.07 GB of VRAM.
                if (_unloadAtStart.Value)
                {
                    _unloadAtStart.Value = false;
                    Logger.LogInfo("Settings migrated to 2.2.0: '레이드 시작 시 1회 정리' switched off (new default)");
                }
            }

            if (_configVersion.Value < 230)
            {
                // Five real SAIN-sim logs (2026-09-29..10-01): every auto unload freed ~0 GB and cost a 0.5-1.5 s frame.
                // The growth turned out to be per-death bot equipment that is still referenced (APBS mod item variety).
                if (_unloadAuto.Value)
                {
                    _unloadAuto.Value = false;
                    Logger.LogInfo("Settings migrated to 2.3.0: '레이드 중 자동 정리' switched off (new default)");
                }
            }

            if (_configVersion.Value < 280 && _hitchModTracking.Value)
            {
                // 2.8.0: continuous per-mod measuring moved behind the one-key diagnostic mode; normal play stays light.
                _hitchModTracking.Value = false;
                Logger.LogInfo("Settings migrated to 2.8.0: '끊김 원인 모드 추적' is now off by default (use the diagnostic mode key)");
            }

            _configVersion.Value = CurrentConfigVersion;
        }

        private void BindHitchSettings()
        {
            _hitchEnabled = Bind(HitchSection, HitchCategory, "Enabled", "끊김 감지기 켜기", true,
                "레이드 중 한 프레임이 아래 기준보다 오래 걸리면 로그([hitch] 줄)에 남기고, 그 순간 이 모드가 GC·에셋 정리·누수 추적·" +
                "워킹셋 정리 중이었는지 같이 적습니다. '이 끊김이 RAM 클리너 때문인가?'를 가려내는 용도입니다. " +
                "기록만 하므로 게임 동작에는 영향이 없습니다.",
                null, 10);

            _hitchThresholdMs = Bind(HitchSection, HitchCategory, "Threshold (ms)", "끊김 기준 (ms)", 50,
                "이보다 오래 걸린 프레임만 기록합니다. 참고: 60fps 한 프레임은 약 16ms, 50ms면 눈에 띄는 끊김입니다.",
                new AcceptableValueRange<int>(20, 1000), 9);

            _hitchModTracking = Bind(HitchSection, HitchCategory, "Track mod causes", "끊김 원인 모드 추적 (상시 측정)", false,
                "레이드 내내 모드별 시간을 재서, 끊긴 프레임마다 그 프레임에서 가장 오래 걸린 모드를 원인으로 기록합니다. " +
                "원인은 모드명 / RAM 클리너 / 봇 스폰 / 게임 자체(렌더링·물리·로딩 등 모드 코드 밖) 중 하나. " +
                "'10. 모드별 부하 분석'이 켜져 있어야 하고, 켜면 그쪽도 상시 측정이 됩니다(보통 프레임당 0.1~0.5ms 추가). " +
                "평소엔 꺼 두고 '원인 추적 모드'(단축키)로 필요할 때만 켜는 걸 추천합니다.",
                null, 8);

            _hitchNotify = Bind(HitchSection, HitchCategory, "Suspect notification", "끊김 의심 게임 알림", true,
                "한 원인이 이번 레이드 끊김의 30% 이상(3회 이상)이면 레이드당 한 번 게임 알림으로 알려 줍니다.",
                null, 7);
        }

        private void BindReportSettings()
        {
            _reportEnabled = Bind(ReportSection, ReportCategory, "Enabled", "레이드 결산 리포트 켜기", true,
                "레이드가 끝나면 최고 메모리, 사망 1명당 메모리, GC 횟수·회수량, 끊김 횟수(이 모드 때문인 것 포함)를 " +
                "로그([raid report] 줄)와 F12 '현재 상태'에 한 번에 정리합니다. 설정을 바꿔 가며 판마다 비교할 때 쓰세요.",
                null, 10);

            _reportNotify = Bind(ReportSection, ReportCategory, "In-game notification", "게임 알림으로도 표시", true,
                "결산 요약을 게임 오른쪽 아래 알림으로도 띄웁니다. 끄면 로그와 F12에만 남습니다.",
                null, 9);
        }

        private void BindWarningSettings()
        {
            _warnEnabled = Bind(WarningSection, WarningCategory, "Enabled", "메모리 위험 경고 켜기", true,
                "커밋 메모리(RAM + 페이지 파일 한도)가 바닥나기 직전이면 경고합니다. 한도를 넘으면 게임이 튕깁니다(5분에 한 번까지).",
                null, 10);

            _warnCommitPercent = Bind(WarningSection, WarningCategory, "Commit free below (%)", "커밋 여유 기준 (%)", 10,
                "남은 커밋이 한도의 이 %보다 적거나 2GB 미만이면 경고합니다.",
                new AcceptableValueRange<int>(3, 30), 9);

            _warnVram = Bind(WarningSection, WarningCategory, "VRAM warning", "VRAM 포화 경고", true,
                "게임이 쓰는 그래픽카드의 메모리(VRAM, 모든 프로그램 합계)가 아래 기준 이상이고 실제로 시스템 메모리로 넘친 상태가 계속되면 레이드마다 한 번 경고합니다. " +
                "넘친 텍스처는 시스템 메모리로 가서 끊김 원인이 될 수 있습니다. 듀얼 GPU(Lossless Scaling 등)는 그래픽카드별로 따로 셉니다.",
                null, 8);

            _warnVramPercent = Bind(WarningSection, WarningCategory, "VRAM full at (%)", "VRAM 포화 기준 (%)", 95,
                "VRAM 사용량이 그래픽카드 용량의 이 % 이상이면 '포화'로 봅니다.",
                new AcceptableValueRange<int>(80, 100), 7);

            _warnVramSeconds = Bind(WarningSection, WarningCategory, "VRAM full for (s)", "VRAM 포화 지속 시간 (초)", 120,
                "포화 상태가 이 시간 넘게 이어져야 경고합니다(잠깐 차는 건 무시).",
                new AcceptableValueRange<int>(10, 900), 6);

            _warnVramSpillGb = Bind(WarningSection, WarningCategory, "VRAM spill at (GB)", "VRAM 넘침 기준 (GB, 0=확인 안 함)", 1f,
                "게임의 '공유' 그래픽 메모리(그래픽카드에 못 들어가 시스템 RAM으로 간 양)가 이만큼 이상일 때만 포화로 봅니다. " +
                "게임은 VRAM을 꽉 채워 쓰기도 해서(텍스처 미리 올림) %만으로는 넘쳤는지 알 수 없습니다. 0이면 옛날처럼 %만 봅니다.",
                new AcceptableValueRange<float>(0f, 8f), 5);

            _warnNotify = Bind(WarningSection, WarningCategory, "In-game notification", "게임 알림으로 표시", true,
                "경고를 게임 알림으로 띄웁니다. 끄면 로그와 F12 '현재 상태'에만 남습니다.",
                null, 5);
        }

        private void BindProfilerSettings()
        {
            _profilerEnabled = Bind(ProfilerSection, ProfilerCategory, "Enabled", "모드별 부하 분석 켜기", true,
                "각 모드의 코드(게임에 끼워 넣은 함수, 모드의 매 프레임 함수, SAIN 같은 봇 두뇌)가 한 프레임에 몇 ms를 쓰는지 잽니다. " +
                "결과는 로그([mods] 줄), F12 '현재 상태', 화면 막대에 나오고, 한 모드가 너무 크면 '의심 모드'로 표시합니다. " +
                "게임 시작 후 메인 메뉴에서 한 번 측정 장치를 설치합니다(1~3초). 바꾸면 다음 게임 실행부터 완전히 적용됩니다.",
                null, 10);

            _profilerIntervalMin = Bind(ProfilerSection, ProfilerCategory, "Interval (min)", "측정 간격 (분)", 5,
                "레이드 시작 1분 뒤 첫 측정, 그 뒤로 이 간격마다 측정합니다.",
                new AcceptableValueRange<int>(1, 30), 9);

            _profilerWindowSec = Bind(ProfilerSection, ProfilerCategory, "Window (s)", "한 번 측정 시간 (초)", 15,
                "한 번 측정할 때 몇 초 동안 모을지 정합니다. 측정 중에는 아주 약간(보통 0.1~0.5ms/프레임) 부하가 더 생깁니다.",
                new AcceptableValueRange<int>(5, 60), 8);

            _profilerContinuous = Bind(ProfilerSection, ProfilerCategory, "Continuous", "상시 측정 (막대 실시간 갱신)", false,
                "켜면 레이드 내내 측정하고 위 '한 번 측정 시간'마다 막대를 갱신합니다. 원인을 찾는 동안만 켜는 걸 추천합니다.",
                null, 7);

            _profilerSuspectMs = Bind(ProfilerSection, ProfilerCategory, "Suspect at (ms)", "의심 기준: 프레임당 ms", 2f,
                "1위 모드가 이 ms 이상이면서 아래 비율도 넘으면 '의심 모드'로 표시합니다.",
                new AcceptableValueRange<float>(0.5f, 20f), 6);

            _profilerSuspectShare = Bind(ProfilerSection, ProfilerCategory, "Suspect share (%)", "의심 기준: 프레임 중 비율 (%)", 15,
                "1위 모드가 한 프레임 시간의 이 % 이상을 쓰면 의심합니다. 레이드 초반보다 2배 넘게 느려진 모드도 의심으로 표시합니다.",
                new AcceptableValueRange<int>(5, 80), 5);

            _objectsEnabled = Bind(ModObjectsSection, ModObjectsCategory, "Enabled", "모드별 오브젝트 증가 추적 켜기", false,
                "측정 간격마다 각 모드가 만든 컴포넌트 수를 세서, 레이드 중 계속 늘어나는 모드를 표시합니다(메모리 의심). " +
                "한 번 셀 때 0.2~0.8초 끊길 수 있어 조용한 순간에만 하고 기본은 꺼 둡니다. " +
                "봇 장비처럼 게임이 만드는 오브젝트는 모드 이름이 안 남아서 잡히지 않습니다(그건 '사망 1명당 메모리'로 보세요).",
                null, 10);

            _objectsSuspectGrowth = Bind(ModObjectsSection, ModObjectsCategory, "Suspect growth", "의심 기준: 늘어난 개수", 2000,
                "레이드 시작 이후 한 모드의 컴포넌트가 이만큼 넘게 늘면 의심합니다.",
                new AcceptableValueRange<int>(100, 100000), 9);

            _fpsHistoryEnabled = Bind(FpsSection, FpsCategory, "Enabled", "레이드별 프레임 기록·비교 켜기", true,
                "레이드가 끝날 때마다 맵, 평균 FPS, 1% 저점, 사망 수, 설치된 모드 목록(버전 + DLL 수정 시각)을 " +
                "BepInEx\\config\\RamCleaner.performance.txt에 남기고, 같은 맵의 이전 레이드와 비교합니다.",
                null, 10);

            _fpsDropPercent = Bind(FpsSection, FpsCategory, "Drop warning (%)", "프레임 저하 경고 기준 (%)", 15,
                "같은 맵 이전 레이드보다 평균 FPS가 이 % 넘게 낮으면 경고하고, 그 사이 추가·업데이트·삭제된 모드를 같이 알려 줍니다.",
                new AcceptableValueRange<int>(5, 50), 9);

            _fpsNotify = Bind(FpsSection, FpsCategory, "In-game notification", "게임 알림으로 표시", true,
                "프레임 저하 경고를 게임 알림으로도 띄웁니다.",
                null, 8);
        }

        private void BindMemorySuspectSettings()
        {
            _memSuspectEnabled = Bind(MemSuspectSection, MemSuspectCategory, "Enabled", "메모리 누수 의심 판정 켜기", true,
                "세 가지 신호로 메모리 의심을 판정해 F12·로그([mem suspect])·화면에 표시합니다. " +
                "① 모드별 메모리 생성량(10번 측정 때 같이 잼) ② GC 뒤에도 계속 남는 관리 메모리(진짜 누수 신호) " +
                "③ 사망 1명당 메모리(봇 장비 — 봇 스폰 모드 쪽). 11번을 켜면 모드별 오브젝트 증가도 포함합니다.",
                null, 10);

            _allocSuspectMbPerMin = Bind(MemSuspectSection, MemSuspectCategory, "Alloc suspect (MB/min)", "의심 기준: 모드 메모리 (MB/분)", 50f,
                "한 모드가 분당 이만큼 넘게 만들면서 모드 전체 생성량의 40% 이상이면 의심합니다(레이드 초반보다 2배 늘어도 의심). " +
                "레이드 중엔 게임이 GC를 꺼 둬서 이게 그대로 메모리 증가가 됩니다(1번 자동 GC가 치우긴 함).",
                new AcceptableValueRange<float>(5f, 1000f), 9);

            _retainedSuspectMbPerMin = Bind(MemSuspectSection, MemSuspectCategory, "Retained suspect (MB/min)", "의심 기준: GC 뒤 남는 양 (MB/분)", 10,
                "자동 GC가 끝날 때마다 남는 관리 메모리가 분당 이만큼 넘게 계속 오르면 '관리 메모리 누수 의심'으로 표시합니다(GC 3회·10분 이상 기준).",
                new AcceptableValueRange<int>(1, 500), 8);

            _perDeathSuspectMb = Bind(MemSuspectSection, MemSuspectCategory, "Per-death suspect (MB)", "의심 기준: 사망 1명당 메모리 (MB)", 200,
                "최근 사망 10명 기준 1명당 메모리가 이보다 크면 '봇 장비 메모리 과다'로 표시합니다. 참고: APBS 수정 전 280~340MB, 후 60~75MB.",
                new AcceptableValueRange<int>(50, 2000), 7);

            _keptAfterRaidSuspectMb = Bind(MemSuspectSection, MemSuspectCategory, "Kept after raid suspect (MB)", "의심 기준: 레이드 후 남는 메모리 (MB)", 500,
                "레이드가 끝나고 메뉴에서 GC까지 한 뒤에도 관리 메모리가 레이드 전보다 이만큼 넘게 많으면 경고합니다(레이드 끝난 뒤 메뉴 정리가 켜져 있어야 측정). " +
                "어떤 모드가 지난 레이드 데이터를 놓지 않는 것이라 레이드를 반복할수록 쌓입니다.",
                new AcceptableValueRange<int>(100, 10000), 6);

            _memSuspectNotify = Bind(MemSuspectSection, MemSuspectCategory, "In-game notification", "게임 알림으로 표시", true,
                "새 의심이 생기면 종류별로 레이드당 한 번 게임 알림을 띄웁니다.",
                null, 6);
        }

        private void BindServerSettings()
        {
            _serverEnabled = Bind(ServerSection, ServerCategory, "Enabled", "SPT 서버 상태 보기", true,
                "SPT 서버 프로그램의 메모리(같은 PC의 RAM을 같이 씀)와 서버 응답 시간을 잽니다. 서버 모드가 없어도 동작합니다.\n" +
                "• 봇 생성 응답: 레이드 중 봇을 요청하고 서버가 만들어 줄 때까지 걸린 시간 — 길면 스폰이 늦습니다(봇 장비 모드가 무거우면 늘어남).\n" +
                "• 서버 응답 대기로 멈춤: 어떤 모드가 서버에 '기다리는' 방식으로 물어보면 그동안 게임이 멈춥니다. " +
                "07. 끊김 감지기가 이런 끊김을 '서버 응답 대기 (주소)'로 표시합니다 — 주소(/sain/…, /orbit/… 등)로 어느 모드인지 알 수 있습니다.\n" +
                "끄면 바로 꺼집니다. 처음 켜면 메인 메뉴에서 측정 장치를 한 번 설치합니다.",
                null, 10);

            _overlayServer = Bind(ServerSection, ServerCategory, "Show in overlay", "화면 표시에 서버 칸 넣기", true,
                "왼쪽 위 화면 표시(05번 '화면에 표시' 또는 원인 추적 모드)에 '서버' 칸을 넣습니다.",
                null, 9);
        }

        private void BindForecastSettings()
        {
            _forecastEnabled = Bind(ForecastSection, ForecastCategory, "Enabled", "메모리 예측 켜기", true,
                "• 여유 예상(레이드 중): 최근 10분 동안 메모리가 줄어드는 속도로 'RAM 부족(끊김 시작)' 또는 '커밋 한도(튕김)'까지 남은 시간과, " +
                "지금 사망 1명당 메모리로 '봇 몇 명 더 죽으면 한도'인지 계산합니다(레이드 3분 뒤부터).\n" +
                "• 재시작 권장(레이드 후): 메뉴 정리 뒤에도 판마다 남는 메모리와 레이드 중 늘어나는 양으로 '몇 판 더 가능한지'를 계산합니다(2판째부터).",
                null, 10);

            _overlayForecast = Bind(ForecastSection, ForecastCategory, "Show in overlay", "화면 표시에 예측 넣기", true,
                "왼쪽 위 화면 표시의 '메모리' 칸에 여유 예상(레이드 중)과 재시작 권장(메뉴)을 한 줄로 넣습니다.",
                null, 9);

            _forecastWarnMinutes = Bind(ForecastSection, ForecastCategory, "Warn below (min)", "여유 경고 기준 (분)", 15,
                "여유 예상이 이 시간보다 짧아지면 화면 표시가 빨갛게 바뀌고(아래 알림이 켜져 있으면) 레이드당 한 번 알립니다.",
                new AcceptableValueRange<int>(3, 60), 8);

            _forecastNotify = Bind(ForecastSection, ForecastCategory, "Runway notification", "여유 부족 게임 알림", true,
                "여유 예상이 기준보다 짧아지면 게임 알림으로 띄웁니다(레이드당 한 번).",
                null, 7);

            _restartNotify = Bind(ForecastSection, ForecastCategory, "Restart notification", "재시작 권장 게임 알림", true,
                "다음 판까지만 하고 재시작하는 게 좋겠다고 판단되면 레이드 후 메뉴에서 게임 알림으로 띄웁니다.",
                null, 6);
        }

        private void BindSessionReportSettings()
        {
            _sessionReportEnabled = Bind(SessionReportSection, SessionReportCategory, "Enabled", "세션 보고서 만들기", true,
                "레이드가 끝날 때마다 BepInEx\\RamCleaner\\RamCleaner-report-날짜.html 을 새로 씁니다(게임을 켤 때마다 파일 하나, 최근 15개 보관). " +
                "판별 요약 표, 판마다 메모리·FPS 그래프, 끊김 원인, 남은 메모리와 재시작 판단이 들어갑니다. " +
                "05번의 '세션 보고서 열기' 버튼이나 탐색기에서 더블클릭으로 엽니다(인터넷 불필요).",
                null, 10);
        }

        private void BindHeavyItemSettings()
        {
            _heavyEnabled = Bind(HeavyItemsSection, HeavyItemsCategory, "Enabled", "[실험] 무거운 아이템 찾기 켜기", false,
                "레이드 중 봇이 스폰될 때 게임이 장비 번들(모델·텍스처)을 처음 불러오면서 늘어난 메모리를 재서, " +
                "어느 모드의 어느 아이템이 메모리를 많이 먹는지 순위를 만듭니다(SPT 번들 목록으로 모드 구분). " +
                "'사망 1명당 메모리'가 클 때 APBS 등에서 뺄 아이템을 고르는 참고용입니다.\n" +
                "실험 기능인 이유: 불러오기가 비동기라 다른 일과 섞여서 숫자 하나하나는 부정확합니다. 여러 판에서 반복해서 큰 것만 믿으세요. " +
                "켜면 메인 메뉴에서 측정 장치를 설치합니다(측정 자체 부담은 거의 없음).",
                null, 10);

            _overlayHeavy = Bind(HeavyItemsSection, HeavyItemsCategory, "Show in overlay", "화면 표시에 순위 넣기", true,
                "켜져 있으면 왼쪽 위 화면 표시에 모드별 막대를 넣습니다.",
                null, 9);
        }

        private void BindWebSettings()
        {
            _webEnabled = Bind(WebSection, WebCategory, "Enabled", "웹 페이지 켜기", true,
                "게임이 켜져 있는 동안 브라우저에서 http://127.0.0.1:6977/ 을 열면 실시간 현황(메모리·FPS·서버·여유 예상 그래프), " +
                "진행 중인 레이드까지 들어간 세션 보고서, 이 설정 전체(바꾸면 바로 적용)를 볼 수 있습니다. " +
                "'05. 공통'의 '웹 페이지 열기' 버튼으로도 열립니다. 이 PC에서만 열리고, 아무도 안 보고 있을 때는 거의 일을 하지 않습니다.\n" +
                "zip에 들어 있는 서버 부품(SPT_Runtime\\user\\mods\\RamCleanerInterval.Server)도 깔면 SPT 런처의 '모드 페이지' 목록에 'RAM Cleaner'로 뜹니다(서버와 게임이 같은 PC일 때).",
                null, 10);

            _webPort = Bind(WebSection, WebCategory, "Port", "포트 번호", 6977,
                "주소의 끝 번호입니다(http://127.0.0.1:번호/). 다른 프로그램과 겹쳐서 '시작 실패'가 뜨면 다른 번호로 바꾸세요. 바꾸면 바로 다시 켜집니다.\n" +
                "6969는 SPT 서버가 쓰는 번호라 쓸 수 없습니다. 런처 '모드 페이지'에서 6969 주소(/ramcleaner/)로 열리는 것은 서버 부품이 서버 안에서 이 페이지를 대신 보여 주는 것이니, 이 값은 그대로 두면 됩니다.",
                new AcceptableValueRange<int>(1024, 65535), 9);

            _webLan = Bind(WebSection, WebCategory, "Allow LAN", "다른 기기 접속 허용 (같은 네트워크)", false,
                "켜면 휴대폰·다른 PC에서 이 PC의 IP 주소(예: http://192.168.0.10:6977/)로 열 수 있습니다. " +
                "대신 같은 네트워크의 누구나 설정을 바꿀 수 있고, 처음 켤 때 윈도우 방화벽 허용 창이 뜰 수 있습니다. 집 네트워크에서만 켜세요.",
                null, 8);
        }

        private void BindLeakSettings()
        {
            _leakEnabled = Bind(LeakSection, LeakCategory, "Enabled", "누수 추적 켜기", false,
                "레이드 중 일정 간격으로 게임 안의 모든 오브젝트를 종류별·이름별로 세어서, 레이드 시작 이후 무엇이 계속 늘어나는지 " +
                "로그([leak] 줄)에 남깁니다. 어떤 모드가 메모리를 새게 하는지 찾을 때만 켜세요. " +
                "한 번 셀 때 0.1~1초 끊길 수 있어서 조용한 순간에만 실행합니다.",
                null, 10);

            _leakIntervalMin = Bind(LeakSection, LeakCategory, "Interval (min)", "기록 간격 (분)", 5,
                "레이드 시작 1분 뒤 기준점을 잡고, 그 뒤로 이 간격마다 기록합니다.",
                new AcceptableValueRange<int>(1, 30), 9);
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
                "참고: 60fps는 한 프레임이 약 16ms, 144fps는 약 7ms입니다. " +
                "단, GC 마지막 단계는 나눌 수 없어서 한 프레임이 50~150ms 걸릴 수 있습니다 — 그래서 '전투 중에는 미루기'가 있습니다.",
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
                "메모리를 실제로 비우는 게 아니라 옮기는 것이라, 게임이 다시 쓸 때 읽어오느라 직후에 끊김이 생길 수 있습니다. " +
                "v2.1.0부터는 게임과 별도 스레드에서 실행해서, 정리 자체로 게임이 멈추지는 않습니다.",
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

        private void BindAssetSettings()
        {
            _unloadAtStart = Bind(AssetSection, AssetCategory, "At raid start", "레이드 시작 시 1회 정리", false,
                "카운트다운이 끝나고 레이드가 시작되면, 메뉴·로딩 때 쓰고 남은 텍스처·모델을 한 번 내립니다. " +
                "SPTVRAMCleaner(matsix)의 기능입니다. 실측(2026-09-29): 게임이 5.6초 멈췄고 VRAM은 0.07GB만 줄어서 기본값을 껐습니다.",
                null, 10);

            _unloadStartDelaySec = Bind(AssetSection, AssetCategory, "Start delay (s)", "시작 후 대기 (초)", 3,
                "레이드가 시작되고 몇 초 뒤에 정리할지 정합니다.",
                new AcceptableValueRange<int>(0, 60), 9);

            _unloadAuto = Bind(AssetSection, AssetCategory, "Auto in raid", "레이드 중 자동 정리", false,
                "GC가 관리하지 않는 '네이티브 메모리'(텍스처·모델·사운드·물리, 모드가 만든 오브젝트 등)가 많이 늘면, " +
                "조용한 순간에 안 쓰는 에셋을 내립니다. 두 번 연속 거의 안 줄면 게임을 끌 때까지 자동으로 그만둡니다. " +
                "실측(SAIN 시뮬 로그 5개): 매번 거의 0GB만 줄고 0.5~1.5초 끊겨서 기본값을 껐습니다. " +
                "레이드 중 메모리 증가는 대부분 죽은 봇의 장비(모드 아이템 종류가 많을수록 큼)라 에셋 정리로는 안 줄어듭니다.",
                null, 8);

            _unloadNativeGrowthGb = Bind(AssetSection, AssetCategory, "Native growth trigger (GB)", "시작 기준: 네이티브 증가량 (GB)", 8f,
                "마지막 에셋 정리(또는 레이드 시작) 이후 네이티브 메모리가 이만큼 늘면 정리합니다.",
                new AcceptableValueRange<float>(2f, 64f), 7);

            _unloadCooldownMin = Bind(AssetSection, AssetCategory, "Minimum gap (min)", "자동 정리 최소 간격 (분)", 10,
                "에셋 정리 사이의 최소 간격입니다. 에셋 정리는 GC보다 무거워서 1~3초 끊길 수 있습니다.",
                new AcceptableValueRange<int>(3, 60), 6);

            _unloadGcFirst = Bind(AssetSection, AssetCategory, "GC first", "정리 전에 GC 먼저", true,
                "레이드 중에는 GC가 꺼져 있어서, 이미 버려진 오브젝트가 에셋을 아직 붙잡고 있을 수 있습니다. " +
                "켜면 GC를 먼저 끝낸 뒤 에셋을 내려서 더 많이 비웁니다. (원본 SPTVRAMCleaner의 GC.Collect()는 " +
                "레이드 중 GC가 꺼져 있을 때 무시돼서 실제로는 아무 일도 안 했습니다.)",
                null, 5);
        }

        private void BindTimingSettings()
        {
            _waitForQuiet = Bind(TimingSection, TimingCategory, "Wait for quiet", "전투 중에는 미루기", true,
                "켜면 자동 GC·에셋·워킹셋 정리를, 최근에 총을 쏘거나 맞거나 조준하지 않은 '조용한 순간'까지 미룹니다. " +
                "인벤토리를 열고 있으면 항상 조용한 순간으로 봅니다. 수동 버튼은 미루지 않습니다.",
                null, 10);

            _quietSec = Bind(TimingSection, TimingCategory, "Quiet seconds", "조용한 순간 기준 (초)", 15,
                "마지막으로 쏘거나 맞거나 조준한 뒤 이만큼 지나면 조용한 순간으로 봅니다.",
                new AcceptableValueRange<int>(5, 120), 9);

            _gcMaxDeferSec = Bind(TimingSection, TimingCategory, "GC max wait (s)", "GC 최대 대기 (초, 0=무제한)", 180,
                "전투가 계속돼도 GC는 이 시간이 지나면 그냥 실행합니다(메모리가 끝없이 늘지 않게). 에셋 정리는 끝까지 기다립니다. " +
                "워킹셋 정리는 RAM 부족 비상용이라 최대 60초만 기다립니다.",
                new AcceptableValueRange<int>(0, 900), 8);

            _runOnInventory = Bind(TimingSection, TimingCategory, "Run on inventory", "인벤토리 열면 밀린 정리 실행", true,
                "정리할 게 밀려 있을 때 인벤토리(Tab)를 열면 그 즉시 실행합니다. 가방 정리하는 동안이라 끊김이 거의 안 느껴집니다.",
                null, 7);
        }

        private void BindGeneralSettings()
        {
            _postRaidCleanup = Bind(GeneralSection, GeneralCategory, "After raid cleanup", "레이드 끝난 뒤 메뉴에서 정리", true,
                "레이드가 끝나고 메뉴로 돌아오면 GC → 안 쓰는 에셋 정리 → 워킹셋 정리를 한 번 합니다(메뉴라 끊겨도 상관없음). " +
                "레이드 동안 커진 게임 메모리가 윈도우에 바로 돌아가서 '시스템 여유'가 금방 회복됩니다. " +
                "대신 다음 레이드 로딩 때 일부를 다시 읽어 오느라 로딩이 아주 약간 길어질 수 있습니다.",
                null, 11);

            _postRaidDelaySec = Bind(GeneralSection, GeneralCategory, "After raid delay (s)", "레이드 끝난 뒤 정리까지 대기 (초)", 20,
                "메뉴로 돌아온 뒤 게임이 자체 정리를 마칠 때까지 기다리는 시간입니다.",
                new AcceptableValueRange<int>(5, 120), 11);

            _diagMode = Bind(GeneralSection, GeneralCategory, "Diagnostic mode", "원인 추적 모드 (한 번에 켜기)", false,
                "켜면 화면 표시 + 모드별 상시 측정 + 끊김 원인 추적을 한꺼번에 켭니다(개별 설정과 상관없이). 끄면 개별 설정대로 돌아가고, " +
                "그동안의 요약(의심 모드, 끊김 원인, 메모리)이 전용 로그(BepInEx\\RamCleaner 폴더)에 저장됩니다. 모드를 '집중 분석'으로 고르면 같이 켜집니다.",
                null, 12);

            _diagHotkey = Bind(GeneralSection, GeneralCategory, "Diagnostic mode key", "모드 바꾸기 단축키", new KeyboardShortcut(UnityEngine.KeyCode.F9, UnityEngine.KeyCode.LeftControl),
                "게임 중 누를 때마다 맨 위 '모드'를 자동 정리 → 간단 확인 → 집중 분석 → 누수 추적 → 꺼짐 → 자동 정리 … 순서로 바꿉니다(게임 알림으로 알려 줌). " +
                "'직접 설정' 상태에서 누르면 자동 정리부터 시작합니다.",
                null, 12);

            _diagHeavy = Bind(GeneralSection, GeneralCategory, "Diagnostic includes heavy", "원인 추적에 무거운 추적 포함", false,
                "켜면 원인 추적 모드 때 '06. 누수 추적'과 '11. 모드별 오브젝트 증가'도 같이 켭니다. 각각 몇 분마다 0.2~1초 끊길 수 있습니다.",
                null, 11);

            _onlyInRaid = Bind(GeneralSection, GeneralCategory, "Only in raid", "레이드 중에만 자동 실행", true,
                "켜면 은신처·메뉴에서는 자동 정리를 하지 않습니다. 아래 수동 버튼은 언제나 동작합니다.",
                null, 10);

            Bind(GeneralSection, GeneralCategory, "Manual actions", "수동 실행", string.Empty,
                "GC 정리: 위 설정대로 나눠서 정리합니다. 워킹셋 정리: 별도 스레드에서 실행, 직후 잠깐 끊길 수 있습니다. " +
                "에셋 정리: ('정리 전에 GC 먼저'가 켜져 있으면 GC 후) 안 쓰는 텍스처·모델을 내립니다. 1~3초 멈출 수 있습니다.",
                null, 9, ManualButtonsDrawer);

            Bind(GeneralSection, GeneralCategory, "Status", "현재 상태", string.Empty,
                "1초마다 갱신되는 메모리 상태입니다.",
                null, 8, StatusDrawer);

            _showOverlay = Bind(GeneralSection, GeneralCategory, "Show overlay", "화면 표시 켜기 (왼쪽 위)", false,
                "화면 왼쪽 위에 메모리 사용량을 띄웁니다. 아래 'FPS 줄', '모드별 부하 막대'도 이게 켜져 있어야 보입니다.",
                null, 7);

            _overlayFps = Bind(GeneralSection, GeneralCategory, "Overlay FPS", "화면 표시: FPS 줄", true,
                "메모리 줄 밑에 현재 FPS · 레이드 평균 · 1% 저점(가장 느린 1% 프레임의 FPS = 끊김 체감) · 끊김 횟수를 표시합니다.",
                null, 7);

            _overlayMods = Bind(GeneralSection, GeneralCategory, "Overlay mod bars", "화면 표시: 모드별 부하 막대", true,
                "그 밑에 '10. 모드별 부하 분석' 결과를 막대로 표시하고, 의심 모드가 있으면 빨간 글씨로 알려 줍니다.",
                null, 7);

            _overlayMem = Bind(GeneralSection, GeneralCategory, "Overlay memory bars", "화면 표시: 메모리 의심 막대", true,
                "그 밑에 모드별 메모리 생성량(MB/분) 막대와 '13. 메모리 누수 의심 판정' 결과를 빨간 글씨로 표시합니다.",
                null, 7);

            _overlayHitch = Bind(GeneralSection, GeneralCategory, "Overlay hitch bars", "화면 표시: 끊김 원인 막대", true,
                "그 밑에 이번 레이드 끊김의 원인별 횟수 막대(보라, 회색=게임 자체, 빨강=의심)와 끊김 의심 문구를 표시합니다.",
                null, 7);

            _overlayModCount = Bind(GeneralSection, GeneralCategory, "Overlay mod bar count", "화면 표시: 막대 개수", 5,
                "부하가 큰 순서로 몇 개 모드까지 막대로 보여줄지 정합니다.",
                new AcceptableValueRange<int>(1, 12), 7);

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
            _localized.Add(new LocalizedEntry
            {
                Attributes = attributes,
                Key = section + "|" + key,
                CategoryKo = category,
                NameKo = displayName,
                DescriptionKo = description,
            });

            if (drawer != null)
            {
                attributes.CustomDrawer = drawer;
                attributes.HideDefaultButton = true;
                attributes.HideSettingName = true; // the drawers draw their own title and use the whole window width
            }

            ConfigEntry<T> bound = Config.Bind(section, key, defaultValue, new ConfigDescription(description, range, attributes));
            _localized[_localized.Count - 1].Entry = bound;
            return bound;
        }
    }
}

using System;
using BepInEx.Configuration;

namespace CactusPie.RamCleanerInterval
{
    public partial class CustomRamCleanerIntervalPlugin
    {
        // Config keys stay English so the .cfg file is stable; everything the player sees in F12
        // (category, name, description) is Korean through ConfigurationManagerAttributes.
        // Section keys are never renamed (that would reset people's values) - only the categories are.
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
        private const string InternalSection = "Internal";

        // Bumped when a default has to be forced onto existing .cfg files (a saved value beats a new default).
        private const int CurrentConfigVersion = 280;

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

        private ConfigEntry<bool> _onlyInRaid;
        private ConfigEntry<bool> _showOverlay;
        private ConfigEntry<int> _logIntervalSec;

        private void BindSettings()
        {
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
            MigrateSettings();
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
                "그래픽카드 메모리(VRAM)가 아래 기준 이상인 상태가 계속되면 레이드마다 한 번 경고합니다. " +
                "넘친 텍스처는 시스템 메모리로 가서 끊김 원인이 될 수 있습니다.",
                null, 8);

            _warnVramPercent = Bind(WarningSection, WarningCategory, "VRAM full at (%)", "VRAM 포화 기준 (%)", 95,
                "VRAM 사용량이 그래픽카드 용량의 이 % 이상이면 '포화'로 봅니다.",
                new AcceptableValueRange<int>(80, 100), 7);

            _warnVramSeconds = Bind(WarningSection, WarningCategory, "VRAM full for (s)", "VRAM 포화 지속 시간 (초)", 120,
                "포화 상태가 이 시간 넘게 이어져야 경고합니다(잠깐 차는 건 무시).",
                new AcceptableValueRange<int>(10, 900), 6);

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

            _allocSuspectMbPerMin = Bind(MemSuspectSection, MemSuspectCategory, "Alloc suspect (MB/min)", "의심 기준: 모드 메모리 생성 (MB/분)", 50f,
                "한 모드가 분당 이만큼 넘게 만들면서 모드 전체 생성량의 40% 이상이면 의심합니다(레이드 초반보다 2배 늘어도 의심). " +
                "레이드 중엔 게임이 GC를 꺼 둬서 이게 그대로 메모리 증가가 됩니다(1번 자동 GC가 치우긴 함).",
                new AcceptableValueRange<float>(5f, 1000f), 9);

            _retainedSuspectMbPerMin = Bind(MemSuspectSection, MemSuspectCategory, "Retained suspect (MB/min)", "의심 기준: GC 뒤 남는 양 (MB/분)", 10,
                "자동 GC가 끝날 때마다 남는 관리 메모리가 분당 이만큼 넘게 계속 오르면 '관리 메모리 누수 의심'으로 표시합니다(GC 3회·10분 이상 기준).",
                new AcceptableValueRange<int>(1, 500), 8);

            _perDeathSuspectMb = Bind(MemSuspectSection, MemSuspectCategory, "Per-death suspect (MB)", "의심 기준: 사망 1명당 메모리 (MB)", 200,
                "최근 사망 10명 기준 1명당 메모리가 이보다 크면 '봇 장비 메모리 과다'로 표시합니다. 참고: APBS 수정 전 280~340MB, 후 60~75MB.",
                new AcceptableValueRange<int>(50, 2000), 7);

            _keptAfterRaidSuspectMb = Bind(MemSuspectSection, MemSuspectCategory, "Kept after raid suspect (MB)", "의심 기준: 레이드 후에도 남는 관리 메모리 (MB)", 500,
                "레이드가 끝나고 메뉴에서 GC까지 한 뒤에도 관리 메모리가 레이드 전보다 이만큼 넘게 많으면 경고합니다(레이드 끝난 뒤 메뉴 정리가 켜져 있어야 측정). " +
                "어떤 모드가 지난 레이드 데이터를 놓지 않는 것이라 레이드를 반복할수록 쌓입니다.",
                new AcceptableValueRange<int>(100, 10000), 6);

            _memSuspectNotify = Bind(MemSuspectSection, MemSuspectCategory, "In-game notification", "게임 알림으로 표시", true,
                "새 의심이 생기면 종류별로 레이드당 한 번 게임 알림을 띄웁니다.",
                null, 6);
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

            _unloadNativeGrowthGb = Bind(AssetSection, AssetCategory, "Native growth trigger (GB)", "정리 시작 기준: 네이티브 증가량 (GB)", 8f,
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

            _runOnInventory = Bind(TimingSection, TimingCategory, "Run on inventory", "인벤토리 열면 밀린 정리 바로 실행", true,
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

            _diagMode = Bind(GeneralSection, GeneralCategory, "Diagnostic mode", "원인 추적 모드 (한 번에 켜기/끄기)", false,
                "켜면 화면 표시 + 모드별 상시 측정 + 끊김 원인 추적을 한꺼번에 켭니다(개별 설정과 상관없이). 끄면 개별 설정대로 돌아가고, " +
                "그동안의 요약(의심 모드, 끊김 원인, 메모리)이 전용 로그(BepInEx\\RamCleaner 폴더)에 저장됩니다. 아래 단축키로도 켜고 끌 수 있습니다.",
                null, 12);

            _diagHotkey = Bind(GeneralSection, GeneralCategory, "Diagnostic mode key", "원인 추적 모드 단축키", new KeyboardShortcut(UnityEngine.KeyCode.F9, UnityEngine.KeyCode.LeftControl),
                "게임 중 이 키를 누르면 원인 추적 모드를 켜고 끕니다(게임 알림으로 알려 줌).",
                null, 12);

            _diagHeavy = Bind(GeneralSection, GeneralCategory, "Diagnostic includes heavy", "원인 추적 모드에 무거운 추적 포함", false,
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

            if (drawer != null)
            {
                attributes.CustomDrawer = drawer;
                attributes.HideDefaultButton = true;
            }

            return Config.Bind(section, key, defaultValue, new ConfigDescription(description, range, attributes));
        }
    }
}

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
        private const string InternalSection = "Internal";

        // Bumped when a default has to be forced onto existing .cfg files (a saved value beats a new default).
        private const int CurrentConfigVersion = 220;

        private const string GcCategory = "1. 자동 메모리 정리 (GC) — 추천";
        private const string TrimCategory = "2. 워킹셋 정리 (원본 RAM 클리너 방식)";
        private const string AssetCategory = "3. 에셋·VRAM 정리 (SPTVRAMCleaner 개선판)";
        private const string TimingCategory = "4. 전투 중에는 미루기";
        private const string GeneralCategory = "5. 공통 · 수동 실행 · 상태";
        private const string LeakCategory = "6. 누수 추적 (진단용)";

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

            _configVersion.Value = CurrentConfigVersion;
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

            _unloadAuto = Bind(AssetSection, AssetCategory, "Auto in raid", "레이드 중 자동 정리", true,
                "GC가 관리하지 않는 '네이티브 메모리'(텍스처·모델·사운드·물리, 모드가 만든 오브젝트 등)가 많이 늘면, " +
                "조용한 순간에 안 쓰는 에셋을 내립니다. 두 번 연속 거의 안 줄면(에셋 문제가 아니라 모드 누수) " +
                "게임을 끌 때까지 자동으로 그만둡니다(이 설정을 껐다 켜면 다시 시도).",
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
    }
}

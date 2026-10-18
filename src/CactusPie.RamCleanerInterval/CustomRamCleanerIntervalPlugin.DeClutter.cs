using System;
using System.Net;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;

namespace CactusPie.RamCleanerInterval
{
    /// <summary>
    /// The web page's "DeClutter" tab: the client mod Tyrian-DeClutterer (zzap 2.1 and newer). Its settings are that
    /// plugin's own F12 entries - changing one here is the same as changing it in F12 (its preset logic, raid-time apply
    /// and .cfg save all run as usual). Status comes from its <c>TYR_DeClutterer.StatusBridge</c>. Everything goes through
    /// reflection, so neither mod needs the other; the language follows this plugin's, like CompoundingPerf.
    /// </summary>
    public partial class CustomRamCleanerIntervalPlugin
    {
        private const string DeClutterGuid = "com.TYR.DeClutter";
        private const string DeClutterUrl = "https://github.com/danyhappy564-cmyk/Tyrian-DeClutterer-zzap--bootleg-";
        private const int DeClutterBridgeVersion = 1;

        // Its language values carry both languages ("한국어 · Korean"), unlike ours ("한국어").
        private const string DeClutterKorean = "한국어 · Korean";
        private const string DeClutterEnglish = "English";

        private bool _tyrLanguagePending = true;

        /// <summary>The DeClutter plugin, or null when it is not installed.</summary>
        private static BaseUnityPlugin DeClutterPlugin =>
            Chainloader.PluginInfos.TryGetValue(DeClutterGuid, out PluginInfo info) ? info.Instance as BaseUnityPlugin : null;

        /// <summary>Its StatusBridge, or null when the plugin is missing or too old (zzap 2.1.0 and newer have it).</summary>
        private static Type DeClutterBridge
        {
            get
            {
                Type bridge = DeClutterPlugin?.GetType().Assembly.GetType("TYR_DeClutterer.StatusBridge");
                object version = bridge?.GetField("Version", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                return version is int v && v == DeClutterBridgeVersion ? bridge : null;
            }
        }

        private static bool DeClutterReady => DeClutterBridge != null;

        /// <summary>Once a second: push our language to DeClutter after a change (or at start, once it has loaded).</summary>
        private void SyncDeClutterLanguage(float now)
        {
            if (!_tyrLanguagePending)
            {
                return;
            }

            BaseUnityPlugin plugin = DeClutterPlugin;
            if (plugin is null)
            {
                _tyrLanguagePending = now < 60f;
                return;
            }

            _tyrLanguagePending = false;
            string want = _language.Value == LanguageEnglish ? DeClutterEnglish : DeClutterKorean;
            if (plugin.Config.TryGetEntry(new ConfigDefinition("1. General", "Language"), out ConfigEntry<string> language) && language.Value != want)
            {
                language.Value = want;
                Logger.LogInfo($"[web] DeClutter language set to {want} (follows the RAM cleaner)");
            }
        }

        private static string DeClutterMissing =>
            DeClutterPlugin is null
                ? Loc.L("Tyrian-DeClutterer (zzap 2.1.0 이상)가 설치되어 있지 않습니다. 맵의 잡동사니를 숨기고 그래픽을 가볍게 하는 모드로, 설치하면 이 탭에서 설정을 바로 바꿀 수 있습니다.",
                        "Tyrian-DeClutterer (zzap 2.1.0 or newer) is not installed. It hides map clutter and lightens graphics; with it, this tab changes its settings live.")
                : Loc.L("설치된 Tyrian-DeClutterer가 오래된 버전입니다. zzap 2.1.0 이상으로 업데이트하면 이 탭을 쓸 수 있습니다.",
                        "The installed Tyrian-DeClutterer is too old. Update to zzap 2.1.0 or newer to use this tab.");

        private string BuildDeClutterSettingsJson() =>
            DeClutterReady ? SettingsJson(PluginWebSettings(DeClutterPlugin)) : "{\"error\":" + WebServer.Str(DeClutterMissing) + "}";

        private string ApplyDeClutterSetting(string key, string value) =>
            DeClutterReady
                ? ApplySetting(PluginWebSettings(DeClutterPlugin), key, value)
                : "{\"ok\":false,\"error\":" + WebServer.Str(DeClutterMissing) + "}";

        private string BuildDeClutterStatusJson()
        {
            Type bridge = DeClutterBridge;
            if (bridge == null)
            {
                return "{\"error\":" + WebServer.Str(DeClutterMissing) + "}";
            }

            return "{\"status\":" + WebServer.Str(BridgeGet(bridge, "Status") as string ?? string.Empty) +
                   ",\"last\":\"\",\"version\":" + WebServer.Str(DeClutterPlugin?.Info?.Metadata?.Version?.ToString()) + "}";
        }

        /// <summary>Main thread (reads the other plugin's state).</summary>
        private static string DeClutterPage()
        {
            string H(string text) => WebUtility.HtmlEncode(text);
            string title = Loc.L("RAM 클리너 — 잡동사니·그래픽 (DeClutter)", "RAM Cleaner — DeClutter");
            string content =
                "<h1>" + H(Loc.L("잡동사니·그래픽 (Tyrian-DeClutterer)", "DeClutter (Tyrian-DeClutterer)")) + "</h1><p class=\"sub\">" +
                H(Loc.L("Tyrian-DeClutterer의 설정입니다(게임 F12의 'TYR_DeClutter'와 같은 항목). 바꾸면 게임에 바로 적용되고 저장됩니다. 레이드 중이면 숨김·그래픽이 몇 프레임에 걸쳐 다시 적용됩니다(맵 데칼만 다음 레이드부터). '프리셋'을 고르면 2·3번 항목이 한 번에 바뀌고, 그 뒤 항목을 직접 바꾸면 프리셋이 '직접 설정'으로 바뀝니다.",
                        "Settings of Tyrian-DeClutterer (the same entries as 'TYR_DeClutter' in F12). Changes apply in the game at once and are saved. In a raid the hiding and graphics are re-applied over a few frames (map decals only from the next raid). Picking a 'preset' sets sections 2 and 3 at once; changing an item afterwards switches the preset to 'Custom'.")) +
                "</p>";
            if (!DeClutterReady)
            {
                content += "<section class=\"card\"><p>" + H(DeClutterMissing) + "</p><p><a href=\"" + DeClutterUrl + "\" target=\"_blank\" rel=\"noopener\">" +
                           H(Loc.L("Tyrian-DeClutterer (zzap) 받기", "Get Tyrian-DeClutterer (zzap)")) + "</a></p></section>";
                return Shell(title, "declutter", content, Strings("offline", Offline), null);
            }

            content +=
                "<section class=\"card\"><h2>" + H(Loc.L("현재 상태", "Status")) + "</h2><pre id=\"cpStatus\">" + H(Loc.L("읽는 중...", "Reading...")) + "</pre>" +
                "<p class=\"msg\" id=\"cpLast\" role=\"status\"></p><div class=\"actions\"><button type=\"button\" id=\"cpRefresh\">" +
                H(Loc.L("다시 읽기", "Read again")) + "</button></div></section>" +
                "<div class=\"toolbar\"><input type=\"search\" id=\"q\" placeholder=\"" + H(Loc.L("설정 검색 (예: 데칼, 그림자, 탄피)", "Search settings (e.g. decal, shadow, shell)")) +
                "\" aria-label=\"" + H(Loc.L("설정 검색", "Search settings")) + "\"></div><p class=\"msg\" id=\"msg\" role=\"status\"></p><div id=\"list\"></div>";
            return Shell(title, "declutter", content, SettingsStrings("api/tyr/settings", "api/tyr/status", null, reloadAll: true), "settings.js");
        }
    }
}

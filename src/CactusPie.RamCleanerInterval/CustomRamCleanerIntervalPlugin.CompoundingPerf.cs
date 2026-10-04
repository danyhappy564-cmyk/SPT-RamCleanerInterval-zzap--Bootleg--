using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using UnityEngine;

namespace CactusPie.RamCleanerInterval
{
    /// <summary>
    /// The web page's "Server optimisation" tab: the SPT server mod CompoundingPerf (zzap), through its client plugin.
    /// Its settings are that plugin's own F12 entries - changing one here is the same as changing it in F12, so the
    /// plugin sends it to the server (0.8 s debounce), the server applies it live and saves its config.json. Status comes
    /// from its <c>CompoundingPerf.Client.StatusBridge</c>. Everything goes through reflection: neither mod needs the other.
    /// The language follows this plugin's (its F12 "Language" entry is set when ours changes).
    /// </summary>
    public partial class CustomRamCleanerIntervalPlugin
    {
        private const string CompoundingPerfGuid = "com.echostarz.compoundingperf.client";
        private const string CompoundingPerfUrl = "https://github.com/danyhappy564-cmyk/CompoundingPerf-zzap--Bootleg-";
        private const int CompoundingPerfBridgeVersion = 1;

        private bool _cpLanguagePending = true;

        /// <summary>The CompoundingPerf client plugin, or null when it is not installed.</summary>
        private static BaseUnityPlugin CompoundingPerfPlugin =>
            Chainloader.PluginInfos.TryGetValue(CompoundingPerfGuid, out PluginInfo info) ? info.Instance as BaseUnityPlugin : null;

        /// <summary>Its StatusBridge, or null when the plugin is missing or too old (2.2.2 zzap and newer have it).</summary>
        private static Type CompoundingPerfBridge
        {
            get
            {
                Type bridge = CompoundingPerfPlugin?.GetType().Assembly.GetType("CompoundingPerf.Client.StatusBridge");
                object version = bridge?.GetField("Version", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                return version is int v && v == CompoundingPerfBridgeVersion ? bridge : null;
            }
        }

        private static bool CompoundingPerfReady => CompoundingPerfBridge != null;

        private static object BridgeGet(Type bridge, string name) =>
            bridge.GetProperty(name, BindingFlags.Public | BindingFlags.Static)?.GetValue(null);

        /// <summary>Once a second: push our language to CompoundingPerf after a change (or at start, once it has loaded).</summary>
        private void SyncCompoundingPerfLanguage(float now)
        {
            if (!_cpLanguagePending)
            {
                return;
            }

            BaseUnityPlugin plugin = CompoundingPerfPlugin;
            if (plugin is null)
            {
                // Not installed, or not loaded yet: keep trying for the first minute after start only.
                _cpLanguagePending = now < 60f;
                return;
            }

            _cpLanguagePending = false;
            if (plugin.Config.TryGetEntry(new ConfigDefinition("Language", "Language"), out ConfigEntry<string> language) && language.Value != _language.Value)
            {
                language.Value = _language.Value;
                Logger.LogInfo($"[web] CompoundingPerf language set to {_language.Value} (follows the RAM cleaner)");
            }
        }

        /// <summary>CompoundingPerf's F12 entries (status drawer and hidden entries left out), current language.</summary>
        private static IEnumerable<WebSetting> CompoundingPerfSettings()
        {
            BaseUnityPlugin plugin = CompoundingPerfPlugin;
            if (plugin is null)
            {
                yield break;
            }

            foreach (ConfigDefinition definition in plugin.Config.Keys.ToList())
            {
                ConfigEntryBase entry = plugin.Config[definition];
                object attributes = entry.Description.Tags?.FirstOrDefault(t => t != null && t.GetType().Name == "ConfigurationManagerAttributes");
                T Field<T>(string name) => attributes?.GetType().GetField(name)?.GetValue(attributes) is T value ? value : default(T);
                if (attributes == null || Field<Delegate>("CustomDrawer") != null || Field<bool?>("Browsable") == false)
                {
                    continue;
                }

                string category = Field<string>("Category") ?? definition.Section;
                yield return new WebSetting
                {
                    Key = definition.Section + "|" + definition.Key,
                    Category = category,
                    SortKey = category.Length >= 3 ? category.Substring(0, 3) : category, // "01." - the same in both languages
                    Name = Field<string>("DispName") ?? definition.Key,
                    Description = Field<string>("Description") ?? entry.Description.Description ?? string.Empty,
                    Order = Field<int?>("Order") ?? 0,
                    Entry = entry,
                };
            }
        }

        private static string CompoundingPerfMissing =>
            CompoundingPerfPlugin is null
                ? Loc.L("CompoundingPerf (zzap 2.2.2 이상)가 설치되어 있지 않습니다. SPT 서버 최적화 모드로, 설치하면 이 탭에서 서버 설정을 바로 바꿀 수 있습니다.",
                        "CompoundingPerf (zzap 2.2.2 or newer) is not installed. It is an SPT server optimisation mod; with it, this tab changes the server's settings live.")
                : Loc.L("설치된 CompoundingPerf가 오래된 버전입니다. zzap 2.2.2 이상으로 업데이트하면 이 탭을 쓸 수 있습니다.",
                        "The installed CompoundingPerf is too old. Update to zzap 2.2.2 or newer to use this tab.");

        private string BuildCompoundingPerfSettingsJson() =>
            CompoundingPerfReady ? SettingsJson(CompoundingPerfSettings()) : "{\"error\":" + WebServer.Str(CompoundingPerfMissing) + "}";

        private string ApplyCompoundingPerfSetting(string key, string value) =>
            CompoundingPerfReady
                ? ApplySetting(CompoundingPerfSettings(), key, value)
                : "{\"ok\":false,\"error\":" + WebServer.Str(CompoundingPerfMissing) + "}";

        private string BuildCompoundingPerfStatusJson(bool refresh)
        {
            Type bridge = CompoundingPerfBridge;
            if (bridge == null)
            {
                return "{\"error\":" + WebServer.Str(CompoundingPerfMissing) + "}";
            }

            if (refresh)
            {
                bridge.GetMethod("Refresh", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
            }

            return "{\"connected\":" + WebServer.Bool(BridgeGet(bridge, "Connected") is true) +
                   ",\"status\":" + WebServer.Str(BridgeGet(bridge, "Status") as string ?? string.Empty) +
                   ",\"last\":" + WebServer.Str(BridgeGet(bridge, "LastResult") as string ?? string.Empty) +
                   ",\"version\":" + WebServer.Str(CompoundingPerfPlugin?.Info.Metadata.Version.ToString()) + "}";
        }

        private static string ServerPage()
        {
            string H(string text) => WebUtility.HtmlEncode(text);
            string content =
                "<h1>" + H(Loc.L("서버 최적화 (CompoundingPerf)", "Server optimisation (CompoundingPerf)")) + "</h1><p class=\"sub\">" +
                H(Loc.L("SPT 서버 모드 CompoundingPerf의 설정입니다(게임 F12의 'CompoundingPerf.Client'와 같은 항목). 바꾸면 0.8초 뒤 서버에 적용되고 서버의 config.json에도 저장됩니다. 서버 재시작은 필요 없습니다.",
                        "Settings of the SPT server mod CompoundingPerf (the same entries as 'CompoundingPerf.Client' in F12). Changes reach the server 0.8 s later, apply at once and are saved to its config.json. No server restart needed.")) +
                "</p>";
            if (!CompoundingPerfReady)
            {
                content += "<section class=\"card\"><p>" + H(CompoundingPerfMissing) + "</p><p><a href=\"" + CompoundingPerfUrl + "\" target=\"_blank\" rel=\"noopener\">" +
                           H(Loc.L("CompoundingPerf (zzap) 받기", "Get CompoundingPerf (zzap)")) + "</a></p></section>";
                return Shell(Loc.L("RAM 클리너 — 서버 최적화", "RAM Cleaner — server optimisation"), "server", content, Strings("offline", Offline), null);
            }

            content +=
                "<section class=\"card\"><h2>" + H(Loc.L("서버 상태", "Server status")) + "</h2><pre id=\"cpStatus\">" + H(Loc.L("읽는 중...", "Reading...")) + "</pre>" +
                "<p class=\"msg\" id=\"cpLast\" role=\"status\"></p><div class=\"actions\"><button type=\"button\" id=\"cpRefresh\">" +
                H(Loc.L("서버에서 다시 읽기", "Read again from the server")) + "</button></div></section>" +
                "<div class=\"toolbar\"><input type=\"search\" id=\"q\" placeholder=\"" + H(Loc.L("설정 검색 (예: 정리, 압축, 로그)", "Search settings (e.g. cleanup, compression, log)")) +
                "\" aria-label=\"" + H(Loc.L("설정 검색", "Search settings")) + "\"></div><p class=\"msg\" id=\"msg\" role=\"status\"></p><div id=\"list\"></div>";
            return Shell(Loc.L("RAM 클리너 — 서버 최적화", "RAM Cleaner — server optimisation"), "server", content, SettingsStrings("api/cp/settings", "api/cp/status"), "settings.js");
        }
    }
}

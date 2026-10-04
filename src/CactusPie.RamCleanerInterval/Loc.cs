using System.Collections.Generic;

namespace CactusPie.RamCleanerInterval
{
    /// <summary>
    /// Korean / English switch for everything the player reads: F12 (category, name, description), the overlay, the
    /// F12 status block and the session report. Log lines stay as they are (they are for bug reports).
    /// </summary>
    internal static class Loc
    {
        /// <summary>Set from the "Language" setting; read everywhere text is built.</summary>
        public static bool En;

        public static string L(string korean, string english) => En ? english : korean;

        /// <summary>F12 category names (the Korean constant is the key).</summary>
        public static readonly Dictionary<string, string> Categories = new Dictionary<string, string>
        {
            ["00. 모드 · 언어"] = "00. Mode · language",
            ["01. 자동 메모리 정리 (GC) — 추천"] = "01. Automatic memory cleanup (GC) — recommended",
            ["02. 워킹셋 정리 (원본 RAM 클리너 방식)"] = "02. Working set trim (original RAM cleaner method)",
            ["03. 에셋·VRAM 정리 (SPTVRAMCleaner 개선판)"] = "03. Asset · VRAM cleanup (improved SPTVRAMCleaner)",
            ["04. 전투 중에는 미루기"] = "04. Wait while fighting",
            ["05. 공통 · 수동 실행 · 상태"] = "05. General · manual actions · status",
            ["06. 누수 추적 (진단용)"] = "06. Leak tracker (diagnostics)",
            ["07. 끊김 감지기"] = "07. Hitch detector",
            ["08. 레이드 결산 리포트"] = "08. Raid report",
            ["09. 메모리 위험 경고"] = "09. Memory danger warnings",
            ["10. 모드별 부하 분석 (어떤 모드가 프레임을 먹나)"] = "10. Per-mod frame cost (which mod eats the frame)",
            ["11. 모드별 오브젝트 증가 (참고용)"] = "11. Per-mod object growth (reference)",
            ["12. 프레임 기록·이전 레이드와 비교"] = "12. FPS history · compare with previous raids",
            ["13. 메모리 누수 의심 판정"] = "13. Memory leak suspects",
            ["14. SPT 서버 상태 (메모리·응답 시간)"] = "14. SPT server status (memory · response time)",
            ["15. 메모리 예측 (남은 시간·재시작 권장)"] = "15. Memory forecast (time left · restart advice)",
            ["16. 세션 보고서 (그래프 페이지)"] = "16. Session report (chart page)",
            ["17. [실험] 무거운 모드 아이템 찾기"] = "17. [Experimental] Find heavy mod items",
            ["18. 웹 페이지 (브라우저로 보기·설정, 127.0.0.1)"] = "18. Web page (view · settings in a browser, 127.0.0.1)",
        };

        /// <summary>English (name, description) per "section|key". Missing entries fall back to Korean.</summary>
        public static readonly Dictionary<string, string[]> Settings = new Dictionary<string, string[]>
        {
            // 00. Mode · language
            ["0. Mode|Language"] = new[] { "Language", "Language of F12, the top-left overlay, the session report and the web page. F12 shows the new language after you close and reopen it." },
            ["0. Mode|Preset"] = new[] { "Mode",
                "Picking a mode sets the switches below in one go (you can still change any of them afterwards):\n" +
                "• Auto cleanup — memory cleanup only, no overlay, almost nothing in the log. For normal play and raids; lowest cost.\n" +
                "• Quick view — auto cleanup + the top-left overlay (memory, FPS, server, time-left forecast), stutter counter, raid report and session report. Very light.\n" +
                "• Deep analysis — everything on: per-mod frame cost measured all the time, stutter causes per mod, the diagnostic log and the [experimental] heavy item finder. " +
                "Costs about 0.1–0.5 ms per frame and writes a lot to the log — use it while hunting a problem, then go back.\n" +
                "• Custom — leaves your settings as they are.\n" +
                "Note: when leaving deep analysis, the per-mod cost probe is fully removed only after a game restart (until then it just stops measuring)." },

            // 01. GC
            ["1. Auto GC|Enabled"] = new[] { "Enable automatic GC cleanup", "In raid the game switches the GC (the cleaner that frees unused memory) off completely on 12 GB+ PCs, so memory keeps growing the longer the raid and the more bots there are. This turns the GC on briefly every time memory has grown by a set amount and spreads the work over many frames." },
            ["1. Auto GC|Growth trigger (GB)"] = new[] { "Start cleanup after growth of (GB)", "Cleanup starts when managed memory has grown this much since the last cleanup (or raid start). Lower = more often and shorter, higher = rarer and longer." },
            ["1. Auto GC|Work per frame (ms)"] = new[] { "Work per frame (ms)", "At most this many ms of GC work per frame. Lower = less stutter but cleanup takes longer. For reference: one frame is about 16 ms at 60 fps, 7 ms at 144 fps. The last GC step can't be split and may take 50–150 ms in one frame — that's what 'Wait while fighting' is for." },
            ["1. Auto GC|Minimum gap (s)"] = new[] { "Minimum gap between cleanups (s)", "How long to wait after a cleanup before the next automatic one." },
            ["1. Auto GC|Allow blocking fallback"] = new[] { "Allow a full GC if incremental GC is unsupported", "Only matters if the game doesn't support incremental (split) GC. Then a full GC runs in one go and may freeze for a second or more depending on memory. If the BepInEx log says 'incremental GC supported=True' you can ignore this." },

            // 02. Working set
            ["2. Working set|Enabled"] = new[] { "Enable working set trim", "Forces the game's memory out of RAM (to standby memory / the page file). It moves memory rather than freeing it, so the game may stutter right after while it reads it back. Runs on its own thread, so the trim itself doesn't freeze the game." },
            ["2. Working set|Only when RAM is low"] = new[] { "Only when system RAM is low", "On: trim only when Windows' free RAM is below the threshold below (recommended). Off: trim on a fixed interval no matter what, like the original mod." },
            ["2. Working set|Low RAM threshold (%)"] = new[] { "Low RAM threshold (%)", "Free system RAM below this % of total counts as 'low'. 10% of 64 GB is about 6.4 GB." },
            ["2. Working set|Interval (s)"] = new[] { "Interval (s)", "With 'Only when RAM is low' off, trim this often. With it on, this is the minimum gap between trims." },
            ["2. Working set|After GC"] = new[] { "Also right after GC", "Trim the working set after every automatic/manual GC too. Task Manager numbers drop a lot, but there may be a stutter right after." },

            // 03. Assets
            ["4. Assets|At raid start"] = new[] { "Once at raid start", "When the countdown ends, unload textures/models left over from the menu and loading. A SPTVRAMCleaner (matsix) feature. Measured 2026-09-29: the game froze 5.6 s and VRAM dropped only 0.07 GB, so it's off by default." },
            ["4. Assets|Start delay (s)"] = new[] { "Delay after start (s)", "How many seconds after the raid starts to run it." },
            ["4. Assets|Auto in raid"] = new[] { "Automatically in raid", "When 'native memory' the GC doesn't manage (textures, models, sounds, physics, objects mods created, …) grows a lot, unload unused assets at a quiet moment. Stops by itself until the game closes if two runs in a row free almost nothing. Measured in 5 SAIN sim logs: freed ~0 GB each time and stuttered 0.5–1.5 s, so it's off by default. Most in-raid growth is dead bots' gear, which asset cleanup can't free." },
            ["4. Assets|Native growth trigger (GB)"] = new[] { "Start after native growth of (GB)", "Clean up when native memory has grown this much since the last asset cleanup (or raid start)." },
            ["4. Assets|Minimum gap (min)"] = new[] { "Minimum gap (min)", "Minimum time between asset cleanups. They are heavier than GC and may stutter 1–3 s." },
            ["4. Assets|GC first"] = new[] { "GC first", "In raid the GC is off, so objects already thrown away may still hold assets. On: finish a GC before unloading assets, which frees more. (The original SPTVRAMCleaner's GC.Collect() was ignored in raid while the GC is off, so it did nothing there.)" },

            // 04. Timing
            ["5. Timing|Wait for quiet"] = new[] { "Wait while fighting", "Delay automatic GC / asset / working set cleanup until a 'quiet moment' with no recent shooting, getting hit or aiming. An open inventory always counts as quiet. Manual buttons never wait." },
            ["5. Timing|Quiet seconds"] = new[] { "Quiet after (s)", "This long after your last shot, hit or aim counts as a quiet moment." },
            ["5. Timing|GC max wait (s)"] = new[] { "GC maximum wait (s, 0 = no limit)", "Even if fighting goes on, GC runs after this long (so memory can't grow forever). Asset cleanup waits until the end. Working set trim is an emergency for low RAM and waits 60 s at most." },
            ["5. Timing|Run on inventory"] = new[] { "Run pending cleanup when the inventory opens", "If cleanup is waiting, opening the inventory (Tab) runs it right away — you're sorting your bag, so you barely notice a stutter." },

            // 05. General
            ["3. General|After raid cleanup"] = new[] { "Clean up in the menu after a raid", "Back in the menu after a raid, run GC → unused assets → working set once (stutters don't matter in the menu). The memory the game grew in raid goes straight back to Windows, so 'system free' recovers fast. The next raid's loading may take slightly longer while some of it is read back." },
            ["3. General|After raid delay (s)"] = new[] { "Wait after the raid (s)", "Time to let the game finish its own cleanup after returning to the menu. If you also use CompoundingPerf, keep its post-raid delay at least 10 s longer than this." },
            ["3. General|Diagnostic mode"] = new[] { "Diagnostic mode (one switch)", "On: overlay + per-mod measurement all the time + stutter cause tracking, regardless of the individual settings. Off: back to the individual settings, and a summary (suspect mods, stutter causes, memory) is saved to the dedicated log (BepInEx\\RamCleaner folder). The hotkey below toggles it too." },
            ["3. General|Diagnostic mode key"] = new[] { "Diagnostic mode hotkey", "Press in game to turn diagnostic mode on/off (an in-game notification confirms it)." },
            ["3. General|Diagnostic includes heavy"] = new[] { "Diagnostic mode includes heavy tracking", "On: diagnostic mode also turns on '06. Leak tracker' and '11. Per-mod object growth'. Each may stutter 0.2–1 s every few minutes." },
            ["3. General|Only in raid"] = new[] { "Automatic cleanup only in raid", "On: no automatic cleanup in the hideout or menus. The manual buttons below always work." },
            ["3. General|Manual actions"] = new[] { "Manual actions", "GC: cleans up in slices as set above. Working set: runs on its own thread, may stutter briefly right after. Assets: (after a GC if 'GC first' is on) unloads unused textures/models; may freeze 1–3 s. Session report: opens the chart page in your browser." },
            ["3. General|Status"] = new[] { "Current status", "Memory status, refreshed every second." },
            ["3. General|Show overlay"] = new[] { "Show the overlay (top left)", "Shows memory use in the top-left corner. The 'FPS line' and 'mod bars' below need this on." },
            ["3. General|Overlay FPS"] = new[] { "Overlay: FPS line", "Under the memory line: current FPS · raid average · 1% low (FPS of the slowest 1% of frames = how stutter feels) · stutter count." },
            ["3. General|Overlay mod bars"] = new[] { "Overlay: per-mod cost bars", "Below that, the '10. Per-mod frame cost' result as bars; a suspect mod is shown in red." },
            ["3. General|Overlay memory bars"] = new[] { "Overlay: memory suspect bars", "Below that, per-mod memory creation (MB/min) bars and '13. Memory leak suspects' in red." },
            ["3. General|Overlay hitch bars"] = new[] { "Overlay: stutter cause bars", "Below that, this raid's stutters counted by cause (purple; gray = the game itself; red = suspect) and the stutter suspect line." },
            ["3. General|Overlay mod bar count"] = new[] { "Overlay: number of bars", "How many mods to show as bars, biggest first." },
            ["3. General|Log interval (s)"] = new[] { "Log interval (s, 0 = off)", "In raid, write one memory status line to the BepInEx log (LogOutput.log) this often. Very helpful when reporting a problem." },

            // 06. Leak
            ["6. Leak tracker|Enabled"] = new[] { "Enable leak tracker", "In raid, every so often count every object in the game by type and name and log ([leak] lines) what keeps growing since the raid started. Only turn on while looking for a mod that leaks memory. One count may stutter 0.1–1 s, so it runs only at quiet moments." },
            ["6. Leak tracker|Interval (min)"] = new[] { "Interval (min)", "The baseline is taken 1 minute into the raid, then a record every this many minutes." },

            // 07. Hitch
            ["7. Hitch detector|Enabled"] = new[] { "Enable hitch detector", "In raid, a frame that takes longer than the threshold is logged ([hitch] lines) together with whether this mod was doing a GC / asset cleanup / leak count / working set trim at that moment — to tell 'is this stutter the RAM cleaner?'. Recording only; it doesn't change the game." },
            ["7. Hitch detector|Threshold (ms)"] = new[] { "Stutter threshold (ms)", "Only frames longer than this are recorded. For reference: one frame at 60 fps is about 16 ms; 50 ms is a noticeable stutter." },
            ["7. Hitch detector|Track mod causes"] = new[] { "Track which mod causes stutters (always measuring)", "Measures per-mod time for the whole raid and records, for each long frame, the mod that took longest in it. The cause is a mod / the RAM cleaner / waiting on the SPT server / a bot spawn / the game itself (rendering, physics, loading — outside mod code). Needs '10. Per-mod frame cost' on and makes it measure all the time (usually +0.1–0.5 ms per frame). Best left off and turned on with diagnostic mode (hotkey) when needed." },
            ["7. Hitch detector|Suspect notification"] = new[] { "Stutter suspect notification", "If one cause is behind 30%+ of this raid's stutters (3 or more), show one in-game notification per raid." },

            // 08. Report
            ["8. Raid report|Enabled"] = new[] { "Enable raid report", "At raid end, sum up peak memory, memory per death, GC count and amount freed, and stutters (including ours) in the log ([raid report] line) and F12 status. Handy for comparing raids while changing settings." },
            ["8. Raid report|In-game notification"] = new[] { "Also as an in-game notification", "Shows the summary as a notification in the bottom right too. Off: log and F12 only." },

            // 09. Warnings
            ["9. Warnings|Enabled"] = new[] { "Enable memory danger warnings", "Warns when commit memory (RAM + page file limit) is about to run out. Past the limit the game crashes (at most once every 5 minutes)." },
            ["9. Warnings|Commit free below (%)"] = new[] { "Free commit threshold (%)", "Warn when free commit is below this % of the limit or under 2 GB." },
            ["9. Warnings|VRAM warning"] = new[] { "VRAM full warning", "Warn once per raid when graphics memory (VRAM) stays at or above the threshold below. Textures that don't fit spill into system memory and can cause stutter." },
            ["9. Warnings|VRAM full at (%)"] = new[] { "VRAM full at (%)", "VRAM use at or above this % of the card counts as 'full'." },
            ["9. Warnings|VRAM full for (s)"] = new[] { "VRAM full for (s)", "Warn only if it stays full longer than this (brief peaks are ignored)." },
            ["9. Warnings|In-game notification"] = new[] { "In-game notification", "Show warnings as in-game notifications. Off: log and F12 status only." },

            // 10. Profiler
            ["10. Mod profiler|Enabled"] = new[] { "Enable per-mod frame cost", "Measures how many ms per frame each mod's code takes (functions it patched into the game, its per-frame functions, bot brains like SAIN). Results go to the log ([mods] lines), F12 status and overlay bars; a mod that takes too much is marked as a suspect. The probe installs once in the main menu (1–3 s). Fully applies from the next game start." },
            ["10. Mod profiler|Interval (min)"] = new[] { "Interval (min)", "First measurement 1 minute into the raid, then every this many minutes." },
            ["10. Mod profiler|Window (s)"] = new[] { "Measurement length (s)", "How many seconds each measurement collects. While measuring there is a very small extra cost (usually 0.1–0.5 ms per frame)." },
            ["10. Mod profiler|Continuous"] = new[] { "Measure all the time", "On: measure the whole raid and refresh the bars every measurement length. Recommended only while hunting a problem." },
            ["10. Mod profiler|Suspect at (ms)"] = new[] { "Suspect at ms per frame", "The top mod is a suspect when it takes at least this many ms and the share below." },
            ["10. Mod profiler|Suspect share (%)"] = new[] { "Suspect share of the frame (%)", "The top mod is a suspect when it uses at least this % of a frame. A mod that got more than 2× slower than early in the raid is also flagged." },

            // 11. Objects
            ["11. Mod objects|Enabled"] = new[] { "Enable per-mod object growth", "Every measurement counts the components each mod created and flags mods whose count keeps growing in raid (memory suspect). One count may stutter 0.2–0.8 s, so it runs only at quiet moments and is off by default. Objects the game creates (like bot gear) carry no mod name and aren't caught — see 'memory per death' for those." },
            ["11. Mod objects|Suspect growth"] = new[] { "Suspect growth (count)", "Suspect a mod whose components grew by more than this since the raid started." },

            // 12. FPS history
            ["12. FPS history|Enabled"] = new[] { "Enable FPS history and comparison", "At raid end, save map, average FPS, 1% low, deaths and the installed mod list (version + DLL time) to BepInEx\\config\\RamCleaner.performance.txt and compare with the previous raid on the same map." },
            ["12. FPS history|Drop warning (%)"] = new[] { "FPS drop warning (%)", "Warn if average FPS is more than this % below the previous raid on the same map, listing mods added/updated/removed in between." },
            ["12. FPS history|In-game notification"] = new[] { "In-game notification", "Also show the FPS drop warning as an in-game notification." },

            // 13. Memory suspects
            ["13. Memory suspects|Enabled"] = new[] { "Enable memory leak suspects", "Judges memory suspects from three signals and shows them in F12, the log ([mem suspect]) and the overlay: ① per-mod memory creation (measured with section 10) ② managed memory that stays after every GC (the real leak signal) ③ memory per death (bot gear — the bot spawn mod's side). With section 11 on, per-mod object growth is included too." },
            ["13. Memory suspects|Alloc suspect (MB/min)"] = new[] { "Suspect at mod memory creation (MB/min)", "Suspect a mod that creates more than this per minute and 40%+ of all mod creation (or 2× its early-raid rate). In raid the game's GC is off, so this turns straight into memory growth (section 01's automatic GC does clean it up)." },
            ["13. Memory suspects|Retained suspect (MB/min)"] = new[] { "Suspect at memory left after GC (MB/min)", "Flag a 'managed memory leak suspect' if the managed memory left after each automatic GC keeps rising faster than this (after 3 GCs and 10+ minutes)." },
            ["13. Memory suspects|Per-death suspect (MB)"] = new[] { "Suspect at memory per death (MB)", "Flag 'too much bot gear memory' when memory per death over the last 10 deaths is above this. For reference: APBS before its fix 280–340 MB, after 60–75 MB." },
            ["13. Memory suspects|Kept after raid suspect (MB)"] = new[] { "Suspect at memory kept after a raid (MB)", "Warn if, after the raid and a GC in the menu, managed memory is still this much above what it was before the raid (needs 'clean up in the menu after a raid'). Some mod is holding on to the last raid's data, and it piles up raid after raid." },
            ["13. Memory suspects|In-game notification"] = new[] { "In-game notification", "Show one in-game notification per raid for each new kind of suspect." },

            // 14. Server
            ["14. SPT server|Enabled"] = new[] { "Show SPT server status", "Measures the SPT server program's memory (it shares your PC's RAM) and its response times. Works without any server mod.\n• Bot generation response: how long the server takes to make the bots the game asks for in raid — long times mean late spawns (heavier bot gear mods make it longer).\n• Frozen waiting on the server: when a mod asks the server something and waits for the answer, the game freezes meanwhile. '07. Hitch detector' labels these stutters 'waiting on server (URL)' — the URL (/sain/…, /orbit/…) tells you the mod.\nTurning it off takes effect at once. The first time it's on, the probe installs once in the main menu." },
            ["14. SPT server|Show in overlay"] = new[] { "Show in the overlay", "Adds an 'SPT server' block to the top-left overlay (section 05 'show overlay' or diagnostic mode)." },

            // 15. Forecast
            ["15. Forecast|Enabled"] = new[] { "Enable memory forecast", "• Time left (in raid): from how fast memory dropped over the last 10 minutes, the time until 'RAM low (stutter starts)' or 'commit limit (crash)', and, at the current memory per death, how many more bot deaths that is (from 3 minutes into the raid).\n• Restart advice (after a raid): from the memory left behind each raid after the menu cleanup and the in-raid growth, how many more raids fit (from the second raid)." },
            ["15. Forecast|Show in overlay"] = new[] { "Show in the overlay", "Adds one line to the overlay's memory block: time left (in raid) or restart advice (in the menu)." },
            ["15. Forecast|Warn below (min)"] = new[] { "Warn below (min)", "If the time left drops below this, the overlay line turns red and (if the notification below is on) you're told once per raid." },
            ["15. Forecast|Runway notification"] = new[] { "Low time-left notification", "In-game notification when the time left drops below the threshold (once per raid)." },
            ["15. Forecast|Restart notification"] = new[] { "Restart advice notification", "In-game notification in the menu after a raid when it's best to play one more raid and then restart." },

            // 16. Session report
            ["16. Session report|Enabled"] = new[] { "Write the session report", "After every raid, rewrite BepInEx\\RamCleaner\\RamCleaner-report-<date>.html (one file per game start, newest 15 kept): per-raid table, memory and FPS charts per raid, stutter causes, memory left behind and restart advice. Open it with the 'open session report' button in section 05 or by double-clicking it (no internet needed)." },

            // 17. Heavy items
            ["17. Experimental heavy items|Enabled"] = new[] { "[Experimental] Enable heavy mod item finder", "When bots spawn in raid, measures how much memory the game's gear bundles (models, textures) add when loaded for the first time and ranks which mod's which item costs the most (mods told apart by SPT's bundle list). A guide for what to remove from APBS etc. when 'memory per death' is high.\nWhy experimental: loading is asynchronous and mixes with other work, so single numbers are inexact. Trust only what stays large across raids. Turning it on installs a probe in the main menu (measuring itself costs almost nothing)." },
            ["17. Experimental heavy items|Show in overlay"] = new[] { "Show in the overlay", "When on, adds per-mod bars to the top-left overlay." },

            // 18. Web page
            ["18. Web page|Enabled"] = new[] { "Enable the web page", "While the game runs, open http://127.0.0.1:6977/ in a browser for the live view (memory · FPS · server · time-left charts), the session report including the raid in progress, and all of these settings (changes apply at once). The 'open web page' button in section 05 opens it too. Only this PC can open it, and it does almost nothing while nobody is looking.\nWith the server part from the zip (SPT_Runtime\\user\\mods\\RamCleanerInterval.Server) it is also listed as 'RAM Cleaner' in the SPT launcher's mod pages (server and game on the same PC)." },
            ["18. Web page|Port"] = new[] { "Port number", "The number at the end of the address (http://127.0.0.1:number/). If it says 'could not start' because another program uses it, pick another number. Changing it restarts the page right away." },
            ["18. Web page|Allow LAN"] = new[] { "Allow other devices on the network", "When on, a phone or another PC can open it with this PC's IP address (e.g. http://192.168.0.10:6977/). Anyone on the same network can then change the settings, and Windows Firewall may ask for permission the first time. Only turn it on at home." },
        };
    }
}

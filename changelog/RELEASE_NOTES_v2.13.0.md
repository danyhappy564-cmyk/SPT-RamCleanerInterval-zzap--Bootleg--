# RAM 클리너 (RamCleanerInterval-zzap--Bootleg-) v2.13.0 릴리즈 노트 (v2.12.0 대비)

> 원작: CactusPie — SPT-RamCleanerInterval (GPL-3.0). 이 버전은 비공식 SPT 4.1 포팅입니다. 문제가 생겨도 원작자에게 문의하지 말아 주세요.
> 이번 버전은 **세션 보고서를 더 편하게 보기** 위한 작은 업데이트입니다. 사용자 제보 3건과 로그에서 찾은 계산 오류 1건을 고쳤고, 메모리 원인 찾기용 모드를 하나 추가했습니다.

**<한눈에 보기>**

1. **보고서 자동 새로고침** — 웹 페이지 '세션 보고서'가 레이드 중 10초마다 저절로 바뀝니다. 이제 F5를 누를 필요가 없습니다.
2. **'지난 기록' 탭** — 게임을 다시 켜도 이전 세션들의 레이드 기록을 웹 페이지에서 볼 수 있습니다.
3. **강제 종료한 판도 기록** — 레이드 도중 Alt+F4로 끄거나 게임이 튕겨도 그 판이 보고서에 남습니다.
4. **새 모드 '누수 추적'** — 판마다 메모리가 쌓이는 원인을 찾는 설정을 F12 '모드'에서 한 번에 켭니다.
5. **Ctrl+F9로 모드 바꾸기** — 누를 때마다 자동 정리 → 간단 확인 → 집중 분석 → 누수 추적 → **꺼짐** 순서로 바뀝니다. F12나 웹 페이지에 들어가지 않아도 됩니다.
6. **F12 글자 잘림 수정** — '현재 상태'와 버튼이 창 전체 너비를 쓰고, 긴 줄은 줄바꿈됩니다.
7. **VRAM을 그래픽카드별로** — 듀얼 GPU(Lossless Scaling 등)에서 카드별로 따로 재고, VRAM 경고는 실제로 넘쳤을 때만 뜹니다.

**<설치>**

- zip을 **SPT 폴더(예: `E:\SPT 4.1`)에 그대로 풀어서 덮어쓰면** 됩니다. 들어가는 위치와 F12 설정 값은 v2.12.0과 같습니다. F12 '모드'에 선택지 **누수 추적**·**꺼짐**이 생겼고, Ctrl+F9의 역할이 '원인 추적 켜고 끄기'에서 '모드 바꾸기'로 바뀌었습니다. 이름이 길던 설정 몇 개는 이름만 짧아졌습니다(값은 그대로).
- 런처 '모드 페이지'용 서버 부품(`SPT_Runtime\user\mods\RamCleanerInterval.Server\`)은 버전 숫자만 바뀌었습니다. 같이 덮어쓰면 됩니다.

---

**<변경점>**

- **웹 페이지 '세션 보고서' 자동 새로고침**

1. 전에는 `http://127.0.0.1:6977/` 의 세션 보고서가 열 때 한 번 만들어진 화면이라, 진행 상황을 보려면 F5를 눌러야 했습니다.
2. 이제 **레이드 중에는 10초마다**, 레이드 밖에서는 30초마다 저절로 새로 고쳐집니다. 그래프도 새 숫자로 다시 그려집니다.
3. 새로 고쳐져도 **스크롤 위치와 펼쳐 둔 항목은 그대로**입니다. 탭을 숨겨 두면 30초마다로 줄어듭니다.
4. 보고서 위의 **'자동 새로고침'** 체크를 끄면 멈춥니다. 브라우저가 이 선택을 기억합니다. 게임과 연결이 끊기면 같은 줄에 표시됩니다.

- **'지난 기록' 탭 — 이전 세션의 레이드 기록**

1. 전에는 게임을 다시 켜면 웹 페이지 보고서가 새 세션부터 시작해서, 이전 레이드 기록이 안 보였습니다.
2. 위 메뉴에 **'지난 기록'** 이 생겼습니다. `BepInEx\RamCleaner\RamCleaner-report-*.html`(최근 15개 세션)을 시작 시각 순으로 보여 주고, 누르면 그 보고서가 열립니다.
3. 세션 보고서 화면 위에도 **'지난 세션 기록 보기'** 버튼이 있습니다.

- **레이드 도중 Alt+F4·튕김으로 나가도 그 판이 남음**

1. 전에는 보고서 파일이 레이드가 **정상적으로 끝날 때만** 저장돼서, 도중에 게임을 끄면 그 판이 통째로 사라졌습니다.
2. 이제 레이드 시작 1분 뒤부터 **1분마다** 진행 중인 판을 저장하고, 레이드 중 게임을 닫으면 **닫는 순간 한 번 더** 저장합니다.
3. 그런 판은 보고서에 이렇게 표시됩니다.
   - **'게임 종료로 중단 · 시각'**: Alt+F4 등으로 닫은 판
   - **'끝나지 않음 · 시각까지 기록'**: 튕김이나 작업 관리자 강제 종료 등으로, 마지막 1분 저장분까지만 남은 판
4. 레이드를 끝까지 마치면 '중단' 표시 없이 평소처럼 저장됩니다. 1분이 안 된 판은 전처럼 기록하지 않습니다.
5. 저장은 1분에 한 번이라 레이드 중 부담은 거의 없을 것으로 봅니다(측정은 아직 안 함).

- **'레이드 후 남은 메모리' 계산 수정**

1. 2판째부터 이 값이 "-1.77GB"처럼 마이너스로 나오는 경우가 있었습니다. 비교 기준이 '레이드 로딩 시작 때'라서, 로딩 중 잠깐 생긴 메모리까지 기준에 들어갔기 때문입니다.
2. 이제 2판째부터는 **지난 판이 끝나고 정리한 뒤의 값**과 비교합니다. 판마다 실제로 쌓이는 양이 제대로 보입니다(보고서에는 "지난 판 정리 뒤보다 +0.99GB"처럼 표시).

- **새 모드 '누수 추적'**

1. 전에는 집중 분석을 골라도 '06. 누수 추적'과 '11. 모드별 오브젝트 증가'는 꺼져 있었습니다. 몇 분마다 0.2~1초씩 끊기는 기능이라, 끊김 원인을 재는 집중 분석에 넣으면 측정이 흐려지기 때문입니다. 그래서 판마다 메모리가 쌓이는 원인을 찾으려면 F12에서 따로 켜야 했습니다.
2. 이제 F12 '모드'에서 **누수 추적**을 고르면 메모리 원인 찾기에 필요한 것만 한 번에 켭니다.
   - 켬: 간단 확인 전부, 모드별 메모리 생성량(몇 분마다 측정), 06 누수 추적, 11 모드별 오브젝트 증가, [실험] 무거운 아이템 찾기, 로그 60초 간격
   - 끔: 끊김 원인 모드 추적, 끊김 알림, 화면의 모드별 부하 막대(누수 추적 자체가 짧게 끊기므로)
3. 쓰는 법: 메인 메뉴에서 모드를 **누수 추적**으로 바꿈 → 같은 맵 2판 → `BepInEx\RamCleaner\` 최신 로그와 세션 보고서를 보내 주세요. 로그의 `[leak]`·`[mod objects]` 줄에 무엇이 늘었는지 나옵니다.
4. 다 찾았으면 원래 모드로 돌아가세요. 다른 모드를 고르면 06·11은 다시 꺼집니다.

- **Ctrl+F9로 모드 바꾸기 + '꺼짐' 모드**

1. 전에는 Ctrl+F9가 '원인 추적 모드'만 켜고 껐고, 모드를 바꾸려면 F12나 웹 페이지에 들어가야 했습니다.
2. 이제 게임 중 **Ctrl+F9를 누를 때마다** 모드가 이 순서로 바뀝니다. 게임 알림에 지금 모드와 다음 모드가 나옵니다.
   - 자동 정리 → 간단 확인 → 집중 분석 → 누수 추적 → **꺼짐** → 자동 정리 …
   - '직접 설정' 상태에서 누르면 자동 정리부터 시작합니다.
3. **꺼짐**은 이 모드가 아무것도 하지 않는 상태입니다. 자동 정리, 경고, 화면 표시, 측정이 모두 멈춥니다(원래 게임과 같음). 웹 페이지와 단축키만 남아 있어서 Ctrl+F9로 다시 켤 수 있습니다.
4. 꺼짐에서 다시 켜면 워킹셋 정리 켜기/끄기는 꺼지기 전 값으로 돌아갑니다.
5. 집중 분석에서 다음 모드로 넘어가면 원인 추적이 꺼지면서 요약이 전용 로그에 저장됩니다(전과 같음).
6. 단축키는 F12 '05. 공통'의 **모드 바꾸기 단축키**에서 바꿀 수 있습니다(전 이름: 원인 추적 모드 단축키). F12 '수동 실행'에도 **[모드 바꾸기]** 버튼이 생겼습니다.

- **F12 글자 잘림 수정**

1. F12의 '현재 상태'와 '수동 실행' 버튼이 오른쪽 좁은 칸(약 270px)에 그려졌습니다. 특히 버튼 8개가 그 칸에 옆으로 나란히 끼어서 글자가 잘렸습니다.
2. 이제 이 두 칸은 이름 칸 없이 **창 전체 너비**를 쓰고, 버튼은 세로로 한 줄씩 놓이며, 긴 줄은 줄바꿈합니다. '현재 상태' 맨 위에 지금 모드도 나옵니다.
3. 왼쪽 이름 칸(약 260px)을 넘던 긴 설정 이름도 줄였습니다. 예: '의심 기준: 레이드 후에도 남는 관리 메모리 (MB)' → '의심 기준: 레이드 후 남는 메모리 (MB)'. 이름만 바뀌고 값은 그대로입니다.

- **VRAM을 그래픽카드별로 재기 (듀얼 GPU·Lossless Scaling) + 경고는 넘쳤을 때만**

1. 전에는 이 게임이 쓰는 VRAM을 모든 그래픽카드에서 더했습니다. 듀얼 GPU(게임은 1번 카드, Lossless Scaling 프레임 생성은 2번 카드)에서는 게임이 2번 카드에 둔 화면 복사용 메모리까지 더해져 숫자가 부풀 수 있었습니다.
2. 이제 'VRAM'은 **게임이 주로 쓰는 카드만** 셉니다. F12 '현재 상태', 웹 페이지, 로그에 아래가 따로 나옵니다.
   - 게임 그래픽카드 전체 사용량(모든 프로그램 합계)과 다른 프로그램 몫. 한 카드로 Lossless Scaling을 돌리면 그 몫이 여기 들어갑니다.
   - 다른 그래픽카드 사용량(Lossless Scaling을 돌리는 카드). 그래픽카드가 둘뿐이면 이름과 용량도 나옵니다.
   - 게임이 다른 카드에 둔 양(화면 복사용, VRAM 숫자에서는 뺌)
3. **VRAM 경고 기준이 바뀌었습니다.** 게임은 VRAM을 일부러 꽉 채워 쓰기도 해서 %만으로는 넘쳤는지 알 수 없습니다. 실제로 2026-10-10 로그에서는 97~99%가 계속 찍혔지만, 시스템 메모리로 넘친 양('공유')은 최대 0.38GB였습니다.
4. 이제 **카드 전체 사용량이 기준(95%) 이상이고, 넘친 양이 1GB 이상**일 때만 경고합니다. 새 F12 설정 '09 · **VRAM 넘침 기준 (GB)**'에서 바꿀 수 있고, 0이면 예전처럼 %만 봅니다.
5. 다른 그래픽카드의 이름과 크기는 Windows가 카드마다 붙이는 고유 번호(LUID)로 찾습니다. 옛 드라이버 흔적이나 내장 그래픽이 있어도 정확히 나옵니다.

- **누수 추적 때 1초 멈춤의 원인 표시 수정**

1. 누수 추적 기록(약 1초)과 모드 오브젝트 세기가 같은 프레임에 돌면 1초 멈춤이 '게임 자체'로 잘못 찍혔습니다. 이제 'RAM 클리너'로 나옵니다. 누수 추적 모드에서 5분마다 약 1초 멈추는 것은 그 모드의 원래 비용입니다.

- **알려진 문제 / 주의**

1. 이번 변경은 빌드와 테스트 환경(브라우저·Mono)에서만 확인했고, **실제 게임에서는 아직 확인하지 못했습니다.**
2. Alt+F4 때 '닫는 순간 저장'이 됐는지는 `BepInEx\LogOutput.log` 에서 `[report] saved the unfinished raid as the game closed` 줄로 알 수 있습니다. 이 줄이 없어도 1분마다 저장한 기록은 남습니다.
3. 런처 '모드 페이지'(`/ramcleaner/`)를 거쳐 지난 보고서를 열면 위 메뉴 없이 보고서만 보입니다. 뒤로 가기로 돌아오면 됩니다.
4. 그 밖의 주의 사항은 v2.12.0 릴리즈 노트와 같습니다.

- **제보 방법**

1. `BepInEx\RamCleaner\` 폴더의 최신 로그 파일 하나 + 가능하면 `BepInEx\LogOutput.log`
2. 보고서 문제라면 같은 폴더의 세션 보고서(`RamCleaner-report-*.html`)도 같이 보내 주세요.

^^7

------------------------------------------------------------------------------------------------------------------------------------------------------

# RAM Cleaner (RamCleanerInterval-zzap--Bootleg-) v2.13.0 release notes (vs v2.12.0)

> Original: CactusPie — SPT-RamCleanerInterval (GPL-3.0). This is an unofficial SPT 4.1 port; please don't contact the original author about it.
> A small update that makes the **session report easier to follow**. It fixes three user reports and one miscalculation found in a log, and adds a mode for hunting memory that piles up.

**At a glance**

1. **Report refreshes itself** — the web page's session report updates every 10 s during a raid. No more F5.
2. **History tab** — the raids of earlier game sessions stay viewable on the web page after you restart the game.
3. **Raids cut short are kept** — closing the game with Alt+F4 mid-raid, or a crash, no longer wipes that raid from the report.
4. **New mode "Leak hunt"** — one pick in the F12 "Mode" turns on what you need to find memory that piles up raid after raid.
5. **Ctrl+F9 switches the mode** — each press steps auto cleanup → quick view → deep analysis → leak hunt → **off**. No need to open F12 or the web page.
6. **F12 text no longer cut off** — "Current status" and the buttons use the full window width and long lines wrap.
7. **VRAM per graphics card** — dual-GPU setups (Lossless Scaling etc.) are measured per card, and the VRAM warning only fires when memory really spills over.

**Install**

- Extract the zip **straight into your SPT folder (e.g. `E:\SPT 4.1`)** and overwrite. Same locations and F12 setting values as v2.12.0. The F12 "Mode" gains **Leak hunt** and **Off**, Ctrl+F9 now switches the mode instead of toggling diagnostic mode, and a few long setting names got shorter (values unchanged).
- The optional server part for the launcher's mod pages (`SPT_Runtime\user\mods\RamCleanerInterval.Server\`) only changed its version number. Overwrite it as well.

---

**Changes**

- **Session report on the web page refreshes itself**

1. Before, the session report at `http://127.0.0.1:6977/` was built once when opened, so you had to press F5 to see the raid progress.
2. Now it refreshes **every 10 s during a raid** and every 30 s otherwise, and the charts are redrawn with the new numbers.
3. **Your scroll position and opened sections stay put.** While the tab is hidden it slows to every 30 s.
4. Untick **"Auto refresh"** above the report to stop it; the browser remembers the choice. A lost connection to the game shows on the same line.

- **History tab — raids from earlier sessions**

1. Before, restarting the game started the web report from a new session and earlier raids were not shown.
2. The menu now has **"History"**: it lists `BepInEx\RamCleaner\RamCleaner-report-*.html` (the latest 15 sessions) by start time; click one to open it.
3. The session report also has a **"Past sessions"** button at the top.

- **Raids cut short by Alt+F4 or a crash stay in the report**

1. Before, the report file was only saved when a raid **ended normally**, so closing the game mid-raid lost that raid entirely.
2. Now the raid in progress is saved **every minute** from 1 min in, and **once more the moment the game closes** during a raid.
3. Such raids are marked in the report:
   - **"game closed mid-raid · time"**: closed with Alt+F4 or similar
   - **"unfinished · recorded until time"**: a crash or a Task Manager kill, kept up to the last one-minute save
4. A raid played to the end is saved as usual, with no mark. Raids shorter than 1 minute are still not recorded.
5. Saving once a minute should cost next to nothing during a raid (not measured yet).

- **"Memory kept after the raid" fixed**

1. From the second raid on this could read negative, e.g. "-1.77 GB". It was compared with the heap at raid loading, which already held that load's temporary memory.
2. From the second raid it is now compared with the heap after the previous raid's cleanup, so what really piles up per raid shows (the report says e.g. "+0.99 GB vs after the last raid's cleanup").

- **New mode "Leak hunt"**

1. Before, deep analysis left "06. Leak tracker" and "11. Mod object growth" off: they hitch for 0.2–1 s every few minutes, which would blur deep analysis's stutter-cause measuring. So hunting memory that piles up meant switching them on by hand in F12.
2. Now picking **Leak hunt** in the F12 "Mode" turns on just what that hunt needs.
   - On: everything in quick view, memory created per mod (sampled every few minutes), 06 leak tracker, 11 mod object growth, the [experimental] heavy item finder, log every 60 s
   - Off: stutter-cause tracking per mod, stutter notifications, the per-mod cost bars on screen (the leak tracker's own snapshots hitch briefly)
3. How to use it: switch the mode to **Leak hunt** in the main menu → two raids on the same map → send the newest log in `BepInEx\RamCleaner\` and the session report. The `[leak]` and `[mod objects]` lines show what grew.
4. Switch back when you're done. Picking another mode turns 06 and 11 off again.

- **Ctrl+F9 switches the mode + "Off" mode**

1. Before, Ctrl+F9 only toggled diagnostic mode; changing the mode meant opening F12 or the web page.
2. Now **each press of Ctrl+F9** in game moves to the next mode, and an in-game notification names the current and the next one.
   - auto cleanup → quick view → deep analysis → leak hunt → **off** → auto cleanup …
   - From "custom" it starts at auto cleanup.
3. **Off** means this mod does nothing: automatic cleanup, warnings, overlay and measuring all stop (the plain game). Only the web page and the hotkey stay, so Ctrl+F9 turns it back on.
4. Leaving off puts the working-set trim switch back to what it was before.
5. Leaving deep analysis turns diagnostic mode off and saves its summary to the dedicated log (as before).
6. Change the key in F12 "05. General" → **Mode hotkey** (was: diagnostic mode hotkey). "Manual actions" in F12 also has a **[Switch mode]** button.

- **F12 text no longer cut off**

1. F12's "Current status" and "Manual actions" were drawn in the narrow right column (about 270 px); the eight buttons even sat side by side in it, so their labels were cut.
2. Both now use the **full window width** without the name column, the buttons stack one per line, and long lines wrap. "Current status" also shows the current mode at the top.
3. Setting names wider than the name column (about 260 px) were shortened, e.g. "Suspect at memory kept after a raid (MB)" → "Suspect: kept after raid (MB)". Only names changed; values are kept.

- **VRAM per graphics card (dual GPU · Lossless Scaling) + warning only on real spill**

1. Before, the game's VRAM was summed over every graphics card. In a dual-GPU setup (game on card 1, Lossless Scaling frame generation on card 2) the frame copies the game keeps on card 2 were added too, which could inflate the number.
2. "VRAM" now counts **only the card the game mainly uses**. F12 "Current status", the web page and the log also show:
   - the whole game card's use (all programs) and the other programs' share. Running Lossless Scaling on the same card puts its share here.
   - the other card's use (the one running Lossless Scaling), with name and size when there are only two cards
   - what the game keeps on the other card (frame copies, left out of the VRAM number)
3. **The VRAM warning changed.** Games may fill VRAM on purpose, so the % alone can't tell whether it overflowed. The 2026-10-10 log read 97–99% all along, yet at most 0.38 GB spilled into system memory ("shared").
4. It now warns only when **the whole card is at or above the threshold (95%) and at least 1 GB has spilled**. Set it in the new F12 setting "09 · **VRAM spill at (GB)**"; 0 = the % alone, as before.
5. Other cards' names and sizes are now looked up by the ID Windows gives each card (LUID), so they show even with leftover driver entries or integrated graphics.

- **Leak-hunt 1 s freezes now blamed correctly**

1. When the leak snapshot (~1 s) and the mod-object count ran in the same frame, the freeze was logged as 'the game itself'. It now shows as the RAM cleaner. A ~1 s freeze every 5 minutes is the normal cost of Leak hunt mode.

- **Known issues / notes**

1. These changes were checked in a build and test setup (browser, Mono) only — **not in the real game yet.**
2. Whether the save on Alt+F4 worked shows as the line `[report] saved the unfinished raid as the game closed` in `BepInEx\LogOutput.log`. Without that line the one-minute saves are still there.
3. Opening a past report through the launcher's mod page (`/ramcleaner/`) shows the report without the menu bar; use Back to return.
4. Everything else is as in the v2.12.0 release notes.

- **How to report**

1. The newest log file in `BepInEx\RamCleaner\` + `BepInEx\LogOutput.log` if you can.
2. For report problems, also send the session report (`RamCleaner-report-*.html`) from the same folder.

^^7

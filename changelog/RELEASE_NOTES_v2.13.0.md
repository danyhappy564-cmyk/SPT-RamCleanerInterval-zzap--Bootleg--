# RAM 클리너 (RamCleanerInterval-zzap--Bootleg-) v2.13.0 릴리즈 노트 (v2.12.0 대비)

> 원작: CactusPie — SPT-RamCleanerInterval (GPL-3.0). 이 버전은 비공식 SPT 4.1 포팅입니다. 문제가 생겨도 원작자에게 문의하지 말아 주세요.
> 이번 버전은 **세션 보고서를 더 편하게 보기** 위한 작은 업데이트입니다. 사용자 제보 3건을 고쳤습니다.

**<한눈에 보기>**

1. **보고서 자동 새로고침** — 웹 페이지 '세션 보고서'가 레이드 중 10초마다 저절로 바뀝니다. 이제 F5를 누를 필요가 없습니다.
2. **'지난 기록' 탭** — 게임을 다시 켜도 이전 세션들의 레이드 기록을 웹 페이지에서 볼 수 있습니다.
3. **강제 종료한 판도 기록** — 레이드 도중 Alt+F4로 끄거나 게임이 튕겨도 그 판이 보고서에 남습니다.

**<설치>**

- zip을 **SPT 폴더(예: `E:\SPT 4.1`)에 그대로 풀어서 덮어쓰면** 됩니다. 들어가는 위치와 F12 설정은 v2.12.0과 같고, 새로 생기는 F12 항목은 없습니다.
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
> A small update that makes the **session report easier to follow**. It fixes three user reports.

**At a glance**

1. **Report refreshes itself** — the web page's session report updates every 10 s during a raid. No more F5.
2. **History tab** — the raids of earlier game sessions stay viewable on the web page after you restart the game.
3. **Raids cut short are kept** — closing the game with Alt+F4 mid-raid, or a crash, no longer wipes that raid from the report.

**Install**

- Extract the zip **straight into your SPT folder (e.g. `E:\SPT 4.1`)** and overwrite. Same locations and F12 settings as v2.12.0; no new F12 entries.
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

- **Known issues / notes**

1. These changes were checked in a build and test setup (browser, Mono) only — **not in the real game yet.**
2. Whether the save on Alt+F4 worked shows as the line `[report] saved the unfinished raid as the game closed` in `BepInEx\LogOutput.log`. Without that line the one-minute saves are still there.
3. Opening a past report through the launcher's mod page (`/ramcleaner/`) shows the report without the menu bar; use Back to return.
4. Everything else is as in the v2.12.0 release notes.

- **How to report**

1. The newest log file in `BepInEx\RamCleaner\` + `BepInEx\LogOutput.log` if you can.
2. For report problems, also send the session report (`RamCleaner-report-*.html`) from the same folder.

^^7

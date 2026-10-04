# RAM 클리너 (RamCleanerInterval-zzap--Bootleg-) v2.11.0 릴리즈 노트 (v2.10.0 대비)

> 원작: CactusPie — SPT-RamCleanerInterval (GPL-3.0). 이 버전은 비공식 SPT 4.1 포팅입니다. 문제가 생겨도 원작자에게 문의하지 말아 주세요.
> 게임 쪽 메모리·끊김을 다루는 모드입니다. 이번 버전은 **"왜 끊기는지, 언제 메모리가 바닥나는지"를 미리 보여 주는 기능**을 추가했습니다.

**<설치>**

- **v2.10.0 이하에서 올라오는 경우:** `BepInEx\plugins\CactusPie.RamCleanerInterval\` 의 DLL을 덮어쓰면 됩니다. 기존 F12 설정은 그대로 유지되고, 14~17번 항목이 새로 생깁니다.
- 서버 모드는 필요 없습니다. 서버 쪽 최적화 모드(CompoundingPerf)와 같이 써도 되고 따로 써도 됩니다.

---

**<변경점>**

- **SPT 서버 상태 보기 (F12 14번, 기본 켜짐)**

1. 화면 왼쪽 위 표시에 **'SPT 서버'** 칸이 생겼습니다. 서버 프로그램이 쓰는 메모리가 나옵니다. 서버도 같은 PC의 RAM을 같이 쓰기 때문에, "시스템 여유 RAM이 적은데 게임은 멀쩡한" 경우를 바로 구분할 수 있습니다.
2. 레이드 중 **봇 생성 응답 시간**이 나옵니다. 게임이 서버에 봇을 요청하고 받기까지 걸린 시간이라, 이게 몇 초씩 걸리면 스폰이 늦는 이유가 서버 쪽입니다(봇 장비 모드에 아이템이 많을수록 늘어남).
3. 어떤 모드가 서버에 "대답을 기다리는" 방식으로 물어보면 그동안 게임 전체가 멈춥니다. 이제 이런 끊김을 **"서버 응답 대기 /orbit/config"** 처럼 주소와 함께 보여 줍니다. 주소 앞부분이 모드 이름입니다(`/sain/…`, `/orbit/…`, `/botplacementsystem/…` 등).

- **메모리 예측 (F12 15번, 기본 켜짐)**

1. 레이드 중 **"여유 예상: 약 35분 뒤 커밋 한도(튕김) · 봇 약 120명 더 죽으면 한도"** 처럼 메모리가 바닥나기까지 남은 시간을 보여 줍니다. 최근 10분 동안 줄어든 속도로 계산합니다(레이드 시작 3분 뒤부터).
2. 남은 시간이 15분보다 짧아지면 빨간색으로 바뀌고 게임 알림이 한 번 뜹니다. 긴 시뮬·긴 레이드에서 언제 마무리할지 정하는 데 쓰면 됩니다.
3. 레이드가 끝나고 메뉴로 오면 **"재시작 판단: 약 2판 더 가능, 그 뒤 재시작 권장"** 이 나옵니다. 레이드를 반복할수록 게임이 놓지 못하고 쌓이는 메모리를 보고 계산합니다(2판째부터).

- **세션 보고서 (F12 16번, 기본 켜짐)**

1. 레이드가 끝날 때마다 `BepInEx\RamCleaner\RamCleaner-report-날짜.html` 파일이 갱신됩니다. 브라우저로 여는 그래프 페이지입니다.
2. 판별 요약 표(FPS·끊김·최고 메모리·사망당 메모리), 판마다 메모리·FPS 그래프(마우스를 올리면 값이 보임), 끊김 원인, 재시작 판단이 들어갑니다.
3. F12 → 05번의 **'세션 보고서 열기'** 버튼으로 바로 열 수 있습니다. 인터넷 연결은 필요 없습니다.

- **[실험] 무거운 모드 아이템 찾기 (F12 17번, 기본 꺼짐)**

1. 봇이 스폰될 때 장비(총·방어구 등)를 처음 불러오면서 늘어난 메모리를 재서 **"어느 모드의 어느 아이템이 메모리를 많이 먹는지"** 순위를 만듭니다.
2. "사망 1명당 메모리"가 클 때, 봇 장비 모드(APBS 등)에서 뺄 아이템을 고르는 참고용입니다.
3. 실험 기능이라 숫자 하나하나는 정확하지 않을 수 있습니다. 여러 판에서 계속 위에 오는 것만 참고해 주세요.

- **알려진 문제 / 주의**

1. 이번에 추가된 기능들은 아직 긴 테스트 레이드를 거치지 않았습니다. 표시가 이상하거나 끊김이 늘면 해당 번호(14~17)를 꺼 주세요.
2. 17번(실험)은 실제 게임에서 처음 돌아가는 기능입니다. 켰을 때 문제가 생기면 꺼 두고 알려 주세요.
3. 'SPT 서버' 칸이 계속 "프로세스 찾는 중"이면 서버 프로그램 이름이 달라서 못 찾는 경우입니다. 로그를 보내 주세요.
4. maschine-ModProfiler(F10)와 같이 쓰는 건 여전히 권장하지 않습니다(처음 켤 때 큰 멈춤).

- **제보 방법**

1. `BepInEx\RamCleaner\` 폴더의 최신 로그 파일 하나 + 가능하면 `BepInEx\LogOutput.log`
2. 여러 판을 비교한 문제라면 같은 폴더의 세션 보고서(`RamCleaner-report-*.html`)도 같이 보내 주세요.

^^7

------------------------------------------------------------------------------------------------------------------------------------------------------

# RAM Cleaner (RamCleanerInterval-zzap--Bootleg-) v2.11.0 release notes (vs v2.10.0)

> Original: CactusPie — SPT-RamCleanerInterval (GPL-3.0). This is an unofficial SPT 4.1 port; please don't contact the original author about it.
> This mod handles the game side's memory and stutter. This release adds **features that show why the game stutters and when memory will run out, before it happens**.

**Install**

- **Coming from v2.10.0 or older:** overwrite the DLL in `BepInEx\plugins\CactusPie.RamCleanerInterval\`. Your F12 settings are kept; sections 14–17 are new.
- No server mod needed. Works with or without the server-side optimisation mod (CompoundingPerf).

---

**Changes**

- **SPT server status (F12 section 14, on by default)**

1. The top-left overlay has a new **"SPT 서버" (SPT server)** block with the server process's memory. The server shares your PC's RAM, so "system RAM is low but the game looks fine" is now easy to spot.
2. In raid it shows **bot generation response time** — how long the server takes to hand over the bots the game asked for. Seconds here mean spawns are late because of the server side (it grows with the number of items in bot gear mods).
3. When a mod asks the server something and *waits* for the answer, the whole game freezes meanwhile. Those stutters are now labelled **"서버 응답 대기 /orbit/config" (waiting on the server, with the URL)**; the start of the URL tells you the mod (`/sain/…`, `/orbit/…`, `/botplacementsystem/…`, …).

- **Memory forecast (F12 section 15, on by default)**

1. In raid: **"about 35 min until the commit limit (crash) · about 120 more bot deaths"** — time left before memory runs out, from how fast it dropped over the last 10 minutes (from 3 minutes into the raid).
2. Under 15 minutes the line turns red and one in-game notification appears. Use it to decide when to wrap up a long sim or raid.
3. Back in the menu after a raid: **"about 2 more raids, then restart"** — from the memory the game keeps piling up raid after raid (from the second raid on).

- **Session report (F12 section 16, on by default)**

1. After every raid `BepInEx\RamCleaner\RamCleaner-report-<date>.html` is rewritten — a chart page for your browser.
2. It has a per-raid table (FPS, stutters, peak memory, memory per death), memory and FPS charts per raid (hover for values), stutter causes and the restart advice.
3. Open it with the **"세션 보고서 열기" (open session report)** button in F12 section 05. No internet needed.

- **[Experimental] Find heavy mod items (F12 section 17, off by default)**

1. Measures how much memory each piece of bot gear (guns, armour, …) adds when it is loaded for the first time, and ranks **which mod and which item cost the most memory**.
2. Meant as a guide for what to remove from a bot gear mod (APBS etc.) when "memory per death" is high.
3. Experimental: single numbers can be off. Trust only what stays at the top across several raids.

- **Known issues / notes**

1. The new features have not been through long test raids yet. If something looks wrong or stutters increase, turn off that section (14–17).
2. Section 17 runs in the real game for the first time with this release. If it causes trouble, switch it off and let me know.
3. If the "SPT 서버" block keeps saying "looking for the process", the server program has a different name. Please send the log.
4. Using maschine-ModProfiler (F10) at the same time is still not recommended (big freeze when it first turns on).

- **How to report**

1. The newest log file in `BepInEx\RamCleaner\` + `BepInEx\LogOutput.log` if you can.
2. For problems across several raids, also send the session report (`RamCleaner-report-*.html`) from the same folder.

^^7

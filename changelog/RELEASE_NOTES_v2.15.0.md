# RAM 클리너 (RamCleanerInterval-zzap--Bootleg-) v2.15.0

**태그:** `v2150` (master `de5a02a`)

**원작:**

1. **CactusPie** — SPT-RamCleanerInterval
   - Repository: [CactusPie/SPT-RamCleanerInterval](https://github.com/CactusPie/SPT-RamCleanerInterval)
   - License: GPL-3.0
2. **matsix** — SPTVRAMCleaner ('레이드 시작 때 에셋 정리' 기능의 바탕)
   - Repository: [matsixx/SPTVRAMCleaner](https://github.com/matsixx/SPTVRAMCleaner)
   - License: GPL-3.0
3. **TominoCZ** — Smoothie (19번 게임 메모리 고침의 아이디어만 참고, 코드는 새로 작성)
   - Repository: [TominoCZ/Smoothie](https://github.com/TominoCZ/Smoothie)
   - License: CC BY-NC-SA 4.0

**이전 릴리즈:** v2.14.0 (GitHub 릴리즈 `v2140`, 2026-10-14, 그 뒤 커밋 4개) → **이번: v2.15.0** (기능 추가)

**<변경점>**

**1. 누수 추적 모드: '누수 범인 후보' 순위를 화면과 보고서에 바로 표시**
- 전에는: 메모리가 판마다 쌓일 때 근거가 따로따로였습니다 — 레이드 후 붙잡은 모드 검사(`[leak holders]`, 로그), 모드별 오브젝트 증가(11번, 로그), 모드별 메모리 생성량(막대), 봇 장비 메모리(17번). 어느 모드가 범인인지는 사람이 로그를 맞춰 봐야 했습니다.
- 이제: 네 가지를 모드별로 합쳐 판정과 순위를 매깁니다.
  - **누수 확실**(빨강): 레이드가 끝난 뒤에도 그 모드가 지난 레이드의 봇·시체·플레이어를 붙잡고 있음 — 게임을 끌 때까지 그 메모리가 안 풀림.
  - **누수 의심**(노랑): 레이드 중 그 모드의 오브젝트가 계속 늘어남(11번 기준 이상), 또는 메모리 생성량이 레이드 초반보다 크게 늘어남.
  - **참고: 많이 만듦**(파랑): 메모리를 많이 만들지만 대부분 곧 치워짐. **참고: 봇 장비**(회색): 봇 장비 아이템이 많아서 처음 로드 때 메모리가 큼 — 누수 아님.
- 나오는 곳: 왼쪽 위 화면(빨간 제목 '누수 범인 후보'), 세션 보고서·웹 '세션 보고서' 맨 위 표, F12 '현재 상태', 레이드 결산 알림, 레이드 끝난 뒤 메뉴 알림, 로그 `[leak culprits]`.
- 누수 추적 모드(Ctrl+F9로 바꿈)에서는 레이드가 끝날 때마다 메뉴에서 '붙잡은 모드' 검사를 합니다(전에는 남은 메모리가 기준을 넘을 때만). 최대 4초, 메뉴에서만.
- 한계: 아주 깊숙이 붙잡은 것이나 게임 쪽 데이터를 통해 붙잡은 것은 못 찾습니다. "없음"이 무죄라는 뜻은 아닙니다. 같은 맵 2판 이상 돌린 뒤 보는 게 정확합니다.
- 확인 상태: 가짜 데이터로 판정·순위·보고서 표 확인(Mono + Chromium). 게임 미검증.

**2. F12 '19. 게임 자체 메모리 낭비 고치기' (새 분류, 3개 모두 기본 켜짐)**
- 게임 코드(4.1 클라이언트)를 직접 열어 확인한, 게임이 필요 없는 메모리를 붙잡거나 매 프레임 쓰레기를 만드는 곳 3군데입니다. 게임 동작은 그대로이고, 맨 위 '모드'가 '꺼짐'이면 같이 꺼집니다.
- **레이드 끝난 봇 스폰 기록 놓아주기:** 봇 스폰 시도(실패 포함)마다 그 봇들 프로필이 게임 전체에 살아 있는 이벤트 목록에 걸리는데, 레이드가 끝날 때 게임이 이것만 안 비워서 판이 쌓일수록 남았습니다. 레이드가 끝나는 순간 그 목록만 비웁니다(레이드 중엔 무변경). 로그 `[fix] raid end: let go of N spawn records`.
- **죽은 봇 AI 목록 비우기:** 죽은 봇의 AI를 빼는 '뺄 목록'을 게임이 한 번도 안 비워서 죽은 봇 전부가 레이드 끝까지 붙잡혀 있었습니다. 게임이 정리를 마친 직후, 이미 빠진 것만 목록에서 지웁니다.
- **봇마다 매 프레임 생기는 쓰레기 없애기:** 게임이 매 프레임 봇마다 작은 작업(코루틴)을 새로 만드는데 대부분 할 일이 없습니다(봇 30명·120fps면 초당 3,600개, 레이드 중엔 GC가 꺼져 쌓임). 그 작업이 할 일이 없을 때만 만들지 않고, 나머지는 게임대로 둡니다. 내 캐릭터는 안 건드립니다.
- 이 게임 버전과 안 맞으면 그 항목만 꺼지고 로그에 `[fix] ...: skipped`가 남습니다.
- 확인 상태: 대상 게임 코드와 하나씩 대조, 빌드만 확인. 게임 미검증.

**3. 웹 페이지 '잡동사니·그래픽' 탭 (Tyrian-DeClutterer 연동)**
- Tyrian-DeClutterer(zzap 2.1.0 이상)가 같이 깔려 있으면 웹 페이지 위쪽에 탭이 생깁니다(SPT 런처의 RAM Cleaner 모드 페이지에서도 같음).
- 그 모드의 F12 설정 전체를 브라우저에서 바꿀 수 있고 F12와 똑같이 동작합니다. 현재 상태(레이드 중인지·숨긴 물체 수·로드 때 뺀 데칼 수·프리셋)가 3초마다 보이고, 언어는 RAM 클리너를 따라갑니다.
- 확인 상태: 게임 없이(Mono) 읽기·쓰기·프리셋 적용·범위 보정 시험. 게임 미검증.

**4. 기타**
- '서버 최적화' 탭의 안내 문구: CompoundingPerf 2.3.0의 게임 쪽 항목(09번)은 서버로 보내지 않는다는 내용을 넣었습니다.
- F12 '모드' 설명의 '누수 추적'에 범인 후보 표시 안내 추가.
- 버전: F12 모드 버전·서버 부품 버전·zip 이름(`RamCleanerInterval-2.15.0-SPT4.1.zip`) 2.15.0.

**알려진 점**
- 1~3번 모두 게임 미검증입니다. 19번 항목이 이상하면 F12에서 그 항목만 끄면 원래대로 돌아갑니다.
- 누수 추적 모드에서 레이드 뒤 메뉴가 잠깐(최대 4초) 멈출 수 있습니다(붙잡은 모드 검사).
- 나머지는 v2.14.0 릴리즈 노트와 같습니다.

**설치 / 업데이트**
- `RamCleanerInterval-2.15.0-SPT4.1.zip` 압축을 풀어 SPT 폴더(예: `E:\SPT 4.1`)에 덮어쓰기. 설정은 그대로 유지됩니다. 서버 부품(런처 '모드 페이지'용)도 같이 덮어쓰면 됩니다.
- 배포 zip은 제작자 PC에서 실제 게임 파일로 빌드한 것입니다.

------------------------------------------------------------------------------------------------------------------------------------------------------

**<Changes (English)>**

**1. Leak hunt: a ranked "leak culprits" list in the overlay and the reports**
- Before: the evidence was spread out — the after-raid holder scan (`[leak holders]`), per-mod object growth (11), per-mod memory creation, bot gear memory (17). Matching them up was left to you.
- Now: the four are joined per mod into a verdict and a ranking — **leaking** (red: still held the finished raid's bots/corpses/players after it ended; that memory stays until the game closes), **suspect** (yellow: its objects or memory creation kept growing during a raid), **hint/info** (creates a lot but it is mostly freed soon / many bot gear items, not a leak).
- Shown in the top-left overlay, at the top of the session report and the web report, in F12 status, in the raid report notice, in the after-raid menu notice and as `[leak culprits]` in the log.
- In leak hunt mode the holder scan now runs after every raid (before: only above the kept-memory threshold). 4 s at most, menu only.
- Limits: deeper references or ones held through game data are not found; "none" does not clear a mod. Most reliable after 2+ raids on the same map.
- Status: ranking and report table tested with fake data (Mono + Chromium). Untested in game.

**2. F12 "19. Game memory fixes" (new, all three on by default)**
- Three spots in the 4.1 client where the game keeps memory it no longer needs or makes garbage every frame; game behaviour is unchanged, and the "off" mode turns them off too.
- **Spawn records after the raid:** every spawn attempt's record (with the bots' profiles) stayed on a session-long event list; it is cleared the moment the raid ends (nothing changes during a raid). Log: `[fix] raid end: let go of N spawn records`.
- **Dead bots' AI:** the game never emptied its "to remove" list, holding every dead bot's AI until the raid ended; entries already removed are now dropped right after the game's own update.
- **Per-bot coroutine garbage:** a small job was created for every bot every frame, mostly with nothing to do; it is now skipped only when it would do nothing. Your own character is untouched.
- A fix whose target is missing in this game build is skipped and logged (`[fix] ...: skipped`).
- Status: targets checked against the game code, built only. Untested in game.

**3. "DeClutter" web tab (Tyrian-DeClutterer integration)**
- With Tyrian-DeClutterer (zzap 2.1.0+) installed, the web page gets a tab with all of its F12 settings (same behaviour as F12) and a status box refreshed every 3 s; the language follows RAM Cleaner.
- Status: tested without the game (Mono). Untested in game.

**4. Other**
- "Server optimisation" tab notice now says CompoundingPerf 2.3.0's game-side items (09) stay in the game.
- Leak hunt mode description mentions the culprit list.
- Version 2.15.0 everywhere (F12, server part, zip name).

**Known issues**
- Items 1–3 are untested in game. Switch a section 19 item off in F12 to get the old behaviour back.
- In leak hunt mode the menu may pause briefly (up to 4 s) after a raid for the holder scan.

**Install / update**
- Extract `RamCleanerInterval-2.15.0-SPT4.1.zip` into your SPT folder and overwrite. Settings are kept. Overwrite the optional server part too.

------------------------------------------------------------------------------------------------------------------------------------------------------

**!!!!!!!!!!!PLEASE READ THIS!!!!!!!!!!!**

Hi everyone,

I'd like to share a few thoughts and important warnings regarding the process of porting mods from version 4.0 to 4.1.

Following the recent "ECOT" mod incident, I've had a lot of time to reflect on my actions.
I’m not here to lecture anyone—I’m writing this simply in the hope that you’ll keep these points in mind.


**1. I am not a professional programmer; I am mostly a "vibe coder" who relies heavily on AI.**

By that, I mean I’m not someone who spends days designing and painstakingly crafting mods by hand from scratch.
At the same time, having tinkered with simple mod edits back in the TS/JS (SPT 2.3.1) days, I know firsthand how much time, effort, and care goes into this process.
Therefore, even if a mod is covered under an MIT License, if the original author requests that I delete the source code and stop distribution, I will willingly comply.


**2. Please DO NOT re-distribute or re-upload these modified mods to 3rd/4th party places.**

Even if my ported version has some fixes or improvements, please do not treat it as a long-term solution. Unlike official releases on "THE FORGE," I do not perform thorough debugging or full security scans.
Furthermore, imagine how disruptive it would be if someone mistook my ported build for an "official release" and pestered the original author about bugs in my version. It would be terrible.
Moving forward, I intend to approach any porting work with the utmost respect for original modders.
If I had spent days pouring my heart into making a mod, only to have someone take my code and modify it however they pleased, I wouldn't feel good about it either.
Therefore, when writing README.md files on GitHub, I will always credit the original creators, and if it's not a mod I built from scratch, I will fully respect the original modder's decisions and opinions.


**3. Please respect the original creators.**

If people ignore warning 2. and act as if my port is the "real deal," I would feel immense guilt.
Please do not do that. As the saying goes, "When in Rome, do as the Romans do"—and showing proper respect and courtesy is essential.
Instead, please visit the original creators' comment sections on The Forge to leave warm words of appreciation, encouragement, or even buy them a coffee/leave a tip if you can


**Thank you for reading this long post.**
**Stay safe and stay happy!**

**^^7**

------------------------------------------------------------------------------------------------------------------------------------------------------

**!!!!!!!!!!!꼭 읽어주세요!!!!!!!!!!**

안녕하세요 여러분

4.0 -> 4.1로 포팅하는 과정에 대해 드릴 말씀과 주의사항을 전달하고자 합니다.

이번 "ECOT" 모드 사건 이후로, 많은 반성의 계기를 가지게 되었습니다

뭔가, 설교를 드리는게 아니라 참고 해주셨으면 하는 마음에 글을 작성합니다.


**1. 저는 전문 프로그래머가 아니고, AI를 주로 이용하는 바이브코더입니다.**

해당말의 뜻은, 저는 매일 구상하고 힘들게 한땀한땀 손으로 모드를 만드는 사람이 아니고

또한, TS/JS (SPT 2.3.1) 시절에 간단하게 모드 수정을 거치며 이 과정이 얼마나 노력을 들이고, 시간이 필요한지 잘 알고 있습니다.

그렇기에, 해당 모드를 수정하면서 아무리 MIT 라이선스를 받았더라도 원 모드 제작자가 소스코드 삭제 및 배포를 중단해달라고 하면 중단할 의향이있습니다.


**2. 무단으로 수정한 모드를 "3차 4차 배포하지 마십쇼"**

제가 만든 버전이 개선점이 있어도, 오래 사용하지 마세요 저는 "FORGE" 에 올린 것 처럼 바이러스 검사나 디버깅을 완벽하게 수행하지 않습니다.

또한, 제가 포팅한 버전을 "진짜 릴리즈된 버전" 이라고 생각하고, 제 포팅 모드를 들고 원작자에게 가면 얼마나 민폐일까요? 끔찍합니다

저는 모더들을 최대한 존중하는 방향으로 포팅할 계획입니다.

아마, 제가 모드를 제작할 때 하루를 갈아넣어 만들었는데 누군가가 멋대로 코드를 뺏겨 지 멋대로 작성했다면 기분을 썩 좋지않을 것 같습니다.

고로 GITHUB에 README.MD 작성 시, 원작자 표기 및 제가 실제로 제작하지 않은 모드라면 최대한 해당 모드를 제작한 모더의 의견을 존중하는 방향으로 나아갈 것입니다.

**3. 원작자를 존중해주세요**

만일 2번 주의사항을 어겼을 시, 제가 포팅한 모드가 "진짜 모드다!" 이런 느낌이면 진짜 죄책감에 휩싸일 것 같습니다.

그런 짓은 하지말아주고, 로마에 왔으면 로마법을 지키듯이 한국에서는 예의를 갖추는 것이 미덕입니다.

해당 모드 제작자의 FORGE COMMENT 창에 들어가 따뜻한 위로와 감사하다는 격려 또는 그들에게 TIP을 건네주세요


**긴 글 읽어주셔서 감사합니다.** 

**행복하세요! ^^7**

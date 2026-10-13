# RAM 클리너 (RamCleanerInterval-zzap--Bootleg-) v2.14.0

**태그:** `v2140` (master `d98fc71`)

**원작:**

1. **CactusPie** — SPT-RamCleanerInterval
   - Repository: [CactusPie/SPT-RamCleanerInterval](https://github.com/CactusPie/SPT-RamCleanerInterval)
   - License: GPL-3.0
2. **matsix** — SPTVRAMCleaner ('레이드 시작 때 에셋 정리' 기능의 바탕)
   - Repository: [matsixx/SPTVRAMCleaner](https://github.com/matsixx/SPTVRAMCleaner)
   - License: GPL-3.0

**이전 릴리즈:** v2.13.0 (GitHub 릴리즈 `v2130`, 2026-10-10, 그 뒤 커밋 4개) → **이번: v2.14.0** (기능 추가)

**<변경점>**

**1. 레이드가 끝났는데도 메모리가 안 풀릴 때, 붙잡고 있는 모드 이름까지 찾아 줌 (`[leak holders]`)**
- 전에는: 레이드가 끝나고 정리(GC)를 해도 게임 메모리가 기준(기본 500MB)보다 많이 남으면 "어떤 모드가 지난 레이드 데이터를 붙잡고 있음"까지만 알려 줬습니다. 어느 모드인지는 사람이 추측해야 했습니다.
- 이제: 그 순간(메인 메뉴, 정리 직후) 설치된 모드들의 정적 필드(모드가 레이드가 끝나도 계속 들고 있는 변수)를 훑어서, **이미 사라진 지난 레이드 오브젝트(플레이어·봇·시체 등)를 아직 가리키고 있는 곳**을 찾습니다. 찾으면 모드 이름과 필드 이름을 로그에 `[leak holders]` 줄로 적습니다.
- 알림과 웹 페이지·F12의 '레이드 후 남은 메모리' 문구에도 `— 지난 레이드 오브젝트를 붙잡은 모드: 모드이름` 이 붙습니다.
- 부담: 메뉴에서만, 의심 기준을 넘었을 때만 돌고, **최대 4초**에서 멈춥니다(컬렉션은 5만 개까지만, 깊이 1단계). 레이드 중에는 돌지 않습니다.
- 한계: 아무것도 못 찾았다고 그 모드가 무죄라는 뜻은 아닙니다. 더 깊이 숨은 참조나 게임 자체가 붙잡은 것은 못 봅니다.
- 확인 상태: **빌드·코드 검토만 했고 게임에서는 아직 확인하지 못했습니다.**
- 쓰는 법: 평소처럼 레이드를 2~3판 한 뒤 `BepInEx\RamCleaner\` 최신 로그의 `[leak holders]` 줄과 `[mem suspect]` 줄을 보내 주세요(누수 추적 모드면 `[leak]` 줄도 같이 도움이 됩니다).

**2. 버전 표시 2.14.0으로 통일**
- F12 창의 모드 버전, 서버 부품(런처 '모드 페이지') 버전, 빌드가 만드는 zip 이름(`RamCleanerInterval-2.14.0-SPT4.1.zip`)이 모두 2.14.0입니다.

**3. 기타**
- README 맨 위 원작자 표기를 원작마다 번호를 붙여 층을 나눴습니다. 변경 이력은 10개씩 `changelog/` 폴더로 나눴습니다(기능 변화 없음).

**알려진 점**
- 1번은 게임 미검증입니다. 레이드 후 메뉴에서 이상하게 멈추거나 로그에 `[leak holders] scan failed` 가 보이면 알려 주세요. 스캔이 실패해도 나머지 기능은 그대로 동작합니다.
- 나머지는 v2.13.0 릴리즈 노트와 같습니다.

**설치 / 업데이트**
- `RamCleanerInterval-2.14.0-SPT4.1.zip` 압축을 풀어 SPT 폴더(예: `E:\SPT 4.1`)에 덮어쓰기. 설정은 그대로 유지됩니다. 런처 '모드 페이지'용 서버 부품도 같이 덮어쓰면 됩니다(버전 숫자만 바뀜).

------------------------------------------------------------------------------------------------------------------------------------------------------

**<Changes (English)>**

**1. After a raid, the mod holding on to the old raid's memory is now named (`[leak holders]`)**
- Before: if managed memory stayed above the threshold (default 500 MB) after the post-raid GC, you only got "some mod is holding on to the last raid's data". Which mod was left to guesswork.
- Now: at that moment (main menu, right after the GC) the static fields of installed mods are scanned for references to **destroyed objects of the last raid (players, bots, corpses...)**. Mod and field names go to the log as a `[leak holders]` line.
- The notification, web page and F12 "memory kept after the raid" text also gain `— mods holding last raid's objects: <mod>`.
- Cost: runs only in the menu and only above the suspect threshold, stops at **4 s at most** (collections capped at 50,000 elements, depth 1). Never during a raid.
- Limits: finding nothing does not clear a mod; deeper references and things the game itself holds are not seen.
- Status: **built and code-reviewed only, not yet checked in game.**
- How to use: play 2–3 raids as usual, then send the `[leak holders]` and `[mem suspect]` lines from the newest log in `BepInEx\RamCleaner\`.

**2. Version shown as 2.14.0 everywhere**
- F12 mod version, the server part's version, and the zip name the build produces (`RamCleanerInterval-2.14.0-SPT4.1.zip`).

**3. Other**
- README author credits split per original work, change history split into `changelog/` (no functional change).

**Known issues**
- Item 1 is untested in game. If the menu hitches after a raid or the log shows `[leak holders] scan failed`, please report it; the scan failing does not affect anything else.
- Everything else is as in the v2.13.0 release notes.

**Install / update**
- Extract `RamCleanerInterval-2.14.0-SPT4.1.zip` into your SPT folder (e.g. `E:\SPT 4.1`) and overwrite. Settings are kept. Overwrite the optional server part too (version number only).

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

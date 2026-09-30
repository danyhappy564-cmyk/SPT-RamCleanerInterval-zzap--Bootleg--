### ⚠️ IMPORTANT NOTICE / DISCLAIMER

**Original Author:** CactusPie
**Original Repository:** SPT-RamCleanerInterval
**Original Link:** https://github.com/CactusPie/SPT-RamCleanerInterval
**License:** GNU GPL v3.0 (see `LICENSE`) — this port stays under the same license.
**Merged Feature Author:** matsix — SPTVRAMCleaner (https://github.com/matsixx/SPTVRAMCleaner), GNU GPL v3.0.
The "raid start asset unload" feature is based on that mod and reworked here.
**This Port By:** R_F (danyhappy564-cmyk) — unofficial, AI-assisted port. Not affiliated with or endorsed by the original author.

1. **Reflection & Take-Downs:** I deeply reflect on the ECOT incident. As an AI-assisted "vibe coder," I will immediately delete files if the original authors ask.
2. **No Re-Distribution:** These ported builds are unverified, temporary fixes. Please do NOT re-upload or share them anywhere else.
3. **Do Not Pester Original Authors:** Never report bugs or pester original modders regarding issues from my unofficial ports.
4. **Full Credit & Respect:** I will always credit original creators on GitHub and prioritize their decisions above all else.
5. **Support Original Creators:** Instead of using my ports, please visit the original authors' Forge pages to leave kind words or tips.

---

## 변경 이력

- 2026-09-30 19:11 (KST) — **v2.2.1: 세 번째 로그(누수 추적) 분석 + 추적 항목 추가**
  - 결과: 메모리 증가는 **죽은 봇 수와 거의 정비례** — 25분 동안 사망 94명, 네이티브 +26GB(1명당 약 280MB), 텍스처 메모리 +8.8GB.
    살아 있는 봇은 계속 약 10명 유지. 즉 "새는" 게 아니라 **죽은 봇의 몸·장비·무기가 레이드 내내 남아서** 쌓이는 것일 가능성이 큼
  - 20분 동안 늘어난 오브젝트: GameObject +19.7만 개. 이름은 팔·손가락 뼈(`Base HumanLDigit` 등), 탄약(`patron`), 총구 화염(`muzzleflash`),
    무기 애니메이션 부품 — 전부 봇의 몸과 들고 있는 물건(무기·약·수류탄)
  - 추가: 누수 추적 로그에 **어느 최상위 오브젝트 밑에서 늘었는지**(시체 밑인지, 게임의 오브젝트 풀인지), 꺼져 있는(안 보이는) 오브젝트 수,
    시체 수, 게임 오브젝트 풀의 사용 중/대기 수를 추가 — 시체가 원인인지, 무기를 바꿀 때마다 새로 만든 모델이 쌓이는 건지 가려내기 위함
- 2026-09-29 21:07 (KST) — **v2.2.0: 두 번째 실전 로그 반영 + 누수 추적 기능**
  - 확인됨: 자동 GC 정상(4회, 매번 1~1.5GB 회수). 끊기는 건 매번 **마지막 1프레임만**(55~70ms)이고 나머지는 전부 짧게 나뉨.
    전투 판정도 작동(`waited 12s, quiet 15s` — 싸우는 동안 미뤘다가 조용해지자 실행)
  - **레이드 시작 에셋 정리 기본값 끔:** 실제로 게임이 **5.6초 멈췄는데** VRAM은 0.07GB만 줄었음. 기존 설정 파일에 켜져 있어도
    이번 버전 첫 실행 때 한 번 자동으로 꺼 줌
  - **레이드 중 자동 에셋 정리:** 두 번 해 봤는데 전혀 안 줄어서(모드 누수 확정) 그 레이드에서 자동으로 멈췄음(설계대로).
    이제는 **게임을 끌 때까지** 다시 시도하지 않음 (레이드마다 끊김 2번씩 겪지 않게)
  - **새 기능: 누수 추적 (진단용, 기본 끔)** — 몇 분마다 게임 안의 오브젝트를 종류별(재질·메시·텍스처 등)·이름별로 세서,
    레이드 시작 이후 **무엇이 계속 늘어나는지** 로그(`[leak]`)에 남김. 어느 모드가 새는지 찾는 용도
  - 1분 로그에 봇 수(살아 있음/죽음)와 텍스처 메모리 추가 — 메모리 증가가 봇 수와 같이 느는지 비교용
  - 확인된 사실: VRAM이 16GB 중 15.5GB에서 꽉 차고, 넘친 만큼(최대 1.8GB) 공유 메모리(시스템 RAM)로 넘어감
- 2026-09-29 19:45 (KST) — **v2.1.0: 첫 실전 로그 반영 + SPTVRAMCleaner 기능 합침**
  - 로그로 확인된 것: 증분 GC 지원됨, 자동 GC가 5번 돌아서 매번 관리 메모리 1.1~1.3GB 회수 (예: 4.07 → 2.99GB)
  - **문제 1 — 워킹셋 정리 중 게임이 3.5초 멈춤:** 게임 메모리 41GB를 내보내는 윈도우 API 호출 자체가 3.5초 걸렸고, v2.0.0은
    이걸 게임 메인 스레드에서 불러서 그동안 게임이 멈췄음 → **별도 스레드에서 실행**하도록 수정 (게임은 계속 돎)
  - **문제 2 — GC 마지막 단계에서 한 프레임이 45~141ms 걸림:** 이 단계는 나눌 수 없음 → **"전투 중에는 미루기"** 추가.
    최근 15초 동안 총을 쏘거나 맞거나 조준했으면 정리를 미루고, 조용해지거나 **인벤토리를 열면 바로** 실행 (GC는 최대 3분까지만 미룸)
  - **문제 3 — 진짜 누수는 GC 밖에 있었음:** 1시간 레이드 동안 관리 메모리는 2~5GB에서 오르내렸지만, 게임 전체 커밋 메모리는
    20.7 → 74.7GB (1분에 약 1GB)로 늘었음. 이건 GC가 못 건드리는 "네이티브 메모리"(텍스처·모델·엔진·모드 오브젝트)
  - **새 기능: 에셋·VRAM 정리 (SPTVRAMCleaner 개선판)**
    - 레이드 시작(카운트다운 끝) 3초 뒤 1회: 메뉴·로딩 때 쓰고 남은 텍스처·모델을 내림 (원작 기능)
    - 원작의 `GC.Collect()`는 레이드 중 GC가 꺼져 있어 **실제로는 무시되고 있었음** → 필요하면 증분 GC를 먼저 끝낸 뒤 에셋을 내리도록 바꿈
    - 레이드 중 자동: 네이티브 메모리가 8GB 늘면 조용한 순간에 정리. **두 번 연속 0.5GB도 못 비우면** 에셋 문제가 아니라
      모드 누수로 보고 그 레이드에서는 자동 정리를 멈춤 (쓸데없는 끊김 방지)
    - 원작은 Harmony 패치 2개를 썼지만 이 버전은 패치 없이 게임 상태를 1초마다 확인
  - **VRAM 사용량 표시:** 작업 관리자와 같은 윈도우 성능 카운터로 이 게임이 쓰는 그래픽 메모리를 측정 (백그라운드 스레드)
  - 로그/상태에 네이티브 메모리, VRAM, 전투 상태, GC 중 가장 긴 프레임이 몇 번째였는지 추가
- 2026-09-29 11:05 (KST) — **v2.0.0: SPT 4.1 포팅 + 구조 개편**
  - SPT 3.7(Aki) 전용이던 원본을 SPT 4.1(`netstandard2.1`)로 옮김. 옛 `Aki.Reflection` 참조를 없애고,
    게임 안의 RAM 정리 함수를 이름으로 찾던 방식 대신 윈도우 API를 직접 호출하도록 바꿈
  - **새 기능: 자동 GC 정리** — 레이드 중 게임이 꺼둔 GC(안 쓰는 메모리를 치우는 청소부)를 잠깐 켜서
    쌓인 메모리를 여러 프레임에 조금씩(기본 프레임당 2ms) 나눠 치움. 레이드가 길어질수록 메모리가
    계속 늘어나던 현상(SAIN 시뮬 등)을 막는 핵심 기능
  - **끊김 원인 수정:** 원본 방식(워킹셋 정리)은 기본값을 "시스템 RAM이 부족할 때만"으로 바꿈. 원본은 5분마다
    무조건 실행해서 매번 끊김을 만들었음
  - 원본은 타이머가 별도 스레드에서 게임 상태를 읽었음 → 전부 게임 메인 스레드(`Update`, 매 프레임 실행되는 부분)에서 처리하도록 변경
  - F12 설정 화면 전체 한글화 + 현재 메모리 상태 표시 + 수동 실행 버튼(GC / 워킹셋 / 에셋) + 화면 오버레이 + 주기적 메모리 로그 추가

---

## 이 모드가 하는 일 (한 줄 요약)

레이드 중에 늘어나기만 하는 게임 메모리를 **끊김이 느껴지지 않는 순간에** 치워 줍니다. GC(관리 메모리), 에셋(텍스처·모델, VRAM),
워킹셋(RAM 비상 정리) 세 가지를 따로 다룹니다.

## 원본 모드가 끊겼던 이유와 메모리가 계속 늘어나는 이유

게임 파일(`Assembly-CSharp.dll`)을 직접 열어서 확인한 내용입니다.

1. **RAM이 12GB 이상인 PC에서는 레이드가 시작될 때 게임이 GC를 완전히 꺼 버립니다.**
   (레이드 준비 단계에서 `GCEnabled = false`. 조건은 게임 설정값 `GigabytesRequiredToDisableGCDuringRaid = 12`)
   - GC가 꺼져 있으면 봇·AI 모드(SAIN 등)·다른 모드가 레이드 동안 만든 임시 데이터가 **하나도 안 치워지고 계속 쌓입니다.**
   - GC를 끈 이유는 GC가 돌 때 생기는 끊김을 피하려는 것입니다. 대신 메모리가 레이드 끝날 때까지 늘어나기만 합니다.
2. **원본 모드의 "RAM 클리너"는 메모리를 비우지 않습니다.** 윈도우의 `EmptyWorkingSet`(게임이 쓰던 메모리를
   RAM에서 대기 메모리/페이지 파일로 밀어내는 기능)을 부를 뿐입니다. 게임이 그 메모리를 다시 쓰는 순간 다시 읽어 와야 해서
   **직후에 끊김이 생깁니다.** 원본은 이걸 5분마다 무조건 했습니다.
3. **(v2.1.0 로그로 확인) 메모리 증가의 대부분은 GC 밖(네이티브)입니다.** 관리 메모리는 GC로 잡히지만, 커밋 메모리는
   1분에 약 1GB씩 계속 늘었습니다. 에셋 정리로 줄어드는지(= 안 쓰는 텍스처·모델) 안 줄어드는지(= 어떤 모드가 붙잡고 있는 누수)를
   v2.1.0 로그의 `UnloadUnusedAssets done ... freed` 값으로 구분할 수 있습니다.

> 참고: GC로 치운 메모리는 게임이 다시 쓰려고 **쥐고 있는 경우가 많아서**, 작업 관리자 숫자가 바로 뚝 떨어지지 않을 수
> 있습니다. F12 "현재 상태"의 "관리 메모리 사용" 수치가 정리 후 내려가는지로 확인하세요.

## 설치

1. `release\RamCleanerInterval-2.1.0-SPT4.1.zip`을 SPT 폴더(`E:\SPT 4.1`)에 그대로 풀면
   `BepInEx\plugins\CactusPie.RamCleanerInterval\CactusPie.RamCleanerInterval.dll`로 들어갑니다.
2. **예전 버전 DLL이 `BepInEx\plugins\`에 있으면 지우세요.**
3. **SPTVRAMCleaner를 쓰고 있었다면 빼세요.** 이 모드에 같은 기능(개선판)이 들어 있어서, 둘 다 있으면 레이드 시작 때 두 번 정리합니다.

## F12 설정 (전부 한글)

### 1. 자동 메모리 정리 (GC) — 추천

| 항목 | 기본값 | 설명 |
|---|---|---|
| 자동 GC 정리 켜기 | 켬 | 이 모드의 핵심 기능 |
| 정리 시작 기준: 증가량 (GB) | 1.5 | 마지막 정리 이후 이만큼 늘면 정리 시작 |
| 프레임당 작업 시간 (ms) | 2 | 한 프레임에 GC를 몇 ms까지 할지. 단, 마지막 단계는 나눌 수 없음(로그상 45~141ms) |
| 자동 정리 최소 간격 (초) | 60 | 정리가 끝난 뒤 다음 정리까지 최소 대기 |
| 증분 GC 미지원 시 전체 GC 허용 | 끔 | 로그상 증분 GC가 지원되므로 신경 쓰지 않아도 됨 |

### 2. 워킹셋 정리 (원본 RAM 클리너 방식)

| 항목 | 기본값 | 설명 |
|---|---|---|
| 워킹셋 정리 켜기 | 켬 | 원본 기능. v2.1.0부터 별도 스레드에서 실행 |
| 시스템 RAM 부족할 때만 | 켬 | 끄면 원본처럼 "간격마다 무조건" |
| 여유 RAM 기준 (%) | 10 | 64GB 기준 약 6.4GB 미만일 때 |
| 간격 (초) | 300 | 무조건 모드의 간격 / 부족 모드의 최소 간격 |
| GC 정리 직후에도 실행 | 끔 | 작업 관리자 숫자를 줄이고 싶을 때만 |

### 3. 에셋·VRAM 정리 (SPTVRAMCleaner 개선판)

| 항목 | 기본값 | 설명 |
|---|---|---|
| 레이드 시작 시 1회 정리 | **끔** | 카운트다운이 끝난 뒤 안 쓰는 텍스처·모델을 내림. 실측: 5.6초 멈춤, VRAM 0.07GB 감소 → 기본 끔 |
| 시작 후 대기 (초) | 3 | 레이드 시작 후 몇 초 뒤에 할지 |
| 레이드 중 자동 정리 | 켬 | 네이티브 메모리가 많이 늘면 조용한 순간에 정리. 두 번 연속 효과가 없으면 게임을 끌 때까지 중단 |
| 정리 시작 기준: 네이티브 증가량 (GB) | 8 | 마지막 정리 이후 이만큼 늘면 |
| 자동 정리 최소 간격 (분) | 10 | 에셋 정리는 1~3초 끊길 수 있어서 간격을 넉넉히 |
| 정리 전에 GC 먼저 | 켬 | 버려진 오브젝트가 붙잡고 있던 에셋까지 내리기 위해 GC를 먼저 끝냄 |

### 4. 전투 중에는 미루기

| 항목 | 기본값 | 설명 |
|---|---|---|
| 전투 중에는 미루기 | 켬 | 최근에 쏘거나 맞거나 조준했으면 자동 정리를 미룸 (수동 버튼은 안 미룸) |
| 조용한 순간 기준 (초) | 15 | 마지막 활동 후 이만큼 지나면 "조용함" |
| GC 최대 대기 (초, 0=무제한) | 180 | 전투가 길어져도 GC는 이 시간 뒤엔 실행 (메모리 폭주 방지). 에셋 정리는 끝까지 기다림, 워킹셋은 60초 |
| 인벤토리 열면 밀린 정리 바로 실행 | 켬 | Tab 누르면 대기 중인 정리를 즉시 실행 — 가방 보는 동안이라 끊김이 안 느껴짐 |

### 5. 공통 · 수동 실행 · 상태

| 항목 | 기본값 | 설명 |
|---|---|---|
| 레이드 중에만 자동 실행 | 켬 | 은신처·메뉴에서는 자동 정리 안 함 (수동 버튼은 항상 동작) |
| 수동 실행 | — | [GC 정리] [워킹셋 정리] [에셋 정리] 버튼 |
| 현재 상태 | — | 1초마다 갱신: 관리/네이티브 메모리, 게임 전체 RAM, 시스템 여유 RAM, **VRAM**, GC 상태, 전투 상태, 대기 중인 정리, 마지막 결과 |
| 화면에 메모리 표시 | 끔 | 왼쪽 위에 한 줄 표시 |
| 로그 기록 간격 (초, 0=끔) | 60 | 레이드 중 `BepInEx\LogOutput.log`에 메모리 상태를 한 줄씩 기록 |

### 6. 누수 추적 (진단용)

| 항목 | 기본값 | 설명 |
|---|---|---|
| 누수 추적 켜기 | 끔 | 레이드 시작 1분 뒤 기준점, 그 뒤 간격마다 오브젝트를 세서 늘어난 것을 로그 `[leak]`에 기록. 한 번에 0.1~1초 끊길 수 있음(조용한 순간에만) |
| 기록 간격 (분) | 5 | |

수동 실행 버튼에 **[누수 추적 기록]**도 추가됐습니다(바로 한 번 세기).

## 테스트할 때 봐 주실 것 (디버깅용)

**v2.2.0 테스트 방법 (누수 찾기):** F12 → "6. 누수 추적" → **켜기**, 평소처럼 SAIN 시뮬을 20~30분 돌린 뒤 로그를 보내 주세요.
`[leak]` 줄에 "레이드 시작 이후 가장 많이 늘어난 오브젝트 종류/이름"이 찍힙니다. 예를 들어 `UnityEngine.Material +50000`이면
어떤 모드가 재질을 계속 복사하는 것이고, 이름 칸에 모드 특유의 이름(이펙트, 데칼 등)이 보이면 범인 후보가 됩니다.


`E:\SPT 4.1\BepInEx\LogOutput.log`에서 `RAM 클리너` 줄을 찾아 주세요.

1. **레이드 시작:** `Raid started: ...` 다음 3초쯤 뒤 `UnloadUnusedAssets done (raid start) ... VRAM a -> b GB (freed c)` —
   레이드 시작 정리로 VRAM이 얼마나 줄었는지.
2. **`[mem]` 줄(1분마다)** — `native`와 `VRAM` 값이 새로 찍힙니다. `VRAM -1.00`이면 측정이 안 되는 환경입니다.
3. **레이드 중 자동 에셋 정리:** `UnloadUnusedAssets done (auto, native +8.xx GB ...) ... freed x GB`
   - `freed`가 수 GB면: 늘어나던 게 안 쓰는 에셋이었음 → 이 기능이 누수를 잡아 줌
   - `freed`가 0.5GB 미만으로 두 번이면 `Auto asset unload stopped for this raid` → **모드 누수**. 이때는 모드를 빼 가며 비교해야
     원인을 찾을 수 있습니다 (`[mem]`의 `native`가 1분에 얼마나 느는지 비교).
4. **GC:** `GC start (auto, ..., quiet 20s)` / `inventory open` — 조용한 순간에 돌았는지. `GC done ... max slice Nms at frame X/Y`.
5. **워킹셋 정리:** `background call Nms` — 이제 이 시간 동안 게임이 멈추지 않아야 합니다.

## 직접 빌드

- 루트의 `build.bat` 더블클릭 → Release 빌드, `E:\SPT 4.1\BepInEx\plugins\CactusPie.RamCleanerInterval\`로 자동 복사,
  `release\RamCleanerInterval-2.1.0-SPT4.1.zip` 생성.
- SPT 경로가 다르면 `src\RamCleaner.local.props.example`을 `src\RamCleaner.local.props`로 복사해서 경로를 고치세요.
- 필요한 것: .NET SDK 8 이상. 참조는 SPT 설치 폴더의 `EscapeFromTarkov_Data\Managed`와 `BepInEx\core`에서 가져옵니다
  (Harmony·`spt-reflection`은 쓰지 않습니다 — 이 모드는 게임 코드를 패치하지 않습니다).

## 파일 구성

| 파일 | 역할 |
|---|---|
| `CustomRamCleanerIntervalPlugin.cs` | 플러그인 본체. 매 프레임 처리(`Update`), 정리 시점 판단, 에셋 정리, 워킹셋 정리(별도 스레드), 상태·오버레이·로그 |
| `CustomRamCleanerIntervalPlugin.Settings.cs` | F12 설정 등록 (키는 영어, 화면 표시는 한글) |
| `GcRunner.cs` | GC를 여러 프레임에 나눠 돌리는 부분. 게임이 GC를 꺼 뒀으면 잠깐 "수동"으로 켜고 끝나면 되돌림 |
| `CombatTracker.cs` | "지금 정리해도 되나" 판단 — 플레이어가 쏘거나(`OnShot`) 맞거나(`BeingHitAction`) 조준 중인지, 인벤토리가 열렸는지(`OnInventoryOpened`) |
| `VramMonitor.cs` | 이 게임의 VRAM 사용량 측정 (윈도우 성능 카운터, 백그라운드 스레드) |
| `MemoryStats.cs` | 메모리 수치 읽기(윈도우 API)와 워킹셋 정리 |
| `GameHelper.cs` | 지금 레이드 중인지 판단 (은신처는 제외) — 원본 그대로 |

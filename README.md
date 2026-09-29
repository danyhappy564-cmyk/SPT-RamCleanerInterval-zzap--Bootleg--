---

### ⚠️ IMPORTANT NOTICE / DISCLAIMER

**Original Author:** CactusPie
**Original Repository:** SPT-RamCleanerInterval
**Original Link:** https://github.com/CactusPie/SPT-RamCleanerInterval
**License:** GNU GPL v3.0 (see `LICENSE`) — this port stays under the same license.
**This Port By:** R_F (danyhappy564-cmyk) — unofficial, AI-assisted port. Not affiliated with or endorsed by the original author.

1. **Reflection & Take-Downs:** I deeply reflect on the ECOT incident. As an AI-assisted "vibe coder," I will immediately delete files if the original authors ask.
2. **No Re-Distribution:** These ported builds are unverified, temporary fixes. Please do NOT re-upload or share them anywhere else.
3. **Do Not Pester Original Authors:** Never report bugs or pester original modders regarding issues from my unofficial ports.
4. **Full Credit & Respect:** I will always credit original creators on GitHub and prioritize their decisions above all else.
5. **Support Original Creators:** Instead of using my ports, please visit the original authors' Forge pages to leave kind words or tips.

---

## 변경 이력

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

레이드 중에 늘어나기만 하는 게임 메모리를 **끊김 없이 조금씩** 치워 줍니다.

## 원본 모드가 끊겼던 이유와 메모리가 계속 늘어나는 이유

게임 파일(`Assembly-CSharp.dll`)을 직접 열어서 확인한 내용입니다.

1. **RAM이 12GB 이상인 PC에서는 레이드가 시작될 때 게임이 GC를 완전히 꺼 버립니다.**
   (레이드 준비 단계에서 `GCEnabled = false`. 조건은 게임 설정값 `GigabytesRequiredToDisableGCDuringRaid = 12`)
   - GC가 꺼져 있으면 봇·AI 모드(SAIN 등)·다른 모드가 레이드 동안 만든 임시 데이터가 **하나도 안 치워지고 계속 쌓입니다.**
     64GB PC라서 오히려 이 조건에 해당합니다. 봇이 많고 레이드가 길수록(시뮬) 누수처럼 보이는 이유가 이것입니다.
   - GC를 끈 이유는 GC가 돌 때 생기는 끊김을 피하려는 것입니다. 대신 메모리가 레이드 끝날 때까지 늘어나기만 합니다.
2. **원본 모드의 "RAM 클리너"는 메모리를 비우지 않습니다.** 윈도우의 `EmptyWorkingSet`(게임이 쓰던 메모리를
   RAM에서 대기 메모리/페이지 파일로 밀어내는 기능)을 부를 뿐입니다. 작업 관리자 숫자는 줄지만, 게임이 그 메모리를
   다시 쓰는 순간 디스크/대기 목록에서 다시 읽어 와야 해서 **직후에 끊김이 생깁니다.** 원본은 이걸 5분마다 무조건 했습니다.

그래서 v2.0.0은 이렇게 바꿨습니다.
- **진짜로 치우는 건 GC가 합니다.** 메모리가 일정량(기본 1.5GB) 늘면 GC를 잠깐 "수동" 모드로 켜고,
  Unity의 **증분 GC**(한 번에 다 하지 않고 프레임마다 조금씩 하는 GC)로 프레임당 최대 2ms씩 나눠서 치운 뒤
  게임 원래 상태(GC 꺼짐)로 되돌립니다.
- **워킹셋 정리(원본 기능)는 비상용**으로만 남겼습니다. 기본값은 "윈도우 전체 여유 RAM이 10% 미만일 때만"입니다.
  64GB PC라면 평소에는 거의 실행되지 않습니다.

> 참고: GC로 치운 메모리는 게임이 다시 쓰려고 **쥐고 있는 경우가 많아서**, 작업 관리자 숫자가 바로 뚝 떨어지지 않을 수
> 있습니다. 대신 **계속 늘어나던 게 멈추는 것**이 정상 동작입니다. F12 "현재 상태"의 "관리 메모리 사용" 수치가 정리 후
> 내려가는지로 확인하세요.

## 설치

1. `release\RamCleanerInterval-2.0.0-SPT4.1.zip`을 SPT 폴더(`E:\SPT 4.1`)에 그대로 풀면
   `BepInEx\plugins\CactusPie.RamCleanerInterval\CactusPie.RamCleanerInterval.dll`로 들어갑니다.
2. **예전 버전 DLL이 `BepInEx\plugins\`에 있으면 지우세요.** (같은 모드라서 BepInEx가 새 버전만 쓰긴 하지만 헷갈리지 않게)
3. 게임 설정의 "자동 RAM 정리(Automatic RAM Cleaner)"는 켜 두든 끄든 상관없습니다. 게임 쪽 정리는 레이드 시작·메뉴에서만 돕니다.

## F12 설정 (전부 한글)

### 1. 자동 메모리 정리 (GC) — 추천

| 항목 | 기본값 | 설명 |
|---|---|---|
| 자동 GC 정리 켜기 | 켬 | 이 모드의 핵심 기능 |
| 정리 시작 기준: 증가량 (GB) | 1.5 | 마지막 정리 이후 이만큼 늘면 정리 시작. 낮추면 자주·짧게 |
| 프레임당 작업 시간 (ms) | 2 | 한 프레임에 GC를 몇 ms까지 할지. 끊김이 느껴지면 1로, 정리가 너무 오래 걸리면 3~4로 |
| 자동 정리 최소 간격 (초) | 60 | 정리가 끝난 뒤 다음 정리까지 최소 대기 |
| 증분 GC 미지원 시 전체 GC 허용 | 끔 | 게임이 증분 GC를 지원하지 않을 때만 의미 있음 (아래 "로그 확인" 참고) |

### 2. 워킹셋 정리 (원본 RAM 클리너 방식)

| 항목 | 기본값 | 설명 |
|---|---|---|
| 워킹셋 정리 켜기 | 켬 | 원본 기능 |
| 시스템 RAM 부족할 때만 | 켬 | 끄면 원본처럼 "간격마다 무조건" (끊김 생김) |
| 여유 RAM 기준 (%) | 10 | 64GB 기준 약 6.4GB 미만일 때 |
| 간격 (초) | 300 | 무조건 모드의 간격 / 부족 모드의 최소 간격 |
| GC 정리 직후에도 실행 | 끔 | 작업 관리자 숫자를 줄이고 싶을 때만 |

### 3. 공통 · 수동 실행 · 상태

| 항목 | 기본값 | 설명 |
|---|---|---|
| 레이드 중에만 자동 실행 | 켬 | 은신처·메뉴에서는 자동 정리 안 함 (수동 버튼은 항상 동작) |
| 수동 실행 | — | [지금 GC 정리] [워킹셋 정리] [에셋 정리] 버튼. 에셋 정리는 안 쓰는 텍스처·모델을 내리는 기능이라 1~3초 멈출 수 있음 |
| 현재 상태 | — | 1초마다 갱신: 관리 메모리 / 게임 전체 RAM / 시스템 여유 RAM / GC 상태 / 마지막 정리 결과 |
| 화면에 메모리 표시 | 끔 | 왼쪽 위에 한 줄 표시 |
| 로그 기록 간격 (초, 0=끔) | 60 | 레이드 중 `BepInEx\LogOutput.log`에 메모리 상태를 한 줄씩 기록 |

## 테스트할 때 봐 주실 것 (디버깅용)

`E:\SPT 4.1\BepInEx\LogOutput.log`에서 `RAM 클리너` 줄을 찾아 주세요.

1. **게임 켤 때:** `Loaded. incremental GC supported=True/False`
   - `True`면 정상입니다 (나눠서 치우는 방식 사용 가능).
   - `False`면 자동 GC 정리가 동작하지 않습니다. 이 경우 로그를 보내 주시면 대안을 적용하겠습니다.
2. **레이드 시작:** `Raid started: GC mode Disabled, ...` — 게임이 GC를 끈 것이 확인되는 줄입니다.
3. **정리될 때:** `GC start (auto, +1.50 GB)` → `GC done ...: 5.10 -> 3.20 GB in 12.3s, ... max slice 2.10ms`
   - `max slice`가 프레임당 실제로 걸린 최대 시간입니다. 설정값보다 훨씬 크면(예: 20ms 이상) 알려 주세요.
4. **1분마다:** `[mem] mono used ... | working set ... | system free ...` — 레이드가 길어져도 `mono used`가
   계속 오르기만 하지 않고 톱니 모양으로 오르내리면 정상입니다.
5. 체감: 정리 중(`GC 정리 중 N초` 표시) 끊김이 느껴지는지, 원본 대비 레이드 중 끊김이 줄었는지.

## 직접 빌드

- 루트의 `build.bat` 더블클릭 → Release 빌드, `E:\SPT 4.1\BepInEx\plugins\CactusPie.RamCleanerInterval\`로 자동 복사,
  `release\RamCleanerInterval-2.0.0-SPT4.1.zip` 생성.
- SPT 경로가 다르면 `src\RamCleaner.local.props.example`을 `src\RamCleaner.local.props`로 복사해서 경로를 고치세요.
- 필요한 것: .NET SDK 8 이상. 참조는 SPT 설치 폴더의 `EscapeFromTarkov_Data\Managed`와 `BepInEx\core`에서 가져옵니다
  (SPT 전용 DLL `spt-reflection` 등은 쓰지 않습니다 — 이 모드는 게임 코드를 패치하지 않습니다).

## 파일 구성

| 파일 | 역할 |
|---|---|
| `CustomRamCleanerIntervalPlugin.cs` | 플러그인 본체. F12 설정 등록, 매 프레임 처리(`Update`), 상태 표시·오버레이·로그 |
| `GcRunner.cs` | GC를 여러 프레임에 나눠 돌리는 부분. 게임이 GC를 꺼 뒀으면 잠깐 "수동"으로 켜고 끝나면 되돌림 |
| `MemoryStats.cs` | 메모리 수치 읽기(윈도우 API)와 워킹셋 정리 |
| `GameHelper.cs` | 지금 레이드 중인지 판단 (은신처는 제외) — 원본 그대로 |

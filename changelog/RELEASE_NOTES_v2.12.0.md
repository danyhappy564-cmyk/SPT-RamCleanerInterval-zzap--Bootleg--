# RAM 클리너 (RamCleanerInterval-zzap--Bootleg-) v2.12.0 릴리즈 노트 (v2.11.0 대비)

> 원작: CactusPie — SPT-RamCleanerInterval (GPL-3.0). 비공식 SPT 4.1 포팅입니다. 문제가 생겨도 원작자에게 문의하지 말아 주세요.
> v2.11.0 노트의 내용(서버 상태, 메모리 예측, 세션 보고서, [실험] 무거운 아이템)은 그대로 유효합니다.

**<설치>**

- DLL을 덮어쓰면 됩니다. 기존 설정은 그대로 유지되고, F12 맨 위에 **'00. 모드 · 언어'** 가 생깁니다.

---

**<변경점>**

- **모드 3종 (F12 → 00. 모드 · 언어 → 모드)**

1. **자동 정리** — 메모리 정리만 합니다. 화면 표시를 끄고 로그도 거의 남기지 않습니다. 평소 게임·레이드용이고 부담이 가장 적습니다.
2. **간단 확인** — 자동 정리에 더해 왼쪽 위 화면 표시(메모리·FPS·서버·여유 예상), 끊김 횟수, 레이드 결산, 세션 보고서를 켭니다. 아주 가볍습니다.
3. **집중 분석** — 전부 켭니다: 모드별 부하 상시 측정, 끊김 원인 모드 추적, 원인 추적 요약 로그, [실험] 무거운 아이템 찾기. 프레임당 약 0.1~0.5ms가 더 들고 로그가 많이 쌓이니 **문제를 찾는 동안만** 쓰고 돌아오세요.
4. 모드를 고르는 순간 관련 설정이 한 번에 바뀌고, 그 뒤 개별 설정은 자유롭게 바꿔도 됩니다. 메모리 정리와 위험 경고는 어느 모드에서나 켜져 있습니다.

- **한국어 / English 전환 (F12 → 00. 모드 · 언어 → 언어)**

1. F12의 분류·이름·설명, 왼쪽 위 화면 표시, F12 '현재 상태', 게임 알림, 세션 보고서 페이지가 전부 같이 바뀝니다.
2. F12는 창을 닫았다 다시 열면 바뀐 언어로 보입니다. 로그 파일 내용은 제보용이라 그대로입니다.

- **알려진 문제 / 주의**

1. 집중 분석에서 다른 모드로 바꾸면 모드별 부하 분석의 측정 장치는 게임을 다시 켜야 완전히 빠집니다(그 전까지는 측정만 멈춤).
2. 언어를 레이드 도중에 바꾸면 그 판의 끊김 원인 목록에 두 언어가 섞여 보일 수 있습니다(다음 판부터 정상).

^^7

------------------------------------------------------------------------------------------------------------------------------------------------------

# RAM Cleaner (RamCleanerInterval-zzap--Bootleg-) v2.12.0 release notes (vs v2.11.0)

> Original: CactusPie — SPT-RamCleanerInterval (GPL-3.0). Unofficial SPT 4.1 port; please don't contact the original author about it.
> Everything in the v2.11.0 notes (server status, memory forecast, session report, experimental heavy items) still applies.

**Install**

- Overwrite the DLL. Your settings are kept; a new **"00. Mode · language"** section appears at the top of F12.

---

**Changes**

- **Three modes (F12 → 00. Mode · language → Mode)**

1. **Auto cleanup** — memory cleanup only. No overlay, almost nothing in the log. For everyday play and raids; the lowest cost.
2. **Quick view** — auto cleanup plus the top-left overlay (memory, FPS, server, time left), stutter count, raid report and session report. Very light.
3. **Deep analysis** — everything on: per-mod cost measured all the time, which mod causes stutters, the diagnostic summary log and the experimental heavy item finder. About 0.1–0.5 ms extra per frame and a busy log — use it **only while hunting a problem**, then switch back.
4. Picking a mode sets the related switches in one go; you can still change any of them afterwards. Memory cleanup and the danger warnings stay on in every mode.

- **Korean / English (F12 → 00. Mode · language → Language)**

1. F12 categories, names and descriptions, the top-left overlay, the F12 status block, in-game notifications and the session report page all switch together.
2. F12 shows the new language after you close and reopen it. Log files stay as they are (they're for bug reports).

- **Known issues / notes**

1. Leaving deep analysis, the per-mod cost probe is fully removed only after a game restart (until then it just stops measuring).
2. Switching language in the middle of a raid can mix both languages in that raid's stutter cause list (fine from the next raid).

^^7

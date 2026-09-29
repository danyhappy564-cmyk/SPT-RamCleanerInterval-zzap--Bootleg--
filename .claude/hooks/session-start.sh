#!/bin/bash
# 세션 시작/재개(startup, resume, clear, compact) 시 origin/master의 최신 커밋을
# 자동으로 받아온다. 여러 세션이 동시에 이 저장소에 커밋하는 경우가 있어서,
# 사용자가 매번 "최신 내용 반영해"라고 수동으로 시킬 필요 없이 항상 최신
# 상태에서 작업을 시작하기 위한 것이다.
set -uo pipefail

cd "${CLAUDE_PROJECT_DIR:-$(dirname "$0")/../..}" || exit 0

git rev-parse --is-inside-work-tree >/dev/null 2>&1 || exit 0

# 이 저장소의 git hooks 경로를 추적되는 .githooks/로 강제한다. pre-commit이
# 매 커밋 직전 작성자를 R_F로 재확인/재설정하므로, 세션 도중 전역 git 설정이
# 흐트러져도(2026-09-18 사고) 커밋 작성자가 틀어지지 않는다.
git config core.hooksPath .githooks 2>/dev/null || true

git fetch origin master --quiet 2>/dev/null || exit 0

LOCAL=$(git rev-parse HEAD 2>/dev/null || echo "")
REMOTE=$(git rev-parse origin/master 2>/dev/null || echo "")

if [ -n "$LOCAL" ] && [ -n "$REMOTE" ] && [ "$LOCAL" != "$REMOTE" ]; then
  # 작업 트리가 깨끗할 때만 fast-forward — 진행 중인 수정을 덮어쓰지 않는다.
  if git diff --quiet 2>/dev/null && git diff --cached --quiet 2>/dev/null; then
    if git merge --ff-only origin/master --quiet 2>/dev/null; then
      echo "RamCleanerInterval: origin/master 최신 내용으로 갱신했습니다 ($LOCAL -> $REMOTE)."
    fi
  fi
fi

# 동시 작업 세션 경고: origin/master의 최신 커밋이 최근 N분 이내면, 다른 세션이
# 지금 이 저장소를 동시에 건드리고 있을 수 있다는 신호로 보고 알린다.
# 실시간 락은 아니고 참고용 힌트다 — 커밋 자체는 pre-commit 훅 + fetch 후
# fast-forward로 이미 안전하게 처리되므로, 이건 어디까지나 "겹칠 수 있다"는
# 귀띔일 뿐이다.
THRESHOLD_MIN=15
LAST_COMMIT_EPOCH=$(git log -1 --format=%ct origin/master 2>/dev/null || echo "")
if [ -n "$LAST_COMMIT_EPOCH" ]; then
  NOW_EPOCH=$(date +%s)
  AGE_MIN=$(( (NOW_EPOCH - LAST_COMMIT_EPOCH) / 60 ))
  if [ "$AGE_MIN" -ge 0 ] && [ "$AGE_MIN" -lt "$THRESHOLD_MIN" ]; then
    LAST_MSG=$(git log -1 --format='%s (%an, %ci)' origin/master 2>/dev/null || echo "")
    echo "주의: origin/master이 ${AGE_MIN}분 전에 갱신됐습니다 — 다른 세션이 지금 이 저장소를 동시에 작업 중일 수 있습니다. 최근 커밋: $LAST_MSG"
  fi
fi

exit 0

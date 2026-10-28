#!/usr/bin/env bash
# SessionStart hook: Claude Code adds what this prints to the session's context, so every session (yolo worktrees,
# agents, cloud) starts knowing where the branch stands and how work moves. The rules themselves are in CLAUDE.md.
cd "${CLAUDE_PROJECT_DIR:-.}" 2>/dev/null || exit 0
branch=$(git branch --show-current 2>/dev/null)
tag=$(git describe --tags --abbrev=0 --match 'v*' origin/main 2>/dev/null)
unreleased=$(git rev-list --count "$tag..origin/main" 2>/dev/null)
ahead=$(git rev-list --count origin/main..HEAD 2>/dev/null)
echo "Flexarr workflow (see CLAUDE.md): branch ${branch:-detached}, ${ahead:-?} commit(s) ahead of main; main has ${unreleased:-?} commit(s) since ${tag:-the last release}."
echo "Finish a branch with /land (never releases). Release only when the user asks (/release); offer it once a batch has built up. /deploy puts main on the Mini, /devices installs the Apple apps."

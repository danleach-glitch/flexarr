#!/usr/bin/env bash
# Claude Code status line: "<branch> +<commits ahead of main> · <commits on main since the last release> unreleased since <tag>".
dir=$(jq -r '.workspace.current_dir // .cwd // empty' 2>/dev/null)
cd "${dir:-.}" 2>/dev/null || exit 0
branch=$(git branch --show-current 2>/dev/null) || exit 0
tag=$(git describe --tags --abbrev=0 --match 'v*' origin/main 2>/dev/null)
unreleased=$(git rev-list --count "$tag..origin/main" 2>/dev/null)
ahead=$(git rev-list --count origin/main..HEAD 2>/dev/null)
line="${branch:-detached}"
[ "${ahead:-0}" -gt 0 ] && [ "$branch" != main ] && line="$line +$ahead"
[ -n "$tag" ] && line="$line · ${unreleased:-0} unreleased since $tag"
printf '%s' "$line"

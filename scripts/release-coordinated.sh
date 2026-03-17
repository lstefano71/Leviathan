#!/usr/bin/env bash
# Release one or both products independently.
#
# Usage:
#   ./scripts/release-coordinated.sh --tui2 0.2.2                 # TUI2 only
#   ./scripts/release-coordinated.sh --gui 0.4.0                  # GUI only
#   ./scripts/release-coordinated.sh --tui2 0.2.2 --gui 0.4.0    # both
#   Add --dry-run to preview without committing.
set -euo pipefail

TUI2_VERSION=""
GUI_VERSION=""
DRYRUN=false

while [ "$#" -gt 0 ]; do
  case "$1" in
    --tui2)    TUI2_VERSION="$2"; shift 2 ;;
    --gui)     GUI_VERSION="$2";  shift 2 ;;
    --dry-run) DRYRUN=true;       shift   ;;
    *) echo "Unknown argument: $1" >&2; echo "Usage: $0 [--tui2 X.Y.Z] [--gui X.Y.Z] [--dry-run]" >&2; exit 1 ;;
  esac
done

if [ -z "$TUI2_VERSION" ] && [ -z "$GUI_VERSION" ]; then
  echo "Specify at least one of --tui2 or --gui." >&2
  echo "Usage: $0 [--tui2 X.Y.Z] [--gui X.Y.Z] [--dry-run]" >&2
  exit 1
fi

bump_version() {
  local file="$1" version="$2"
  if [ ! -f "$file" ]; then echo "File not found: $file" >&2; exit 2; fi
  if $DRYRUN; then
    echo "[dry-run] Would update $file -> $version"
  else
    python -c "import json,sys; p=sys.argv[1]; v=sys.argv[2]; d=json.load(open(p)); d['version']=v; open(p,'w').write(json.dumps(d, indent=2)+'\n')" "$file" "$version"
    echo "Updated $file"
  fi
}

CHANGED_FILES=()
TAGS=()
COMMIT_MSG_PARTS=()

if [ -n "$TUI2_VERSION" ]; then
  bump_version "src/Leviathan.TUI2/version.json" "$TUI2_VERSION"
  CHANGED_FILES+=("src/Leviathan.TUI2/version.json")
  TAGS+=("tui2/v${TUI2_VERSION}")
  COMMIT_MSG_PARTS+=("tui2 ${TUI2_VERSION}")
fi

if [ -n "$GUI_VERSION" ]; then
  bump_version "src/Leviathan.GUI/version.json" "$GUI_VERSION"
  CHANGED_FILES+=("src/Leviathan.GUI/version.json")
  TAGS+=("gui/v${GUI_VERSION}")
  COMMIT_MSG_PARTS+=("gui ${GUI_VERSION}")
fi

if $DRYRUN; then
  echo "[dry-run] Would commit: ${COMMIT_MSG_PARTS[*]}"
  for tag in "${TAGS[@]}"; do echo "[dry-run] Would create tag $tag"; done
  echo "Dry run complete"
  exit 0
fi

COMMIT_MSG="Bump version: $(IFS=', '; echo "${COMMIT_MSG_PARTS[*]}")"
git add "${CHANGED_FILES[@]}"
git commit -m "$COMMIT_MSG"

for tag in "${TAGS[@]}"; do
  git tag -a "$tag" -m "Release $tag"
  echo "Created tag $tag"
done

git push origin --follow-tags
echo "Pushed tags: ${TAGS[*]}"

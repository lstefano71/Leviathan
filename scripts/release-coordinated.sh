#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -lt 1 ]; then
  echo "Usage: $0 <version> [--dry-run]"
  exit 1
fi

VERSION="$1"
DRYRUN=false
if [ "${2-}" = "--dry-run" ]; then
  DRYRUN=true
fi

FILES=("src/Leviathan.GUI/version.json" "src/Leviathan.TUI2/version.json")

for f in "${FILES[@]}"; do
  if [ ! -f "$f" ]; then
    echo "File not found: $f" >&2
    exit 2
  fi
  if $DRYRUN; then
    echo "[dry-run] Would update $f -> $VERSION"
  else
    python -c "import json,sys; p=sys.argv[1]; v=sys.argv[2]; d=json.load(open(p)); d['version']=v; json.dump(d, open(p,'w'), indent=2)" "$f" "$VERSION"
    echo "Updated $f"
  fi
done

if $DRYRUN; then
  echo "Dry run complete"
  exit 0
fi

git add "${FILES[@]}"
git commit -m "Bump versions to $VERSION"
git tag -a "v$VERSION" -m "Release v$VERSION"
git push origin --follow-tags
echo "Created tag v$VERSION and pushed changes"

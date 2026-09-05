#!/usr/bin/env bash
#
# Copy a sample of Word documents out of a tree, into one flat directory.
#
# For putting a manageable slice of a real collection somewhere you can convert it, read the
# output, and throw it away. Pairs with scripts/convert-tree.sh.
#
# Two details worth knowing, both learned the hard way:
#
#   * It disambiguates on copy. Documents genuinely do share filenames across folders --
#     report.docx in 2019/ and 2020/ -- and a flat copy would silently drop one. Duplicates
#     become report.docx, report-2.docx, and the count is reported.
#   * It writes _source.tsv, mapping each copied file back to where it came from. When a
#     conversion later fails on "report-2.docx" you need to know which original that was.
#
# Usage:
#   scripts/sample-docs.sh <source-dir> <dest-dir> [count]
#
#   scripts/sample-docs.sh ~/Documents /tmp/sample 50
#   scripts/sample-docs.sh ~/Documents /tmp/sample 50 --seed 7   # reproducible sample
#   scripts/sample-docs.sh ~/Documents /tmp/sample 50 --first    # first 50, not random
#   scripts/sample-docs.sh ~/Documents /tmp/sample 50 --dry-run  # list, copy nothing
#
# Default count is 25. Sampling is random so the slice is representative -- the first N
# files in directory order tend to come from one project and share one template, which is
# the opposite of what you want when testing a converter.

set -uo pipefail

die() { printf '%s\n' "$*" >&2; exit 2; }

[ $# -ge 2 ] || die "usage: $0 <source-dir> <dest-dir> [count] [--seed N] [--first] [--dry-run]"

SOURCE=${1%/}; DEST=${2%/}; shift 2
COUNT=25
SEED=""
MODE=random
DRY=0

while [ $# -gt 0 ]; do
  case "$1" in
    --seed)    SEED=${2:-}; shift 2 || die "--seed needs a value";;
    --first)   MODE=first; shift;;
    --dry-run) DRY=1; shift;;
    ''|*[!0-9]*) die "unrecognised argument: $1";;
    *)         COUNT=$1; shift;;
  esac
done

[ -d "$SOURCE" ] || die "not a directory: $SOURCE"
[ "$COUNT" -gt 0 ] || die "count must be positive"

# Collect candidates. -print0 so filenames with spaces, quotes and newlines survive; Word
# documents routinely have all three.
mapfile -d '' -t found < <(
  find "$SOURCE" -type f \
    \( -iname '*.docx' -o -iname '*.docm' -o -iname '*.dotx' -o -iname '*.dotm' \) \
    -print0
)

# ~$Foo.docx is a Word lock file -- a few hundred bytes of ownership metadata, not a
# readable package. Sampling them would just manufacture failures.
candidates=()
for path in "${found[@]}"; do
  case "$(basename "$path")" in "~\$"*) continue;; esac
  candidates+=("$path")
done

available=${#candidates[@]}
[ "$available" -gt 0 ] || die "no Word documents found under $SOURCE"

if [ "$MODE" = random ]; then
  if command -v shuf >/dev/null 2>&1; then
    mapfile -d '' -t picked < <(printf '%s\0' "${candidates[@]}" | shuf -z ${SEED:+--random-source=<(yes "$SEED")} -n "$COUNT")
  else
    printf 'shuf not found; falling back to the first %s in directory order\n' "$COUNT" >&2
    picked=("${candidates[@]:0:COUNT}")
  fi
else
  picked=("${candidates[@]:0:COUNT}")
fi

printf 'Found %s document(s) under %s\n' "$available" "$SOURCE"
printf 'Taking %s (%s)\n\n' "${#picked[@]}" "$([ "$MODE" = random ] && echo "random${SEED:+, seed $SEED}" || echo "first, directory order")"

if [ "$DRY" -eq 1 ]; then
  for path in "${picked[@]}"; do printf '  %s\n' "${path#"$SOURCE"/}"; done
  printf '\nDry run — nothing copied.\n'
  exit 0
fi

mkdir -p "$DEST" || die "cannot write to $DEST"
MANIFEST="$DEST/_source.tsv"
printf 'copied\toriginal\n' > "$MANIFEST"

copied=0 renamed=0 bytes=0
for path in "${picked[@]}"; do
  base=$(basename "$path")
  stem=${base%.*}
  ext=${base##*.}
  target="$DEST/$base"

  # Same-named documents in different folders are common, and a flat copy would drop one.
  n=2
  while [ -e "$target" ]; do
    target="$DEST/$stem-$n.$ext"
    n=$((n + 1))
  done
  [ "$target" = "$DEST/$base" ] || renamed=$((renamed + 1))

  if cp -- "$path" "$target" 2>/dev/null; then
    copied=$((copied + 1))
    size=$(wc -c < "$target" 2>/dev/null || printf 0)
    bytes=$((bytes + size))
    printf '%s\t%s\n' "$(basename "$target")" "$path" >> "$MANIFEST"
  else
    printf '  could not copy: %s\n' "$path" >&2
  fi
done

printf 'Copied %s document(s) to %s (%s)\n' "$copied" "$DEST" "$(numfmt --to=iec "$bytes" 2>/dev/null || printf '%s bytes' "$bytes")"
[ "$renamed" -gt 0 ] && printf '  %s renamed to avoid overwriting a same-named document\n' "$renamed"
printf '  traceability: %s maps each copy back to its original path\n' "$MANIFEST"
printf '\nNext:\n  scripts/convert-tree.sh %s %s-out\n' "$DEST" "$DEST"

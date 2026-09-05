#!/usr/bin/env bash
#
# Convert a tree of Word documents, one at a time, surviving failures.
#
# THIS IS A TEST HARNESS, NOT A PRODUCT FEATURE. docmd deliberately converts one document
# per invocation: batch mode needs per-document error isolation and a collision policy, and
# it has neither yet. This script supplies both crudely so a real corpus can be run through
# the converter and the results looked at -- which is how the requirements for a proper
# `-r` get derived rather than guessed.
#
# It reports three things worth more than the Markdown:
#
#   * what failed, and on which document -- the failure modes no fixture anticipated
#   * how many documents have NO heading structure -- those are the ones that will chunk
#     badly in a retrieval index, and it is the single most useful pre-flight signal
#   * how many would have COLLIDED under a flat output layout -- two report.docx in
#     different folders write the same report.md. This script mirrors the source tree so
#     that cannot happen, but it counts what a flat run would have lost.
#
# Usage:
#   scripts/convert-tree.sh <input-dir> <output-dir> [-- <extra docmd args>]
#
#   DOCMD=/path/to/docmd scripts/convert-tree.sh ~/corpus /tmp/out
#   scripts/convert-tree.sh ~/corpus /tmp/out -- --no-images
#
# Writes <output-dir>/_report.tsv (one row per document) and a summary to stdout.

# No `set -e`: surviving a failing document is the entire point.
set -uo pipefail

die() { printf '%s\n' "$*" >&2; exit 2; }

[ $# -ge 2 ] || die "usage: $0 <input-dir> <output-dir> [-- <extra docmd args>]"

INPUT=${1%/}; OUTPUT=${2%/}; shift 2
[ "${1:-}" = "--" ] && shift
EXTRA=("$@")

[ -d "$INPUT" ] || die "not a directory: $INPUT"

# Resolve the converter. A built binary is ~1s per document faster than `dotnet run`, which
# matters at corpus scale.
REPO=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
if [ -n "${DOCMD:-}" ]; then
  CONVERT=("$DOCMD")
elif [ -x "$REPO/src/Docmd.Cli/bin/Debug/net10.0/docmd" ]; then
  CONVERT=("$REPO/src/Docmd.Cli/bin/Debug/net10.0/docmd")
elif [ -x "$REPO/src/Docmd.Cli/bin/Release/net10.0/docmd" ]; then
  CONVERT=("$REPO/src/Docmd.Cli/bin/Release/net10.0/docmd")
elif command -v docmd >/dev/null 2>&1; then
  CONVERT=(docmd)
else
  die "no docmd found. Build it (dotnet build $REPO/docmd.slnx) or set DOCMD=/path/to/docmd"
fi

mkdir -p "$OUTPUT" || die "cannot write to $OUTPUT"
REPORT="$OUTPUT/_report.tsv"
printf 'status\texit\theadings\twarnings\tdocument\tdetail\n' > "$REPORT"

total=0 ok=0 failed=0 no_headings=0 with_warnings=0
declare -A flat_names=()
collisions=0

# -print0 / read -d '' so filenames with spaces, quotes and newlines survive. Word documents
# routinely have all three.
while IFS= read -r -d '' doc; do
  base=$(basename "$doc")

  # ~$Foo.docx is a Word lock file, not a document: a few hundred bytes of ownership
  # metadata that is not a readable package. Skipping them keeps the failure count honest.
  case "$base" in "~\$"*) continue;; esac

  total=$((total + 1))

  rel=${doc#"$INPUT"/}
  stem=${base%.*}

  # Mirror the source tree so same-named documents cannot overwrite each other, and count
  # how many a flat layout would have clobbered.
  if [ -n "${flat_names[$stem]:-}" ]; then
    collisions=$((collisions + 1))
  fi
  flat_names[$stem]=1

  dest="$OUTPUT/$(dirname "$rel")"
  mkdir -p "$dest"

  stderr_file=$(mktemp)
  "${CONVERT[@]}" "$doc" -o "$dest" "${EXTRA[@]}" >/dev/null 2>"$stderr_file"
  code=$?

  # docmd reports non-fatal asset problems as "! part: reason" on stderr.
  # grep -c prints its count AND exits 1 when there are no matches, so `|| printf 0`
  # would append a second zero and break every arithmetic test downstream.
  warnings=$(grep -c '^!' "$stderr_file" 2>/dev/null || true)
  [ -n "$warnings" ] || warnings=0
  detail=$(head -1 "$stderr_file" | tr '\t\n' '  ' | cut -c1-160)
  rm -f "$stderr_file"

  md="$dest/$stem.md"
  if [ "$code" -eq 0 ] && [ -f "$md" ]; then
    ok=$((ok + 1))
    # Heading lines in the body. Frontmatter is fenced by --- and carries no '#'.
    headings=$(grep -c '^#\{1,6\} ' "$md" 2>/dev/null || true)
    [ -n "$headings" ] || headings=0
    [ "$headings" -eq 0 ] && no_headings=$((no_headings + 1))
    [ "$warnings" -gt 0 ] && with_warnings=$((with_warnings + 1))
    printf 'ok\t%s\t%s\t%s\t%s\t%s\n' "$code" "$headings" "$warnings" "$rel" "$detail" >> "$REPORT"
  else
    failed=$((failed + 1))
    printf 'FAIL\t%s\t-\t%s\t%s\t%s\n' "$code" "$warnings" "$rel" "$detail" >> "$REPORT"
  fi
done < <(find "$INPUT" -type f \( -iname '*.docx' -o -iname '*.docm' -o -iname '*.dotx' -o -iname '*.dotm' \) -print0)

printf '\n'
if [ "$total" -eq 0 ]; then
  printf 'No Word documents found under %s\n' "$INPUT"
  exit 0
fi

printf '%s documents\n' "$total"
printf '  %-6s converted\n' "$ok"
[ "$failed" -gt 0 ] && printf '  %-6s FAILED — see %s\n' "$failed" "$REPORT"
printf '\n'
printf 'Of the converted:\n'
printf '  %-6s have no heading structure at all — these will chunk badly in a retrieval index\n' "$no_headings"
printf '  %-6s produced asset warnings (EMF/WMF images, missing parts)\n' "$with_warnings"
[ "$collisions" -gt 0 ] && printf '  %-6s share a filename with another document — a FLAT output layout would have\n         overwritten them. This run mirrored the source tree, so nothing was lost.\n' "$collisions"
printf '\nPer-document detail: %s\n' "$REPORT"

# Worst offenders first is usually what you want to look at next.
if [ "$failed" -gt 0 ]; then
  printf '\nFailures:\n'
  awk -F'\t' '$1=="FAIL" {printf "  exit %s  %s\n    %s\n", $2, $5, $6}' "$REPORT" | head -40
fi

exit 0

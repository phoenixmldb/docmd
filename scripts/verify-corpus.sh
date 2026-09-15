#!/usr/bin/env bash
#
# Prove a corpus directory is the one a figure was measured against, and rebuild it when it
# is not.
#
# A coverage number is only meaningful with the documents it was measured on, and this repo
# has already learned that the hard way: the earlier record of the corpus was a list of
# filenames, and by the time anyone tried to re-measure, 20 of its 21 names no longer
# resolved to a file anywhere. Names do not survive reorganisation. Content does.
#
# Usage:
#   scripts/verify-corpus.sh <corpus-dir>                  verify against its _source.tsv
#   scripts/verify-corpus.sh <corpus-dir> --rebuild <root> re-locate missing documents by
#                                                          hash anywhere under <root>
#   scripts/verify-corpus.sh <corpus-dir> --id             print the corpus id and exit
#
# The corpus id is a hash of the sorted document hashes. Two directories with the same id
# hold the same documents, whatever they are called and wherever they came from. Quote it
# next to any figure you publish.

set -uo pipefail

die() { printf '%s\n' "$*" >&2; exit 2; }

[ $# -ge 1 ] || die "usage: $0 <corpus-dir> [--rebuild <source-root>] [--id]"

DIR=${1%/}; shift
REBUILD=""
IDONLY=0

while [ $# -gt 0 ]; do
  case "$1" in
    --rebuild) REBUILD=${2:-}; [ -n "$REBUILD" ] || die "--rebuild needs a source root"; shift 2;;
    --id)      IDONLY=1; shift;;
    *)         die "unrecognised argument: $1";;
  esac
done

[ -d "$DIR" ] || die "not a directory: $DIR"
MANIFEST="$DIR/_source.tsv"
[ -f "$MANIFEST" ] || die "no manifest at $MANIFEST (was this directory made by sample-docs.sh?)"

head -1 "$MANIFEST" | grep -q '^sha256' \
  || die "manifest has no sha256 column; it predates hashing and cannot be verified. Re-sample."

# --- corpus id -------------------------------------------------------------------------
# Sorted, so the id does not depend on the order documents happened to be copied in.
corpus_id() {
  tail -n +2 "$MANIFEST" | cut -f1 | LC_ALL=C sort | sha256sum | cut -c1-16
}

if [ "$IDONLY" -eq 1 ]; then
  printf '%s\n' "$(corpus_id)"
  exit 0
fi

# --- verify ----------------------------------------------------------------------------
present=0 missing=0 changed=0
missing_hashes=()

while IFS=$'\t' read -r hash name origin; do
  [ -n "${hash:-}" ] || continue
  target="$DIR/$name"
  if [ ! -f "$target" ]; then
    missing=$((missing + 1)); missing_hashes+=("$hash	$name	$origin")
    continue
  fi
  actual=$(sha256sum -- "$target" | cut -d' ' -f1)
  if [ "$actual" = "$hash" ]; then
    present=$((present + 1))
  else
    changed=$((changed + 1))
    printf '  CHANGED  %s\n' "$name"
  fi
done < <(tail -n +2 "$MANIFEST")

total=$((present + missing + changed))
printf 'corpus  %s\n' "$DIR"
printf 'id      %s\n' "$(corpus_id)"
printf 'intact  %s of %s\n' "$present" "$total"
[ "$changed" -gt 0 ] && printf 'changed %s  (same name, different content)\n' "$changed"
[ "$missing" -gt 0 ] && printf 'missing %s\n' "$missing"

# --- rebuild ---------------------------------------------------------------------------
if [ -n "$REBUILD" ] && [ "$missing" -gt 0 ]; then
  [ -d "$REBUILD" ] || die "not a directory: $REBUILD"
  printf '\nSearching %s for the %s missing document(s) by content...\n' "$REBUILD" "$missing"

  # One pass over the source tree hashing candidates, rather than one pass per missing
  # document. A large Dropbox is slow to walk and there is no reason to walk it N times.
  declare -A want
  for row in "${missing_hashes[@]}"; do
    want["${row%%	*}"]=$(printf '%s' "$row" | cut -f2)
  done

  recovered=0
  while IFS= read -r -d '' path; do
    case "$(basename "$path")" in "~\$"*) continue;; esac
    h=$(sha256sum -- "$path" | cut -d' ' -f1)
    if [ -n "${want[$h]:-}" ]; then
      cp -- "$path" "$DIR/${want[$h]}" && {
        printf '  recovered  %s\n' "${want[$h]}"
        recovered=$((recovered + 1))
        unset 'want[$h]'
      }
    fi
    [ ${#want[@]} -eq 0 ] && break
  done < <(find "$REBUILD" -type f \
             \( -iname '*.docx' -o -iname '*.docm' -o -iname '*.dotx' -o -iname '*.dotm' \) -print0)

  printf 'recovered %s of %s\n' "$recovered" "$missing"
  [ ${#want[@]} -gt 0 ] && printf 'still missing %s — those documents are not under %s\n' "${#want[@]}" "$REBUILD"
fi

if [ "$missing" -eq 0 ] && [ "$changed" -eq 0 ]; then
  printf '\nIntact. A figure measured here can be quoted against corpus id %s.\n' "$(corpus_id)"
  exit 0
fi
exit 1

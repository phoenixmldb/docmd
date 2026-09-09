# Deferred work

Findings raised during the core-conversion build that were deliberately not fixed there, kept
because later plans inherit them. Each says why it was deferred and what would close it.

## Known defects, ranked

### 1. `xs:integer` on a non-numeric `w:ilvl` raises a dynamic error
`src/Docmd.Word/Stylesheets/markdown.xslt` — `docmd:ilvl` and the `is-ordered` predicate.

Identical failure mode to the duplicate-`w:numId` crash already fixed with positional `[1]` guards:
a malformed value aborts the transform, which breaks the stated invariant that **one malformed
document must never stop a corpus conversion**. Deferred rather than fixed because `w:ilvl/@w:val`
is a spec-typed integer, so a non-numeric value implies a far more deeply malformed file than the
duplicate-`numId` case (which merged or round-tripped documents produce routinely).

Closes with: a guarded cast, and a fixture asserting it degrades rather than throws.

### 2. Four or more leading `w:tab` render as an indented code block
`markdown.xslt` emits one space per tab; the serialiser trims only *trailing* horizontal
whitespace, never leading. Four leading spaces at the start of a block is CommonMark's indented
code block, so a deeply tab-indented paragraph silently becomes code.

Deferred because the fix touches leading-whitespace policy, which is riskier than it looks and was
raised at the merge gate. Closes with: a `TrimStart(' ', '\t')` on a paragraph's first physical
line — which would also tidy the harmless leading space an emphasis span can emit at a paragraph
start.

### 3. Text inside inline content controls, fields and smart tags is dropped
See `docs/limitations.md` for the measured detail and the counts a corpus audit must produce.
Block-level `w:sdt` is *not* affected. Descending through transparent wrappers is a Plan 2 change.

### 4. Table cells lose inline formatting, including hyperlink URLs
A decided product trade-off, not an oversight — for retrieval a URL is near-worthless and often
already dead, while the anchor text survives. Recorded so the corpus audit can report it
("N tables contained hyperlinks flattened to text") rather than losing it silently.

## Smaller items

- `ConversionOptions.ImageDirectoryName` is carried but `AssetRewriter` hardcodes `img/`. Note this
  is an *inversion*, not just a deferral: the option lives in `Docmd.Word` while the behaviour
  lives in Core, so Core structurally cannot honour it. When threaded through, it belongs in a
  Core-side options type.
- `--flavour commonmark` is parsed and plumbed, but the serialiser emits GFM tables regardless.
- Ordered lists always restart at `1.`; a numbered procedure interrupted by a note paragraph
  renumbers from the top. `md:list/@start` exists in the vocabulary and is never emitted.
- Two media parts sharing a file name overwrite each other — `AssetRewriter` keys the output path
  on the file name alone.
- ~~`HeadingAnnotator` walks `Descendants(w:p)`, which reaches paragraphs inside table cells...
  advances `Slugger`'s dedup counter, so real headings acquire unexplained `-1` suffixes.~~
  **Fixed.** A paragraph inside a `w:tc` is now detected as `None` and spends no slug: it is cell
  content, not document structure, and the table template reads cell text with `string-join`
  without ever applying templates to it, so it could never have become a heading anyway. Output
  is byte-identical across the 32-document corpus, because `md:heading/@slug` is still emitted
  and unused — which is exactly why this had to be fixed *before* the review companion starts
  consuming it rather than after.
- A new `XsltTransformer` is constructed and the stylesheet recompiled per document. Correct, but
  it will dominate cost once batch mode exists.
- `md:heading/@slug` is emitted and unused today. It is Plan 2's cross-link key, not dead weight.
- `docmd audit`, `-r`/`--recursive`, `--review`, `--style-map`, `--strict`, `--report` and
  `--revisions` all currently fail cleanly as not-yet-supported.

## Formats

**PowerPoint is tabled, not rejected.** Scoped against 960 real slides in
[`powerpoint-feasibility.md`](powerpoint-feasibility.md); restart from there rather than from
scratch. **Spreadsheets are out of scope permanently** — the reasoning is in the same file, and
it is written down precisely because `.xlsx` will keep looking adjacent.

## A checklist worth keeping

Three separate defects in this build shared one shape: **an element being present was read as the
feature being on**, when OOXML uses a sentinel value to mean *off*.

| Construct | Sentinel meaning "off" |
|---|---|
| `w:b` / `w:i` | `w:val` of `0`, `false` or `off` |
| `w:outlineLvl` | `9` means body text, not a heading |
| `w:numId` | `0` means numbering removed |

Check for this class first when writing `review.xslt` or `pptmd`.

# Changelog

Notable changes to docmd. Dates are release dates; the format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

While docmd is `0.x`, a minor bump may change conversion output. The md-XML vocabulary a custom
stylesheet writes against is not stable until `1.0`.

## [Unreleased]

## [0.1.1] — 2026-09-11

Documentation only. No behaviour change: the conversion, the CLI surface and the output are
byte-for-byte those of 0.1.0.

### Fixed

- Two coverage figures in the shipped XML documentation contradicted every published number.
  `TextCoverageReport` described "38 documents losing nothing and about 3% of all text" — the
  measurement taken before widening the whitelist, which understated the tool by roughly 300x
  against the actual 43 of 49 and twelve words. It reached the generated API documentation, so
  correcting it is the reason this release exists.

### Changed

- The same remark no longer claims one document is "pathological" for keeping 92% of its
  content in text boxes. docmd reads text boxes correctly; that was one of the constructs the
  coverage measurement led us to fix.
- It now states that CI does not reproduce the coverage figures, because the corpus is not in
  this repository — a dated measurement, not a gate.

## [0.1.0] — 2026-09-10

First release. Converts `.docx`, `.docm`, `.dotx` and `.dotm` to Markdown.

### Added

- **Semantic recovery, not text extraction.** Headings are resolved through explicit outline
  level, style outline level, inherited (`w:basedOn`) level, style name, and finally direct
  formatting — recorded per paragraph so the decision is auditable rather than magic. Lists are
  rebuilt from `numId`/`ilvl`, including nesting, since Word stores no nesting of its own.
- **Text coverage reporting.** Every conversion is measured: docmd compares the words a reader
  can see in the document against the words a Markdown parser recovers from the output, and says
  what did not survive and which construct explains it. Silent when nothing was lost.
- **`--style-map`** — map house styles to Markdown constructs in YAML, matching either a
  `w:styleId` or the style name Word shows. Reports entries that matched nothing and styles worth
  mapping, because a map fails silently by nature.
- **`--stylesheet` and `--print-stylesheet`** — print the stylesheet that actually ran, edit it,
  pass it back. The transform is the semantic recovery layer and is meant to be replaced.
- **`IAssetSink`** — supply your own destination for images and control the URI the Markdown
  references. Defaults to writing beside the output.
- **Deterministic output.** Same input, version and options produce byte-identical Markdown: LF
  endings, culture-invariant formatting, no timestamps, UTF-8 without a BOM. Verified under
  `de-DE` and `tr-TR`.
- **YAML frontmatter** carrying document properties and a SHA-256 of the source.
- Images extracted to `img/`, with `--asset-base-url` to emit remote URLs while writing locally.

### Known limitations

Measured on a 49-document corpus of real business documents spanning 2008 to 2024: **43 lose not
one word**, and total loss is 12 words, under 0.01% of the text. The residue and its causes are
in [`docs/limitations.md`](docs/limitations.md).

Not yet implemented, and each fails with a clear message rather than doing nothing quietly:
`-r`/`--recursive`, `docmd audit`, `--review`, `--strict`, `--report`, `--revisions`,
`docmd register`, `docmd license`.

Conversion cost rises faster than document size above roughly 4,000 paragraphs. A 1,000-paragraph
document converts in about 19 seconds; 11,000 takes about five minutes. Memory is not the
constraint and it degrades rather than failing.

### Engine defects found and reported

Building this on our own XSLT engine surfaced three defects in it, written up with reproductions
in [`docs/engine-defects/`](docs/engine-defects/). The largest made a match pattern with two
chained predicates quadratic, which cost 124 seconds on a document that now converts in 19.

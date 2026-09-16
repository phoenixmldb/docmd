# Changelog

Notable changes to docmd. Dates are release dates; the format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

While docmd is `0.x`, a minor bump may change conversion output. The md-XML vocabulary a custom
stylesheet writes against is not stable until `1.0`.

## [Unreleased]

## [0.2.3] — 2026-09-16

### Added

- **`--report`: a coverage digest you can paste into a public issue.** docmd measures every
  conversion, which makes it the only instrument we have for documents we are never allowed to
  see — and none of that could reach us, because the stderr warning quotes the document. Anyone
  willing to help had to hand-redact it or stay quiet.

  `--report` prints counts, cause element names and versions, aggregated, with documents
  identified by ordinal. No filename, no path, no document property, no word of your text.

  Redaction by construction rather than by filtering: the renderer takes a type with no field
  capable of holding text, so it is never given the words to drop. Two tests check it.

### Changed

- **A loss inside an unrecognised wrapper is now reported as `foreign` rather than going
  unattributed.** Cause names were filtered to WordprocessingML — safe, but silent about exactly
  the construct the coverage check exists to surface. Names from any published schema now print
  as themselves; anything else prints as `foreign`, because a custom XML part's element names are
  written by whoever authored the template and are themselves customer content.

### Fixed

- **A paragraph containing a text box no longer drops non-breaking hyphens and symbols.**
  `docmd:own-text`, the text-box exclusion path, was a fifth independent reader of what counts as
  text and still carried the original list — so 0.2.2 dropped these characters in any paragraph
  with a text box, having just been released to fix that class of bug elsewhere.

  Five such readers are now two shared accessors, held together by a mutation-tested agreement
  check. The consolidation is also **11% faster** than before: the old code evaluated its element
  union twice per run, once to decide whether to emit anything and once to walk it.

## [0.2.2] — 2026-09-15

Packaging and dependencies. **No behaviour change**: conversion output is byte-identical to
0.2.1, verified across 52 documents.

### Added

- **A package icon**, embedded via `PackageIcon` rather than the deprecated `PackageIconUrl`,
  which renders as a broken image once whatever it points at moves.

### Changed

- **The package's project website is the documentation**, not the repository. nuget.org renders
  `PackageProjectUrl` as "Project website" and `RepositoryUrl` as "Source repository", and both
  were the same GitHub URL — so the gallery offered one destination twice and no route to the
  docs at all. Project website is now `phoenixml.dev/tools/docmd`; the repository link is
  unchanged.
- **`PhoenixmlDb.Xslt` 1.8.0 → 2.0.0** (bringing Core and XQuery 2.0.0).

  A major version that fixes none of the defects docmd has open against the engine — its
  breaking change is extension functions moving from `ft:`/`dbxml:` to `phx:`, which docmd does
  not use, since its `docmd:` functions are declared in its own stylesheet. Taken for currency
  rather than benefit, and verified by holding the code still and moving only the pin: 355 tests
  pass, the performance gate passes, and 52 of 52 documents convert byte-identically to 1.8.0.

  It was kept out of 0.2.1 deliberately. That was a correctness release, and mixing a dependency
  move into a behaviour change leaves a later bisect unable to tell which caused what. Here both
  changes are behaviour-neutral, so there is nothing to confuse them with.

## [0.2.1] — 2026-09-15

A correctness fix to the feature 0.2.0 was released for. 0.2.0 taught ordinary text to read
`w:noBreakHyphen` and `w:sym`; table cells have their own text extraction and were missed.

### Fixed

- **A table cell no longer drops non-breaking hyphens and symbols.** 0.2.0 fixed this for
  ordinary text but missed table cells, which have their own text extraction — a cell has to
  reach the serialiser as a single `md:text`, so it cannot use the inline emitters. Inside a
  zoning table `Single-family` still converted as `Singlefamily`.

  Found by the coverage check within an hour of releasing 0.2.0: the oracle counts both elements
  and the table path did not emit them, so the mismatch was reported rather than silent. Under
  0.1.2's oracle it would have gone unnoticed exactly as the original hyphen bug did.

- **One more symbol resolved: the left single quotation mark.** It was unresolved, so the
  coverage check reported the word holding it and printed the surroundings, and the surroundings
  said what it was — `The suffixes [003E]boulevard'`, the pair of a mapping already in the table.
  A character nobody has identified naming itself the first time it costs someone a word is what
  the reporting is for.

  With both fixes, a 76,154-word municipal code dense with tables, symbols and non-breaking
  hyphens converts with **nothing lost**. The same document reported clean this morning while
  silently dropping thousands of characters.

## [0.2.0] — 2026-09-15

A minor bump because **conversion output changes**: characters that were silently dropped now
appear. Nothing was removed, and no option changed.

### Fixed

- **A non-breaking hyphen is no longer dropped, so section numbers are right.**
  `<w:noBreakHyphen/>` carries the hyphen in citations like `Sec. 15-8.3`; docmd read only
  `w:t` and produced `Sec. 158.3` — a citation that is wrong and looks right. 9,371 occurrences
  in one corpus of municipal codes. **This changes conversion output** for any document
  containing one.

### Changed

- **The coverage check now counts `w:noBreakHyphen` and `w:sym`**, and will report losses it
  previously could not see. The oracle had read exactly the three elements the stylesheet read,
  so the two agreed about everything neither could see and the dropped hyphen was undetectable
  by comparing them. The rule now recorded in the code: the oracle must stay strictly more
  inclusive than the transform.

  **`w:sym` is now resolved too.** `SymbolResolver` maps the font's own encoding to a character
  and stamps it on the composite, so the stylesheet and the coverage check read one answer
  instead of each deciding for itself — the independence between those two is exactly what hid
  the hyphen. Covers Adobe's published `Symbol` encoding, and the `WP TypographicSymbols` set a
  WordPerfect-to-Word conversion emits, which turns out to carry section signs, curly quotes,
  dashes and fractions. A font or code point not in the table is still dropped and reported
  rather than guessed at.

  Consequence: **published coverage figures are a lower bound until re-measured.** A document
  containing `w:sym` will report words it did not report before. The conversion did not get
  worse; the reporting got louder.

- **A crash converting tables whose cells span columns.** `PhoenixmlDb.Xslt` 1.6.15 threw an
  unhandled `InvalidCastException` when an `xsl:for-each` ranged over a value cast from an
  attribute — `2 to xs:integer((@gs, 1)[1])`, which is how docmd pads a row from `w:gridSpan`.
  It killed the whole transform rather than degrading, so an affected document did not convert
  at all. Fixed in the engine at 1.8.0; the pin moves with it.

### Changed

- `PhoenixmlDb.Xslt` 1.6.15 → 1.8.0 (which brings `PhoenixmlDb.XQuery` 1.8.0). Verified against
  the version it replaces: 350 tests pass, the performance gate passes, engine timings are
  indistinguishable at every document size once warm-up is controlled, and a real document
  converts byte-identically to what 0.1.2 produces.

### Measurement

Coverage figures are now quoted against a **corpus id** — a content hash of the documents they
were measured on — so a later run can prove it measured the same documents. The previous record
was a list of filenames, and when the oracle changed this week, 20 of its 21 names no longer
resolved to a file. `scripts/verify-corpus.sh` produces the id, verifies a corpus is intact, and
rebuilds one by content when documents have moved or been renamed.

Measured on docmd 0.2.0 against corpus `dca5084324a9022c`: 50 of 50 convert, 27 lose not one
word, 134 words lost of ~51,800. **Not comparable to 0.1.0's figures** — both the documents and
the measurement changed.

## [0.1.2] — 2026-09-11

Packaging only. No behaviour change.

### Added

- A readme in the package. `dotnet pack` had been warning `Readme missing` since the first
  release, so the nuget.org gallery page carried nothing but the one-line description — the
  first thing a .NET developer sees, and it said almost nothing. It is a package-specific
  `PACKAGE.md` rather than the repository's README, because nuget.org renders no relative link
  and every link into `docs/` would have been dead on that page.
- Package metadata that was absent: licence expression, project URL, repository URL, and tags.

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

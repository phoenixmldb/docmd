# docmd

Converts Microsoft Word documents to Markdown — for AI/RAG indexing, for reading, or both.

```console
$ docmd report.docx -o out/
```

```markdown
---
title: Q3 Safety Review
author: A. Whitfield
created: '2026-04-11'
source: report.docx
sha256: 41ae9591…aefd
---

# Q3 Safety Review

The vent assembly is compliant with **AS9110** and *AS9100D*.

Items with a pipe \| and an asterisk \* and an under\_score in the text.

- First item
- Second item
  - Nested child

| Item | Status |
| --- | --- |
| Vent | Pass |
```

## Why another one

There are good Word-to-Markdown converters. Most of them reach Markdown through HTML, and that
intermediate hop is lossy in a way that matters specifically for retrieval.

A `.docx` is an OPC package — a ZIP of XML parts — so docmd never leaves XML until the last step:

```
report.docx  ──▶  one composite XML document  ──▶  md-XML  ──▶  Markdown
                  (body + styles + numbering        (XSLT 4.0)   (C# serialiser)
                   + relationships, resolved)
```

What survives because nothing is discarded first:

- **Headings people made with direct formatting.** Someone bolding a 16 pt line instead of applying
  `Heading 2` is the most common real-world authoring failure, and it is invisible to a converter
  that only maps named styles. docmd checks `w:outlineLvl` first, then walks the `w:basedOn`
  inheritance chain, then style names, then falls back to a formatting heuristic — recording *which*
  rule fired, so you can tell a confident heading from a guessed one.
- **List nesting**, rebuilt from `numId` and `ilvl`, which Word stores as a flat run of paragraphs
  with no tree at all.
- **Sentinel values that mean "off".** `w:b w:val="0"` is not bold, `w:outlineLvl` of `9` is body
  text, `numId` of `0` means numbering was removed. Each of these is a value Word writes to *cancel*
  a property, and reading presence as truth gets all three backwards.

Heading structure is not cosmetic here. Markdown-aware chunkers split on `#` levels and attach the
heading path to each chunk, so a missed heading silently merges two chunks and a false one shatters
a paragraph.

## Determinism

The same `.docx`, converted twice by the same version with the same options, produces byte-identical
output. No timestamps, LF endings on every platform, culture-invariant throughout — pinned by tests
that convert twice and again under `de-DE` and `tr-TR`.

That is not tidiness. These files land in git repos and RAG stores that re-index on content change;
one unstable byte re-embeds a corpus whose text never moved.

## Install

```console
$ dotnet tool install -g Docmd.Cli
$ docmd --help
```

Requires .NET 10. Accepts `.docx`, `.docm`, `.dotx` and `.dotm`. Word 97–2003 `.doc` is a different,
binary format — re-save it first (`soffice --convert-to docx legacy.doc`).

## Usage

```
docmd <input.docx> [options]

  -o, --output <path>        output directory (default: .)
      --asset-base-url <url> emit remote URLs for locally-written assets
      --img-dir <name>       image folder name (default: img)
      --no-images            omit images entirely
      --flavour <name>       gfm | commonmark (default: gfm)
      --front-matter <mode>  yaml | none (default: yaml)
      --stamp                include a conversion timestamp (breaks determinism)
  -h, --help                 -V, --version
```

Images extract to `img/<document>/`, keyed on the document's filename — so two documents with
the *same* name in different folders will overwrite each other's images, and their `.md` too.
`scripts/convert-tree.sh` mirrors the source tree to avoid that, and counts how many a flat
layout would have clobbered. `--asset-base-url` writes them
locally but references them remotely, so whatever already moves your files — `aws s3 sync`, `azcopy`
— keeps doing that job.

## Making it produce what you want

The transform is a stylesheet, not compiled code, and you can replace it. Start from the one
that actually ran rather than reconstructing it:

```console
$ docmd --print-stylesheet > mine.xslt
# edit mine.xslt — override the templates you care about, leave the rest
$ docmd report.docx --stylesheet mine.xslt
```

If you do not want to write XSLT, a style map covers the common case — telling docmd what your
template's own styles mean:

```yaml
# house-styles.yaml
CautionNote:   { as: blockquote, prefix: "Caution: " }
ProcedureStep: { as: ordered-list-item }
PartNumber:    { as: inline-code }
CorpTitle:     { as: heading, level: 1 }
```

```console
$ docmd report.docx --style-map house-styles.yaml
```

Keys match a `w:styleId` or the style name Word shows in its UI, whichever you have.
Paragraph styles take `heading` (with `level`), `blockquote`, `list-item`, `ordered-list-item`,
`code-block` and `para`; character styles take `inline-code`, `strong` and `em`. A `prefix` is
literal text and is escaped like any other document text — for Markdown in a prefix, use a
stylesheet. It applies to `heading`, `blockquote`, `code-block` and `para`; on a list or
character style it is refused rather than accepted and dropped, so a map that parses is a map
that does what it says.

A mapped style beats what docmd would have inferred, so `Heading1: { as: para }` demotes a
heading on purpose. And because a map fails silently by nature — a misspelled style id just never
matches — docmd reports both directions:

```
! style map: 'CorpTitel' matched no style in this document.
? style map: 'Style17 (Corporate Heading)' is used 24 time(s) and is not mapped.
```

The first line is a typo. The second is the conversation: it names the styles your documents
actually lean on, so you can decide what is worth mapping instead of guessing. It counts only
the uses docmd left alone — a style it already read as a heading is not a gap in your map — and
it stays quiet unless you passed a map, since it is advice about a feature you asked for.

`src/Docmd.Word/Stylesheets/markdown.xslt` is the whole semantic-recovery layer: which paragraph
is a heading, which run is bold, how a Word list becomes a Markdown one. It is about 350 lines and
it is meant to be read.

The stylesheet's own directory is its base URI, so if you split your overrides across files, a
relative `xsl:import` resolves against where those files live rather than against wherever you
happened to run `docmd` from.

We are not going to anticipate every house style. What we can do is meet the technical
interpretation of what is in the document and hand you the transform, so refining it is a matter
of editing XSLT rather than filing a feature request.

## What it does not do yet

`--review`, `-r`/`--recursive`, `docmd audit`, `--strict` and `--report` are on the
roadmap and **fail cleanly today** rather than silently doing nothing. See
[`docs/deferred-work.md`](docs/deferred-work.md) for what is inherited and why, and
[`docs/limitations.md`](docs/limitations.md) for what is knowingly dropped.

Table cells flatten inline formatting, including hyperlink URLs. That is a decision, not an
oversight — for retrieval a URL is close to worthless while the anchor text carries the meaning —
but it is a real loss and it is recorded rather than hidden.

## Built on our XSLT engine

The transform is a stylesheet. `src/Docmd.Word/Stylesheets/markdown.xslt` is the whole semantic
recovery layer, and it runs on [PhoenixmlDb.Xslt](https://www.nuget.org/packages/PhoenixmlDb.Xslt) —
an XSLT 3.0/4.0 processor written from scratch in .NET, Apache-2.0, no Java, no Saxon licence.

Building a real product on it found a real bug in it: `xsl:for-each` over a range whose operand is
an `xs:integer` cast from a string crashes 1.6.13 with an unhandled `InvalidCastException`. The
characterisation and a self-contained repro are in
[`docs/engine-defects/`](docs/engine-defects/) — including the reason a conformance suite never
caught it, which is that the literal-operand form passes and only an attribute-sourced count fails.
That is the argument for dogfooding over conformance testing, in one bug.

## Development

```console
$ dotnet build docmd.slnx
$ dotnet test  docmd.slnx        # 337 tests; 1 skipped is the opt-in corpus audit
$ dotnet run --project src/Docmd.Cli -- sample.docx -o out/
```

`TreatWarningsAsErrors` is on with `AnalysisLevel=latest-all`, so any analyzer diagnostic fails the
build. Central Package Management is in use — versions go in `Directory.Packages.props`, never on a
`PackageReference`.

Test fixtures are **unzipped directories of OPC parts**, zipped in memory at test time, so adding a
regression test is eight lines of readable XML rather than a binary nobody can review. Two fixtures
are deliberately exceptions: a real `.docx` produced by a third-party writer, and a hand-authored one
carrying Word's own noise — `w:proofErr`, `rsid` attributes, bookmarks between runs, and words split
mid-word at spell-check boundaries. Those two found bugs no hand-written fixture did.

Two differential oracles do the correctness work a golden file cannot. The serialiser one
round-trips through an independent Markdown implementation, answering *does this Markdown mean what
was intended*. The end-to-end one checks that every word a reader sees in the `.docx` still appears,
in order, in the output — it found two real defects on its first corpus run. Point it at your own
documents with `DOCMD_CORPUS`.

[CONTRIBUTING.md](CONTRIBUTING.md) covers the conventions and the four stylesheet traps that have
each cost real time.

## Documentation

- [Architecture](docs/architecture.md) — how a `.docx` becomes Markdown, and why the pipeline is
  shaped the way it is
- [Extending docmd](docs/extending.md) — style maps, custom stylesheets, and writing an
  `IAssetSink` to send images somewhere other than disk
- [PowerPoint feasibility](docs/powerpoint-feasibility.md) — measured against 960 real slides.
  **Tabled**; spreadsheets are out of scope permanently, and that file says why
- [Limitations](docs/limitations.md) — what is knowingly dropped and how large a document gets
  before conversion slows down, both measured rather than guessed
- [Deferred work](docs/deferred-work.md) — findings recorded instead of fixed, and why
- [Engine defects](docs/engine-defects/) — bugs found in our own XSLT engine by building this on
  it, with reproductions. All three are filed upstream as
  [phoenixmldb-xslt#10](https://github.com/phoenixmldb/phoenixmldb-xslt/issues/10),
  [#11](https://github.com/phoenixmldb/phoenixmldb-xslt/issues/11) and
  [#12](https://github.com/phoenixmldb/phoenixmldb-xslt/issues/12)

## Licence

Apache-2.0. See [LICENSE](LICENSE).

The patent grant is deliberate — it is what enterprise procurement asks about, and docmd is aimed at
regulated document sets where every dependency gets a legal review.

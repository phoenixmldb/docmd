# Deferred work

Findings raised during the core-conversion build that were deliberately not fixed there, kept
because later plans inherit them. Each says why it was deferred and what would close it.

## Known defects, ranked

### 1. ~~`xs:integer` on a non-numeric `w:ilvl` raises a dynamic error~~ — fixed

**[#37](https://github.com/phoenixmldb/docmd/issues/37), closed 2026-10-04** by `a9813b8`. On
`main`, not yet released. Three casts in `markdown.xslt` now test `castable as xs:integer` and
fall back: `docmd:ilvl`, the `is-ordered` level predicate, and `w:gridSpan`. Two cast tests and a
`gridSpan` test pin it.

The entry below is kept because the reasoning was wrong in an instructive way. It argued the
defect could wait, on the grounds that `w:ilvl/@w:val` is a spec-typed integer so a bad value
implies a far more deeply malformed file. That is true and it is beside the point: the invariant
breached was **one malformed document must never stop a corpus conversion**, and how rare the
input is does not change what happens when it arrives. The guard costs three lines.

### 2. ~~Four or more leading `w:tab` render as an indented code block~~ — fixed

**[#38](https://github.com/phoenixmldb/docmd/issues/38), closed 2026-10-04** by `cdc84c2`. On
`main`, not yet released. `MarkdownSerializer.TrimCodeBlockIndent` trims the leading horizontal
whitespace of a block's first physical line only when it reaches 4 columns — CommonMark's
indented-code threshold — and leaves 1 to 3 alone, because 1 to 3 are ignored by readers and
trimming them would change output for no gain.

It was **text loss, not cosmetic**: on one corpus document the coverage oracle's lost-word count
fell from 17 to 4. Two numbers are worth keeping from the fix. A broad unconditional `TrimStart`
moved 1,100 lines across the corpus, of which only 25 were the defect — which is why the
threshold is in the code rather than in a comment. And the scope guards had to move to
`MarkdownSerializerTests`, because the test written for them in `MarkdownStylesheetTests` was
vacuous: it used `HTMLPreformatted`, which never produces `md:code-block` without a style-map
rule, so it had been asserting on an ordinary indented paragraph all along. Only this change
broke it into honesty.

### 3. ~~Text inside inline content controls, fields and smart tags is dropped~~ — FIXED

Fixed by commit `a3e3558`; verified end-to-end and pinned by tests on 2026-10-01. The paragraph
templates descend through inline `w:sdt`, `w:fldSimple` and `w:smartTag`, and deliberately do not
descend into `w:instrText` or `w:del`. The stray blank line that accompanied the loss went with
it. See `docs/limitations.md` for the policy and its measured basis.

This entry predicted the work needed frequency data from a corpus audit before the policy could
be chosen. It did not: the oracle had already decided it. `docmd:text-nodes` walks the descendant
axis, so `TextCoverageReport` already counted this text as words a reader can see — a transform
that could not reach it did not merely omit it, it reported itself as lossy.

Worth recording what the fix left behind. `a3e3558` pinned only its text-box half; the wrapper
half shipped unprotected for eight releases, v0.1.0 through v0.2.4, and `ListStylesheetTests`' content-control test
covers the *block-level* form, which was never the broken one. Six tests now cover it.

### 4. `--stylesheet` runs a caller's stylesheet with no time limit, and cancelling does not stop it

Open, found 2026-10-07 while verifying the Xslt 2.7.0 bump. `--stylesheet` ships and is fully
wired; `MarkdownTransform.RunAsync` compiles whatever file it is handed. It sets neither
`XsltTransformer.RegexMatchTimeout` nor `ResourcePolicy`, and `LoadStylesheetAsync` takes no
cancellation token at all, so the compile phase has no lever either.

Measured on the engine, not inferred. A stylesheet whose whole running time sits inside one
`matches()` call with a catastrophically backtracking pattern:

| Lever set | Outcome |
|---|---|
| Cancellation token only | ran **91,227 ms** to completion; the token had no effect |
| `RegexMatchTimeout = 2s` | stopped at **2,013 ms**, naming the expression in the message |
| Both | stopped at **2,002 ms** |

Identical on 2.5.1 and 2.7.0, so no pin bump closes it — `GHSA-h2xc-4m53-6j8r` and
`GHSA-xxjq-rwpx-m5ww` make the token and the timeout reach places they previously did not, but a
timeout nobody sets still does not fire. docmd's *built-in* stylesheet is not affected: it returns
to a template often enough that cancelling a conversion of the largest corpus document already
works (requested at 500 ms, `OperationCanceledException` at 1,624 ms, on 2.5.1).

Closes with: a `RegexMatchTimeout` whenever `--stylesheet` or `--style-map` is given, a
`ResourcePolicy` alongside it, and a fixture that asserts a backtracking pattern is abandoned
rather than waited on. The timeout wants to be a flag rather than a constant — a legitimate
stylesheet on a large document can spend real time in a regex.

### 5. Table cells lose inline formatting, including hyperlink URLs
A decided product trade-off, not an oversight — for retrieval a URL is near-worthless and often
already dead, while the anchor text survives. Recorded so the corpus audit can report it
("N tables contained hyperlinks flattened to text") rather than losing it silently.

## Smaller items

- `ConversionOptions.ImageDirectoryName` is carried but `AssetRewriter` hardcodes `img/`. Note this
  is an *inversion*, not just a deferral: the option lives in `Docmd.Word` while the behaviour
  lives in Core, so Core structurally cannot honour it. When threaded through, it belongs in a
  Core-side options type.
- `--flavour commonmark` is parsed and plumbed, but the serialiser emits GFM tables regardless.
- ~~Ordered lists always restart at `1.`; a numbered procedure interrupted by a note paragraph
  renumbers from the top.~~ **Fixed** —
  [#39](https://github.com/phoenixmldb/docmd/issues/39), closed 2026-10-04 by `c225d2d`, on
  `main` and not yet released. `build-list` counts the preceding level-0 items of the same
  `w:numId` and emits `md:list/@start`, which the vocabulary already defined and the serialiser
  already read. Worth remembering why nothing caught it: the oracle counts words, and no word
  was missing. A lossless check cannot see a wrong number. Also worth remembering that the
  corpus could not confirm the fix either — it contains **zero** `w:numPr` paragraphs, so the
  "0 of 13 documents changed" reading was vacuous, and the cost had to be measured on a
  synthetic scaling probe against a control on `main`.
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
- `docmd audit`, `-r`/`--recursive`, `--review`, `--style-map`, `--strict` and `--revisions`
  all currently fail cleanly as not-yet-supported. (`--report` shipped in 0.2.3 and is no longer
  on this list.) `--style-map` is unblocked: `phoenixmldb-xslt#12` is closed, so its workaround
  can go. When it ships it should set an `XsltTransformer.ResourcePolicy` — running a stylesheet
  somebody else wrote is untrusted code, and policy enforcement across reads, fetches and
  `xsl:evaluate` landed in Xslt 2.5.1 (GHSA-86rg-wxgp-9p5j). Note that the shipping flag with
  this exposure is `--stylesheet`, not `--style-map`; see defect 4.

## xunit v3 4.0 needed a test-platform migration, not a version bump

**Status:** closed 2026-09-29. On xunit.v3 4.0.1, running on Microsoft.Testing.Platform 2.4.0.

xunit.v3 4.0 drops VSTest on the .NET 10 SDK. The error names a property that sounds like the
fix and is not:

```
error : Testing with VSTest target is no longer supported by Microsoft.Testing.Platform
on .NET 10 SDK and later. If you use dotnet test, you should opt-in to the new dotnet
test experience.
```

`TestingPlatformDotnetTestSupport=true` is the pre-.NET-10 bridge: it redirects the VSTest
target into `InvokeTestingPlatform`. MTP 2.4.0 removed that escape hatch, and the removal is
what produces the error above. `Microsoft.Testing.Platform.MSBuild.targets` raises it from
`_MTPBeforeVSTest`, guarded on nothing but the SDK major version and a variable named
`_SupportsGlobalJsonTestRunner` — so the property cannot suppress it, and the refusal is
conditioned on a *better* opt-in being available. The SDK agrees: `dotnet test --help` on
10.0.401 says to opt in "via global.json".

**What it actually took**, which was less than this entry predicted:

- `global.json` gains `"test": { "runner": "Microsoft.Testing.Platform" }`. Not `dotnet.config`,
  which this entry guessed at; that is a different mechanism and not what this SDK reads.
- Three packages removed rather than bumped, all VSTest-only and inert under MTP:
  `Microsoft.NET.Test.Sdk`, `xunit.runner.visualstudio`, `coverlet.collector`. Reasoning is in
  `Directory.Packages.props`. Coverage, if anyone ever wants a number, is `coverlet.mtp` or
  `Microsoft.Testing.Extensions.CodeCoverage`; nothing here has asked for one.
- `<OutputType>Exe</OutputType>` for test projects, which `Microsoft.NET.Test.Sdk` used to supply
  implicitly. It is in `Directory.Build.props` conditioned on the project name, because
  `IsTestProject` is set long after `.props` is evaluated and a condition on it never matches.
- **The filter rewrite was unnecessary.** This entry expected `--filter
  "FullyQualifiedName~X"` to change in four places. xunit.v3 4.x ships `--filter` accepting
  VSTest syntax, so CI, `CONTRIBUTING.md` and the corpus-audit instructions are unchanged and
  still correct. The restriction is that a VSTest filter cannot be combined with xunit's own
  `--filter-class` / `--filter-query` forms, which nothing here does.

**What we gained rather than paid for:** a run that executes no tests now exits 8 instead of
passing. A filter typo used to be a silent green — measured, not assumed:
`--filter "FullyQualifiedName~NoSuchTestNameAtAll"` exits 8, and the real suite exits 0.

Verified on 2026-09-29, against this commit: 363 tests — 362 pass, 1 skip (the corpus audit),
which is the pre-migration count of 362 plus the one performance test the old filter excluded.
`--locked-mode` restore clean; the performance gate still runs alone. The count is recorded as a
dated measurement rather than as a standing fact; the suite is the fact.

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

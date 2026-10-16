# Known limitations

Behaviour docmd currently gets wrong, deliberately recorded rather than quietly carried.
Each entry says what is lost, why it is not fixed yet, and what the corpus audit must count
so the decision to leave it can be revisited with numbers instead of guesses.

**Measured, not estimated.** docmd checks every conversion: it compares the words a reader can
see in the `.docx` against the words a Markdown parser recovers from the output, and reports any
that did not survive. Measured 2026-10-15 on docmd 0.3.0, against corpus `394b2cb31e7459a1`:

| | |
|---|---|
| Documents converted | 49 of 49, none failed |
| Documents losing not one word | **45** |
| Words lost, all documents | **21** of 245,034 — 0.009% |
| Worst document, by words | 11 words (99.8% kept) |
| Worst document, by share | 2 of 177 words (98.9% kept) |
| Sampled files that are not Word packages | 1 of 50 — a Word 97-2003 binary named `.docx` |

**What the 21 words were**, by cause, each one already described in a section below:

| Words | Document | Cause |
|---|---|---|
| 11 | one of 6,179 words | unmapped `w:sym` characters — the symbol-font encoding has no honest mapping |
| 7 | one of 1,325 words | words fused at a figure-caption boundary, so the oracle counts the pair as missing |
| 2 | one of 177 words | text inside `<pict>` and `<shape>` drawing wrappers |
| 1 | one of 118,338 words | a bare URL |

Nothing new appeared, which is the useful result: every loss falls into a cause this file already
records, and the two causes 0.3.0 fixed produced no losses at all.

The corpus id is a content hash produced by `scripts/verify-corpus.sh`, and it is quoted here so
the figure can be re-run against the same documents.

**Twice now, the corpus behind an earlier figure could not be re-measured.** The record before
`verify-corpus.sh` existed was a list of filenames, and when the measurement changed, 20 of its
21 names no longer resolved to a file. `dca5084324a9022c` was recorded by content hash instead —
and it is equally unreachable, because the `_source.tsv` that held those hashes was not kept. An
id *recognises* a corpus; it cannot reconstruct one. The manifest has to be kept with the figure.

**Not comparable to the 0.2.0 figures** (27 of 50, 134 words of ~51,800). Different documents and
a different converter: the tab-indented code block below was worth 17 words on one document of
that corpus by itself. The gap between the two figures measures nothing.

## ~~Text inside transparent wrappers is dropped~~ — fixed

**Status:** fixed. **Found:** whole-branch review of `feat/core-conversion`, 2026-09-04.
**Fixed:** commit `a3e3558`, "read the wrappers that hide legitimate text". **Verified and
pinned:** 2026-10-01.

WordprocessingML has elements that wrap runs without changing what a reader sees. The child axis
does not descend through them, so an earlier `markdown.xslt` — which selected
`w:r | w:ins | w:hyperlink` from a paragraph — lost every run inside one:

| Element | What it wraps | What Word shows |
|---|---|---|
| `w:sdt` (**inline only** — a child of `w:p`) | a content control inside a paragraph | the control's current text |
| `w:fldSimple` | a field with its cached result | the result |
| `w:smartTag` | a recognised entity | the words, unchanged |

A block-level `w:sdt` was never affected: one wrapping whole paragraphs sits in `w:body`, where
the built-in rules walk into it.

**What it does now.** The paragraph templates select all three wrappers, and a template for each
contributes its children; they recurse, so nesting works. This input, which previously produced
`Field:` and nothing else, now yields every string:

```xml
<w:p><w:sdt><w:sdtPr/><w:sdtContent><w:r><w:t>Inside control</w:t></w:r></w:sdtContent></w:sdt></w:p>
<w:p><w:r><w:t>Field:</w:t></w:r><w:fldSimple w:instr="DOCPROPERTY Title"><w:r><w:t>CachedResult</w:t></w:r></w:fldSimple></w:p>
<w:p><w:smartTag><w:r><w:t>Acme Corp</w:t></w:r></w:smartTag></w:p>
```

**The whitelist stops where a reader stops.** `w:instrText` holds a field code rather than its
result, and `w:del` holds text the author removed under track changes. Neither is emitted:
putting `PAGE \* MERGEFORMAT` or a deleted price into a document someone indexes invents content,
which is worse than missing it. The policy was chosen from measurement, not taste — across the
corpus the stylesheet was built against, the cached results were 103 `DOCPROPERTY`, 12 `SEQ`, a
`TITLE` and an `AUTHOR`, with no `PAGE` and no `TOC`. Every one of them content.

It remains a whitelist and will therefore always be incomplete, because OOXML keeps growing. The
text-coverage check is what makes that survivable: a document whose words go missing says so.

**The stray blank line went with it.** Two axes used to disagree about a paragraph's text. The
empty-paragraph suppression rule asks `docmd:visible-text` (descendant axis) and so *saw* this
text and declined to suppress, while the inline emitters used the child axis and could not reach
it — so the words were lost *and* a blank line was left where they had been. Emitting the text
resolves both. Fixing the blank line alone would have made the loss less visible rather than
less real.

**Pinned by** `MarkdownStylesheetTests`: `InlineContentControl_KeepsItsText`,
`InlineContentControl_AsAWholeParagraph_LeavesNoBlankLine`, `FieldResult_IsEmitted`,
`SmartTag_KeepsItsWords`, `FieldCode_IsNotEmitted` and `NestedWrappers_AreAllTransparent`.
`a3e3558` shipped the fix with tests for the text-box half only; the wrapper half then went
unpinned through every release from `v0.1.0` to `v0.2.4` — eight of them. Reverting the
select-list half of that commit fails five of the six.

## Conversion cost climbs faster than document size above ~4,000 paragraphs

**Status:** open, and accepted for now. **Measured:** 2026-09-05.

Cost per paragraph is flat up to roughly 4,000 paragraphs and rises after it. Measured by
growing a real 470-paragraph statement of work and converting each size:

| Paragraphs | `document.xml` | Time | Peak RSS | Per paragraph |
|---:|---:|---:|---:|---:|
| 470 | 212 KB | 10 s | 121 MB | 21 ms |
| 940 | 424 KB | 13 s | 142 MB | 14 ms |
| 1,880 | 849 KB | 23 s | 163 MB | 12 ms |
| 3,760 | 1.7 MB | 52 s | 213 MB | 14 ms |
| 7,520 | 3.4 MB | 168 s | 343 MB | 22 ms |
| 11,280 | 5.1 MB | 327 s | 453 MB | 29 ms |

Between 3,760 and 11,280 paragraphs the curve is about **n^1.7**. Extrapolating, 20,000
paragraphs is roughly fifteen minutes and 50,000 roughly an hour and a half — though that is an
extrapolation, and the last one made from this data (a supposed memory wall) turned out to be
wrong because the curve was not the shape it looked like from two points.

**It degrades rather than failing.** No crash, no exhaustion, no wrong output: memory tops out
at 453 MB for a 5 MB document and grows sublinearly, so the machine is never the constraint.
The limit is patience, not capacity.

**Where real documents sit.** A 1,000-paragraph design document converts in about 19 seconds,
and a 32-document sample of ordinary business documents averaged about 6 seconds each. Nothing
in normal office use approaches the knee. What does approach it is a single long technical
manual — a 300-page S1000D or specification runs to 10,000 paragraphs or more.

**Why it is accepted rather than fixed.** For a corpus converted once and then indexed, this is
a one-time cost amortised over the life of the index, and the work being done is the product:
heading detection through style inheritance, list reconstruction, style mapping. A pure text
extractor is faster because it answers a smaller question.

It stops being acceptable at volume in the large-document regime — one 300-page manual at
fifteen minutes is fine, two hundred of them is not. Anyone in that regime should measure before
committing to a batch window.

**What would move it:** a residual superlinear term in the transform, not yet located. It is
known *not* to be the `w:p` match patterns (neutering all three buys 15%), not
`xsl:for-each-group`, not the style-map lookups, not template count, and not `xsl:strip-space` —
each ruled out by measurement. A trivial stylesheet processes the same documents at a flat
0.23 ms per paragraph, so the engine is capable of linear behaviour on this input.
`TransformScalingTests` guards against the curve getting worse.

## ~~Adjacent emphasis spans emit ambiguous delimiter runs~~ — fixed

**Status:** fixed 2026-09-05, the same day the text-preservation oracle found it.

The heading above was the first diagnosis and it was wrong. Adjacent emphasis is fine:
`**bold***italic*` renders correctly. The defect was emphasis whose content carries **no letter
or digit**, which breaks the moment it touches a non-space character on either side — including
intraword, with no adjacency involved at all:

```
**b***.*    renders as  b*.*         x*.*y    renders as  x*.*y
**b***,*    renders as  b*,*         x**.**y  renders as  x**.**y
**b***a*    renders as  bolditalic   ok, because the content is a word
```

Word leaves a full stop italic whenever the sentence before it was italicised, which is an
artifact of how text gets selected. One 2008 program guide lost 3,440 of its 4,664 words to the
pattern.

`MarkdownSerializer` now emits no delimiters around a span with no letters or digits, extending a
guard that already existed for whitespace-only spans. The text is preserved exactly; only markup
that Markdown cannot express reliably is dropped, and an italic full stop is not worth corrupting
a sentence to attempt.

Corpus effect at the time: it removed the single largest source of loss in the sample.

## ~~Text inside a text box is dropped~~ — fixed

**Status:** fixed. **Found:** the text-preservation oracle's first corpus run, 2026-09-05.
**Fixed:** commit `a3e3558`. **Verified:** 2026-10-01.

`w:txbxContent` holds paragraphs, but it sits inside `w:p/w:r/w:pict` (or `mc:AlternateContent`),
so its paragraphs are not children of `w:body`. Nothing reached into a text box and every word
inside one was lost. Measured on the 49-document sample, one document contained 236
`w:txbxContent` elements — text boxes are how pull quotes, callouts and diagram labels are
authored, so the loss was concentrated in exactly the summarising sentences a retrieval index
most wants.

Text-box content now becomes blocks after its anchor paragraph. A paragraph holding only a text
box does not also emit a blank paragraph, and a text box inside a table cell is not counted
twice. Pinned by `TextBoxContent_BecomesBlocksAfterItsAnchorParagraph`,
`AParagraphHoldingOnlyATextBox_DoesNotAlsoEmitABlankParagraph` and
`ATextBoxInsideATableCell_IsNotCountedTwice`.

## ~~A paragraph beginning with four or more tabs becomes a code block~~ — fixed

**Status:** fixed in 0.3.0 — [#38](https://github.com/phoenixmldb/docmd/issues/38).
**Found:** the corpus audit, where it cost 17 words on one document. **Fixed:** commit `cdc84c2`,
"a four-space indent no longer turns a paragraph into code". **Verified:** the 2026-10-15 audit
above, where no document loses a word to this cause.

The account below is the defect as it stood. `MarkdownSerializer` now owns the
leading-whitespace policy it describes: it trims the leading whitespace of a block's first
physical line when that line reaches 4 columns, which is CommonMark's indented-code threshold,
and leaves 1 to 3 alone.

`w:tab` now emits one space (see the commit "Stop the stylesheet losing words"). A paragraph
that opens with four or more consecutive tabs therefore starts its Markdown line with four or
more spaces, which CommonMark reads as an indented code block: the words survive verbatim but
are marked up as code, and a retrieval index sees a code fence where prose was intended.

Real but rare — deeply tab-indented paragraphs are a manual-layout habit, not a Word feature —
and the fix is not local. `MarkdownSerializer` would have to own a leading-whitespace policy
(collapse? escape? emit a `&nbsp;`?), which interacts with `EscapeLineStart`, with list-item
continuation indentation, and with the byte-identical output guarantee. That is too much
surface to move immediately before a merge.

**What the audit must count:** paragraphs whose first inline node sequence yields four or more
leading spaces, and how many of those are inside a list item (where the indentation means
something different again).

## ~~A non-numeric `w:ilvl` still raises a dynamic error~~ — fixed

**Status:** fixed in 0.3.0 — [#37](https://github.com/phoenixmldb/docmd/issues/37).
**Found:** pre-existing, reproduced 2026-10-01, which confirmed it exited 1 with no output file
rather than degrading. **Fixed:** commit `a9813b8`, "guard every integer cast that reads a
document value" — `docmd:ilvl`, the `is-ordered` level predicate and `w:gridSpan` each fall back
instead of raising. **Verified:** the 2026-10-15 audit above, where every sampled document
converted without throwing.

The account below is the defect as it stood.

`docmd:ilvl` does `xs:integer(($p/w:pPr/w:numPr/w:ilvl/@w:val, '0')[1])`. An attribute present
but not a number (`w:val="one"`, or empty) is a cast failure, which propagates out of
`ConvertAsync` and ends the batch run — the same failure mode as the duplicate-`w:numId` case
fixed in this wave, one function along.

Left alone for now because it is a strictly more malformed document than the duplicate-`numId`
case: a duplicate id is something Word itself produces when merging files, whereas a
non-numeric `w:ilvl` is not something any Word version writes. The repair is the same shape,
though — a guarded cast falling back to 0 — and should be made when the batch runner exists to
report the degradation rather than swallow it.

**What the audit must count:** `w:ilvl/@w:val` values that do not cast to `xs:integer`, and
the documents they appear in.

## Characters dropped inside a word: `w:sym`

`<w:sym/>` carries a visible character without being `w:t`. Its `w:char` is a code point in the
**font's own encoding**, not Unicode, so for a legacy font such as `WP TypographicSymbols` there
is no honest mapping from the file to a character. docmd drops it rather than guess: emitting a
plausible-looking wrong character is worse than emitting none, because nothing downstream can
tell.

**It is reported.** The coverage check counts these, so a document containing them reports the
affected words as lost and names `sym` as the cause. That is the intended behaviour for a
construct we cannot read: omit, and say so.

On a corpus of municipal codes `w:sym` occurs 2,807 times, mostly section symbols and dashes in
tables of contents.

### Fixed in an earlier release: `w:noBreakHyphen`

Kept here because the *reason* it went unnoticed matters more than the bug.

`<w:noBreakHyphen/>` is the hyphen in a section number like `Sec. 15-8.3`. docmd dropped it,
producing `Sec. 158.3` — a citation that is wrong and looks right. 9,371 occurrences in one
corpus, and **every affected document reported clean coverage**.

The silence was structural, not an oversight. The coverage oracle read `w:t`, `w:tab` and `w:br`;
the stylesheet read the same three. Two readers with the same blind spot agree about everything
neither can see, so comparing them could not detect the loss. Word-level comparison made it
worse: `15-8.3` becoming `158.3` is one word in and one word out, so even the tokens matched.

The rule that came out of it: **the oracle must be strictly more inclusive than the transform.**
An element counted by the oracle and not emitted by the stylesheet reports a loss, which is the
correct direction for the error to point. `w:sym` above is that rule working.

# Known limitations

Behaviour docmd currently gets wrong, deliberately recorded rather than quietly carried.
Each entry says what is lost, why it is not fixed yet, and what the corpus audit must count
so the decision to leave it can be revisited with numbers instead of guesses.

**Measured, not estimated.** docmd checks every conversion: it compares the words a reader can
see in the `.docx` against the words a Markdown parser recovers from the output, and reports any
that did not survive. On the 49-document sample it was built against:

| | |
|---|---|
| Documents converted | 49 of 49, none failed |
| Documents losing not one word | **40** |
| Words lost, all documents | **207** — about 0.15% of the corpus |
| Worst document | a 184-word invoice losing 72 |

The entries below are what accounts for the rest.

An earlier revision of this file reported 3.15% loss and a document losing 92.5% of itself. Those
figures were wrong. They came from a sequence-alignment check that mis-paired a repeated common
word, advanced past everything between, and reported the remainder as missing; on one document it
turned a real loss of 8 words into a claim of 3,307. The measurement now counts occurrences
instead, which cannot cascade.

## Text inside transparent wrappers is dropped

**Status:** open. **Found:** whole-branch review of `feat/core-conversion`, 2026-09-04.

`markdown.xslt` emits inline content by selecting the *children* of a paragraph:

```xml
<xsl:apply-templates select="w:r | w:ins | w:hyperlink" mode="inline"/>
```

WordprocessingML has several elements that wrap runs without changing what the reader sees.
The child axis does not descend through them, and no template matches them, so every run
they contain contributes nothing at all:

| Element | What it wraps | What Word shows |
|---|---|---|
| `w:sdt` (**inline only** — a child of `w:p`) | a content control inside a paragraph | the control's current text |
| `w:fldSimple` | a field with its cached result | the result, e.g. a page number or a cross-reference |
| `w:smartTag` | a recognised entity | the words, unchanged |

**A block-level `w:sdt` is not affected.** One that wraps whole paragraphs sits in `w:body`,
where the built-in rules walk into it and the paragraphs inside reach the template rules
normally, so their text survives — pinned by
`ListStylesheetTests.NumberedParagraphInsideAContentControl_KeepsItsText`. Only the inline
form, where the control is a child of `w:p` and its runs are therefore not children of `w:p`,
is dropped. Scoping this row to the inline form matters: read as "inline or block" it
overstates the loss and would mis-size the work below.

Measured against the stylesheet at the time of writing, this input (all three wrappers below
are the inline form — each is a child of `w:p`):

```xml
<w:p><w:sdt><w:sdtPr/><w:sdtContent><w:r><w:t>Inside control</w:t></w:r></w:sdtContent></w:sdt></w:p>
<w:p><w:r><w:t>Field:</w:t></w:r><w:fldSimple w:instr="PAGE"><w:r><w:t>7</w:t></w:r></w:fldSimple></w:p>
<w:p><w:smartTag><w:r><w:t>Acme Corp</w:t></w:r></w:smartTag></w:p>
```

produces `Field:` and nothing else. "Inside control", "7" and "Acme Corp" are gone.

This matters more than the element names suggest. Content controls are how templated
corporate documents carry their variable parts — the customer name, the revision, the
approval block — so the fields most worth indexing are exactly the ones most likely to be
inside one.

### The related inconsistency: a stray blank line

(Inline `w:sdt` again — a block-level one never reaches this rule as a paragraph's whole
content.)

Two places in the stylesheet disagree about which axis counts as "the text of a paragraph".
The empty-paragraph suppression rule asks `docmd:visible-text`, which is `.//w:t` and
therefore *does* see inside a `w:sdt`:

```xml
<xsl:template match="w:p[not(normalize-space(docmd:visible-text(.)))][not(.//w:drawing)]" priority="1"/>
```

So a paragraph whose only content is a `w:sdt` is not suppressed (it has visible text), then
emits an empty `md:para` (the inline emitters cannot reach that text), and the serialiser
turns that into a blank line. The paragraph's words are lost *and* a stray blank line is
left where they were.

### Why it is not fixed here

Descending through transparent wrappers is a real design decision, not a one-line patch:
`w:fldSimple` needs a policy for which fields have a meaningful cached result and which are
noise (`PAGE`, `DATE`), an inline `w:sdt` needs a decision about whether its `w:sdtPr`
placeholder text counts as content when the control is unfilled, and `w:hyperlink` already
sets a precedent for how a wrapper contributes its children. That work belongs with the plan
that also builds the corpus audit, so the choice can be made against measured frequencies.

### What the audit must count

- paragraphs containing at least one **inline** `w:sdt` (`w:p/w:sdt`), `w:fldSimple` or
  `w:smartTag`, as a share of all paragraphs — block-level `w:sdt` is out of scope, since its
  text already survives
- characters of `w:t` unreachable from the child axis, as a share of all `w:t` characters
- `w:fldSimple` occurrences by `w:instr` keyword, so the "which fields carry content" policy
  is chosen from data
- paragraphs emitting an empty `md:para` despite having non-empty `docmd:visible-text` — the
  stray-blank-line case above, which is a direct count of the inconsistency

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

## Text inside a text box is dropped

**Status:** open. **Found:** the text-preservation oracle's first corpus run, 2026-09-05.

`w:txbxContent` holds paragraphs, but it sits inside `w:p/w:r/w:pict` (or `mc:AlternateContent`),
so its paragraphs are not children of `w:body`. The `w:body` template groups over `*` — top-level
children only — and the inline templates select `w:r | w:ins | w:hyperlink` from a paragraph, so
nothing reaches into a text box. Every word inside one is lost.

Measured on the 49-document sample: one document contained 236 `w:txbxContent` elements. Text
boxes are how pull quotes, callouts and diagram labels are authored, so the loss is concentrated
in exactly the summarising sentences a retrieval index would most want. docmd now counts them and
says so when a document loses words, rather than leaving the reader to wonder where the text
went.

**What the audit must count:** `w:txbxContent` occurrences per document, and characters of `w:t`
inside them as a share of all `w:t` characters.

## A paragraph beginning with four or more tabs becomes a code block

**Status:** open, parked deliberately at the end of the core-conversion wave.

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

## A non-numeric `w:ilvl` still raises a dynamic error

**Status:** open, pre-existing — not introduced by the core-conversion wave.

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

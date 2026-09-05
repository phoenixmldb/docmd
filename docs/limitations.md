# Known limitations

Behaviour docmd currently gets wrong, deliberately recorded rather than quietly carried.
Each entry says what is lost, why it is not fixed yet, and what the corpus audit must count
so the decision to leave it can be revisited with numbers instead of guesses.

**Measured, not estimated.** `TextPreservationTests.Corpus_Audit` checks that every word a
reader sees in a `.docx` still appears in the Markdown. On the 49-document sample it was built
against, **36 convert without losing a single word**. The entries below are what accounts for
the other 13.

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

## Adjacent emphasis spans emit ambiguous delimiter runs

**Status:** open. **Found:** the text-preservation oracle's first corpus run, 2026-09-05.

A bold run immediately followed by an italic run produces `**bold***italic*`. CommonMark reads
the three asterisks as a *single* delimiter run, so it does not close the bold and open the
italic; the emphasised text is swallowed into markup and disappears from the rendered document.

Real example, from a 2008 program guide: the source ends a bold sentence and then sets the full
stop in italics, which Word does routinely.

```
source:  <w:r><w:b/><w:t>...monthly MCT Flash newsletter</w:t></w:r>
         <w:r><w:i/><w:t>.</w:t></w:r>

emitted: newsletter***.*

read as: the full stop is gone
```

That single document lost 3,440 of 4,664 words to this one pattern, because bold-then-italic
recurs throughout it. It is the largest single source of text loss measured so far.

`MarkdigOracleTests` did not catch it: it round-trips emphasis spans one at a time, and the
defect only exists *between* two adjacent spans.

**Why it is not fixed here:** the repair is a real choice, not a patch. Switching `em` to `_`
fixes adjacency but breaks intraword emphasis, which `_` cannot express. Separating the spans
with an empty HTML comment works in CommonMark but puts markup in the output where the document
had none. Emitting the second span's delimiter only when the preceding character is not an
asterisk is the narrowest fix and needs its own oracle cases. Whichever is chosen, it changes
the bytes of every document containing adjacent emphasis, so it wants doing deliberately.

**What the audit must count:** runs whose emphasis differs from the immediately preceding run's
with no separating text, by document.

## Text inside a text box is dropped

**Status:** open. **Found:** the text-preservation oracle's first corpus run, 2026-09-05.

`w:txbxContent` holds paragraphs, but it sits inside `w:p/w:r/w:pict` (or `mc:AlternateContent`),
so its paragraphs are not children of `w:body`. The `w:body` template groups over `*` — top-level
children only — and the inline templates select `w:r | w:ins | w:hyperlink` from a paragraph, so
nothing reaches into a text box. Every word inside one is lost.

Measured on the 49-document sample: one document contained 236 `w:txbxContent` elements. Text
boxes are how pull quotes, callouts and diagram labels are authored, so the loss is concentrated
in exactly the summarising sentences a retrieval index would most want.

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

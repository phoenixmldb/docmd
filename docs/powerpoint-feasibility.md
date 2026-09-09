# PowerPoint: what a `.pptx` front end would actually cost

**Status: tabled 2026-09-09.** Feasible, measured, and deliberately not started. See the decision
below. Spreadsheets are ruled out permanently.

Findings from surveying **40 real decks, 960 slides**, drawn at random from a 337-file collection.
Written before any code, so the scope is argued from measurement rather than from the spec.

## The short version

The plumbing transfers. The semantics do not.

`Ooxml.Md.Core` — OPC reading, the md-XML vocabulary, the serialiser, escaping, slugs, asset
sinks, frontmatter, and the text-coverage check — is format-agnostic and would be reused whole.
What has to be written from nothing is the composer and the stylesheet, because a presentation
does not answer the questions a document answers.

## PowerPoint text is a different vocabulary

Word text is WordprocessingML. Slide text is **DrawingML**:

| | Word | PowerPoint |
|---|---|---|
| paragraph | `w:p` | `a:p` |
| run | `w:r` | `a:r` |
| text | `w:t` | `a:t` |
| table / row / cell | `w:tbl` / `w:tr` / `w:tc` | `a:tbl` / `a:tr` / `a:tc` |

Not one line of `markdown.xslt` matches a slide. The transform is a new file, not an extension of
the existing one — which is an argument for a sibling `Docmd.Presentation` project rather than
options on the Word one.

Slides are also separate parts (`ppt/slides/slideN.xml`), so the composer gathers N parts where
Word's gathers one.

## There is no reading order

This is the real work, and it has no equivalent in Word.

A Word document is a linear sequence of block elements; document order *is* reading order. A
slide is a bag of shapes at absolute positions, stored in the order they were inserted. The
survey found **9,610 shapes across 960 slides**, about ten per slide. Nothing in the file says
which to read first.

Reading order has to be *inferred* from geometry — a shape's `a:off` x and y, banded into rows
then ordered left to right, with the title placeholder hoisted regardless of position. That is a
heuristic, it will be wrong sometimes, and it is the single biggest correctness risk in the
feature. Word's heading detection at least had five explicit signals to consult; this has none.

## The structure signal exists, but covers 70%

`p:ph type="title"` is the slide title, and it is the natural `#` heading:

| placeholder | count |
|---|---|
| `title` | 650 |
| `body` | 252 |
| body (index only, untyped) | 231 |
| `sldNum` | 61 |
| `ctrTitle` | 22 |
| `subTitle` | 13 |
| `ftr` | 4 |

**672 of 960 slides (70%) carry a title.** The other 30% need a fallback — first text shape,
largest font, or no heading at all — and the choice affects chunk boundaries in a retrieval
index, which is the whole product argument.

Note `sldNum` and `ftr`: slide numbers and footer chrome, repeated on every slide. They are the
PowerPoint equivalent of a `PAGE` field and must be excluded, or every slide's text ends with a
stray number.

## Four constructs that would silently lose text

Each is the same shape of problem docmd already solved for Word, which is the encouraging part —
the lesson transfers even though the code does not.

| construct | share of decks | why it matters |
|---|---|---|
| **grouped shapes** (`p:grpSp`) | 23 / 40 (58%) | **10.6% of all text runs sit inside a group.** A naive walk of `spTree/p:sp` loses a tenth of every deck. |
| **`mc:AlternateContent`** | 26 / 40 (65%) | Two branches holding the same content. Read both and you duplicate; read neither and you lose it. docmd already has this fix from the Word side. |
| **SmartArt** (`dgm:relIds`) | 10 / 40 (25%) | Text lives in a *separate diagram part*, not in the slide. One deck's opening slide was entirely SmartArt: nothing in the slide XML, all of it in `ppt/diagrams/`. |
| **tables** (`a:tbl`) | 15 / 40 (38%) | Same shape as Word tables but a different vocabulary. |

Also present: pictures in 32/40, hyperlinks in 21/40, charts in 4/40, embedded objects in 9/40.

The coverage check is what makes this survivable. It is format-agnostic — it compares source text
against recovered text — so it would report a slide losing its SmartArt on day one rather than
after a customer noticed.

## Speaker notes: a product decision, not a technical one

**Notes appear in 32 of 40 decks (80%).** They are a separate part per slide and they routinely
contain the substance a slide only gestures at — the argument, the caveats, the numbers.

For retrieval they are arguably the most valuable text in the file. For a reader they are
backstage. The options are to omit, append per slide under a marker, or emit a parallel document.
Whichever is chosen it should be a flag, and the default matters more than the flag.

## The other decision: one file or one per slide

A deck has no natural document boundary. One `.md` per deck keeps the narrative and produces
sensible chunks when slides are short. One per slide gives clean retrieval boundaries and
produces a directory of 40 stubs for a 40-slide deck.

This is a chunking decision dressed as a file-layout decision, and it should be made against a
real index rather than in the abstract.

## Decision, 2026-09-09

**PowerPoint is tabled.** Not rejected — the survey says it is feasible and the plumbing is
already there — but not started while the Word side is unreleased. Reading order is novel work
with no equivalent in what exists, and beginning it now would widen an unfinished front. This
document is the input to restarting it, which is why the measurements are here rather than in a
chat log.

**Spreadsheets are out of scope, permanently.** `.xlsx` shares OPC and looks adjacent, which is
exactly why it needs saying once rather than being reconsidered every quarter. A spreadsheet is a
grid without prose: the output would be tables with no narrative, close to worthless for
retrieval, and it would earn "docmd handles Excel badly" as a review while adding a format to
maintain. Anyone tempted should read this paragraph first.

**When PowerPoint restarts, it is one tool dispatching on extension.** `docmd deck.pptx` should
work. Real document stores are mixed and a corpus conversion wants one pass over one folder.
Structurally that is `Docmd.Presentation` beside `Docmd.Word`, both feeding `Ooxml.Md.Core`, with
the CLI selecting. The extension points stay learned once.

## What to build first, when the time comes

1. The composer and a coverage baseline — gather slides, notes, diagrams, and measure how much
   text a do-nothing transform recovers. That number sets the target before any stylesheet exists.
2. Reading order, tested against decks with known-correct orderings.
3. Titles and the 30% fallback.
4. Groups, `AlternateContent`, SmartArt, tables — in that order, which is descending by how much
   text each loses.

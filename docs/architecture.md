# Architecture

docmd converts a Word document to Markdown by turning it into one XML document, running an
XSLT 3.0 transform over it, and serialising the result. This describes how the pieces fit and
why they are arranged this way — including a few decisions that look odd until you know what
they are avoiding.

## The pipeline

```
report.docx
    │
    │  1. open            OpcPackage            a .docx is a ZIP of XML parts
    ▼
  parts
    │  2. compose         WordCompositeBuilder  every needed part into ONE document
    ▼
 composite  ──────────────────────────────────────────────┐
    │  3. annotate        HeadingAnnotator                │  docmd:package
    ▼                                                     │    docmd:body        w:body
 composite + heading marks                                │    docmd:styles      w:styles
    │  4. transform       markdown.xslt                   │    docmd:numbering   w:numbering
    ▼                                                     │    docmd:relationships
  md-XML                                                  │    docmd:properties
    │  5a. assets         AssetRewriter → IAssetSink      │    docmd:style-map
    │  5b. serialise      MarkdownSerializer              │
    ▼                                                     └──────────────────────
report.md  (+ img/)
```

Each stage has one job, and the seams between them are where the interesting decisions live.

## 1–2. Why one composite document

A `.docx` is a ZIP of XML parts that reference each other by *relationship id*, not by path. A
paragraph's style is a `w:styleId` that means nothing without `styles.xml`; a list's shape lives
in `numbering.xml`; an image is an `r:embed` that resolves through `document.xml.rels`.

Rather than have the transform reach out for those, the builder assembles everything into one
document under `docmd:` wrapper elements. The transform is then **a pure function of a single
input**, which buys three things:

- **It can be dumped.** When output looks wrong, write the composite to a file and you have
  everything the transform saw, in the order it saw it.
- **It can be tested without a ZIP.** Most stylesheet tests build a composite string inline.
- **It has no I/O.** No relationship resolution, no part loading, no failure modes mid-transform.

There was also a forcing constraint. The design originally passed the style map as an
`xsl:param`, and the engine cannot carry a node in a parameter — see
[`engine-defects/2026-09-04-xslt-node-valued-parameters.md`](engine-defects/2026-09-04-xslt-node-valued-parameters.md).
Moving it into the composite turned out to be the better design anyway, which is the useful
kind of forced hand.

The cost is honest: everything is in memory at once. Measured, that costs roughly 5.8× more
memory for 32× more document — sublinear, because the GC compacts — so it is not the ceiling.
The ceiling is CPU time. See [Scaling](#scaling).

## 3. Annotation: deciding what a heading is

Nothing in OOXML says "this is a heading." Word offers at least four hints and documents in the
wild use all of them, so `HeadingAnnotator` tries them in order of reliability — explicit
`w:outlineLvl`, the style's own outline level, the style's inherited (`w:basedOn`) level, the
style *name* matching `heading N`, and finally direct formatting that looks like a heading
(short, bold, larger than body text).

This is the highest-leverage correctness work in the product. Heading structure decides chunk
boundaries in a retrieval index: a missed heading merges two chunks, a false positive shatters a
paragraph. The rule that fired is recorded as `docmd:heading-source` so the decision is
auditable rather than magic.

It runs in C# rather than XSLT because it is stateful — slugs are deduplicated in document
order, so the same heading text twice yields `scope` then `scope-1`, and anchors stay stable.

## 4. The transform, and the md-XML vocabulary

`markdown.xslt` is the semantic recovery layer: which paragraph is a heading, which run is
emphasis, how a Word list becomes a Markdown one. It is about 480 lines and it is meant to be
read — and replaced, see [extending.md](extending.md).

It does **not** emit Markdown. It emits a small XML vocabulary:

```xml
<md:document>
  <md:heading level="1" slug="scope"><md:text>Scope</md:text></md:heading>
  <md:para><md:text>See </md:text><md:strong><md:text>section 4</md:text></md:strong></md:para>
  <md:list ordered="true"><md:item><md:para>…</md:para></md:item></md:list>
</md:document>
```

The separation matters more than it looks. Markdown is whitespace-significant and
context-sensitive: whether `_` starts emphasis depends on what is adjacent to it, whether four
spaces start a code block depends on where the line begins, and whether `#` needs escaping
depends on column position. Expressing those rules in XSLT means fighting the one thing XSLT is
worst at — precise control of text output — in the same file where you are trying to express
document semantics.

So the stylesheet answers *what this is* and `MarkdownSerializer` answers *how it is written*.
All escaping and whitespace policy lives in exactly one place, in a language with a debugger.

That single-responsibility split is also what makes the output testable against an independent
implementation: `MarkdigOracleTests` serialises, reparses with **Markdig**, and compares. A
differential oracle only works if one component owns the whole question.

## 5. Assets, and the sink seam

`AssetRewriter` walks the md-XML for `md:image`, resolves each back through the composite's
relationships to bytes in the ZIP, hands them to an `IAssetSink`, and rewrites the element to
whatever URI the sink returns.

Assets are written *before* the Markdown is serialised, and the ordering is load-bearing: the
serialiser must see the rewritten tree, or the Markdown names raw OPC part paths that exist
nowhere on disk.

`IAssetSink` is the seam between *where the bytes land* and *what the Markdown says*. See
[extending.md](extending.md#custom-asset-sinks).

## Determinism

Same input plus same version plus same options produces byte-identical output. Concretely:

- **LF line endings always**, on every platform.
- **Culture-invariant** formatting throughout; the suite runs under `de-DE` and `tr-TR`, the two
  cultures that break naive .NET string handling (decimal comma, dotless `i`).
- **No timestamps** in the output.
- **UTF-8 without a BOM** — a BOM is invisible and breaks byte comparison.

This is a product guarantee, not an implementation detail: it is what makes a corpus conversion
reviewable in a diff, and it is why `Deterministic` and `DebugType=embedded` are set for every
configuration rather than just Release.

## ICU is mandatory

The engines depend on ICU for `normalize-unicode()`, collations, and regex character classes.
`DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` silently overrides `InvariantGlobalization=false` and
produces **wrong answers rather than errors**. CI refuses to build if it is set.

## Scaling

Transform cost dominates everything else — on a 1,000-paragraph document it is over 99% of the
conversion, with every other stage together under a second. Two consequences:

- Memory is not the constraint. It grows sublinearly with document size.
- Time is. It grows *super*linearly, so very large documents are the practical limit.

`TransformScalingTests` gates this: it asserts the cost of a 4× larger document stays under 10×,
which linear growth never approaches and quadratic growth never survives. It exists because a
single match pattern with two chained predicates once made the whole transform quadratic —
124 seconds on a document that now takes 19 — and no correctness test could see it, because the
output was byte-identical. See
[`engine-defects/2026-09-05-xslt-chained-predicates-in-match-patterns.md`](engine-defects/2026-09-05-xslt-chained-predicates-in-match-patterns.md).

## Project layout

| Project | Holds | Depends on |
|---|---|---|
| `Ooxml.Md.Core` | OPC reading, the md-XML vocabulary, serialiser, escaper, slugger, asset sinks, frontmatter, style maps | nothing docmd-specific |
| `Docmd.Word` | Word composition, heading annotation, the stylesheet, the conversion pipeline | Core |
| `Docmd.Cli` | argument parsing, exit codes, the `docmd` tool | Word |

`Ooxml.Md.Core` is format-agnostic on purpose: a PowerPoint front end would reuse all of it and
supply its own composer and stylesheet.

## Things that will surprise you

**`XDocument.Parse` drops whitespace-only text nodes by default.** A run containing only a space
is what Word puts between a bold phrase and the next word. Losing those welded `Hello world`
into `Helloworld`. Every parse of transform output passes `LoadOptions.PreserveWhitespace`, and
the parse lives in exactly one place (`MarkdownTransform`) so it cannot drift — the bug survived
review precisely because every test helper had its own copy of the defect.

**Two chained predicates in a match pattern are quadratic.** `w:p[A][B]` costs a document-wide
scan per candidate node; `w:p[A and B]` does not. Semantically identical here, 169× apart.

**`--` cannot appear inside an XML comment.** The stylesheet is an embedded resource parsed at
transform time, so a stray double hyphen in a comment fails on *every* document and looks like a
data bug. `StylesheetOverrideTests` parses the shipped stylesheet to catch it.

**OOXML toggles are not flags.** `<w:b w:val="0"/>` means *not* bold. Testing for the element's
existence marks every opted-out run inside a bold-styled block as bold. The same trap applies to
`w:numId="0"` (numbering cancelled, not list id zero) and `w:outlineLvl="9"` (body text).

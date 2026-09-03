# docmd — design

**Date:** 2026-09-03
**Status:** Agreed. Open commercial items in §16.
**Scope:** `docmd`, a commercial .NET CLI converting OOXML Word documents to Markdown, and the
shared core that a companion `pptmd` tool will reuse.
**Not in scope:** `pptmd`'s own conversion design; the standalone licensing project (tabled —
owned by a separate workstream, see §11).

---

## 1. Problem

Organisations hold hundreds or thousands of Word documents that they now want indexed by AI/RAG
systems, read as standalone Markdown, or both. The conversion options available to a .NET shop are
poor: the OpenXML SDK is raw material rather than a converter, `pandoc` is an external binary with
fixed opinions, and the Python ecosystem's tools (`unstructured`, `docling`) are not deployable
inside a .NET pipeline. Cloud document-intelligence APIs solve a different problem at a different
price, and hand the customer no artifact they own.

Endpoint Systems owns a 97.9%-conformant XSLT 4.0 engine. A `.docx` is a ZIP of XML parts. The
conversion is therefore a transform problem against an engine we already ship — which is both the
technical fit and the commercial reason this product is ours to build.

## 2. Position

`docmd` is a **commercial product in a private repository**. It is not part of the Apache-2.0
engine estate and must never be described as though it were.

- Free for individuals and developers, but **registration is required** — accepting terms is the
  gate.
- **Commercial use requires a commercial licence.** Note this is a *different line* from the
  database engine's model (`phoenixml/docs/superpowers/specs/2026-07-31-licensing-model-design.md`
  §4), which permits free commercial use below a revenue/team threshold. docmd's terms must not
  imply the engine's, and vice versa.
- A technical paygate exists at **cloud output** (§10) and coincides closely with the point at
  which use becomes organisational.

The product's value is not converting one document — that is a commodity. It is converting a
**corpus** identically, reproducibly, with provenance, on a schedule, and telling the customer what
their corpus actually contains before they spend money embedding it.

## 3. Settled decisions

| # | Decision | Rationale |
|---|---|---|
| 1 | **OOXML family only** — `.docx`, `.docm`, `.dotx`, `.dotm` | `.doc` is CFBF binary with no XML; supporting it shares zero code with the XSLT path and is the largest single effort cliff available. A `.doc` gets a clear "re-save as .docx" error and exit 2. |
| 2 | **Markdown + YAML frontmatter** as the primary output | Understood by both RAG indexers and static site generators; one artifact serves both audiences with no mode switch. |
| 3 | **A self-contained HTML companion** for comments and tracked changes | Redline view plus a summary header. Answers "how did this document get here, and what is still contested" — a question the Markdown deliberately does not answer. |
| 4 | **Register to run, then never block** | Registration is the consent moment. After that the free path never blocks for any licensing reason. |
| 5 | **Style map file with a stylesheet escape hatch** | Customers map house styles declaratively; XSLT remains available for full control. |
| 6 | **One private repo** holding `docmd` and later `pptmd` | They share a core, a licence scheme, and a cadence. Splitting them would mean publishing the shared core as a package to consume it next door — the pin-drift failure `phoenixml/CLAUDE.md` documents across this workspace. |
| 7 | **Corpus audit as a first-class command** | The data already flows through the pipeline; aggregating it is the highest value per line of code in the design. |
| 8 | **Cloud output is the paid tier; Pixault is the launch paid sink** | Pixault is the sink a customer cannot replace with `aws s3 sync`, so the paywall sits on genuine value. |

## 4. Architecture

### 4.1 Repository and projects

New private repo at `/repos/phoenixml/docmd`. House conventions apply: `net10.0`,
`LangVersion=preview`, nullable enabled, `TreatWarningsAsErrors=true` with `AnalysisLevel=latest-all`,
Central Package Management, xunit v3, `Directory.Build.rsp` carrying `-nodeReuse:false`.

| Project | Ships in | Role |
|---|---|---|
| `src/Ooxml.Md.Core` | both tools | OPC/ZIP opening, relationship resolution, composite-assembly framework, md-XML model and Markdown serialiser, frontmatter, image extraction, style-map machinery, asset sinks, licence gate |
| `src/Docmd.Word` | docmd | WordprocessingML assembly + `markdown.xslt` / `review.xslt` |
| `src/Docmd.Cli` | docmd | `PackAsTool`, `ToolCommandName=docmd`, argument parsing, console UX, licence gate invocation |
| `tests/Ooxml.Md.Core.Tests` | — | serialiser, sinks, licence gate |
| `tests/Docmd.Word.Tests` | — | golden-file conversion |
| *(later)* `src/Pptmd.Presentation`, `src/Pptmd.Cli` | pptmd | PresentationML assembly + stylesheets |

`Docmd.Word` and `Pptmd.Presentation` differ in exactly one pipeline stage (assembly) plus their
stylesheets. Everything else is shared. **Structure for `pptmd` now; build it later.** No pptmd
projects are created in this phase.

Engine dependency: `PhoenixmlDb.Xslt` **1.6.13** from nuget.org as a `PackageReference`. No
`ProjectReference` across repo boundaries, no symlinks — per `CLAUDE.md`.

### 4.2 The pipeline

```
report.docx
   │
   ├─(1) Open ──────── ZipArchive; content types; resolve parts via _rels
   │
   ├─(2) Assemble ──── ONE composite XML document
   │                   <docmd:package>: body + flattened styles + numbering
   │                   + comments (×3 parts) + foot/endnotes + relationship map
   │
   ├─(3) Annotate ──── style inheritance resolved, heading levels, heading slugs
   │                   (C#, computed once, shared by both emitters)
   │
   ├─(4) Transform ─── PhoenixmlDb.Xslt; same input, two stylesheets
   │                     markdown.xslt ─► md-XML tree
   │                     review.xslt   ─► HTML
   │
   └─(5) Emit ──────── 5a: assets through IAssetSink ─► partId → Uri map
                       5b: md-XML + URI map ─► Markdown text (C# serialiser)
                       report.md · report.review.html · img/report/*
```

### 4.3 Why a composite document (stage 2)

OOXML parts reference each other constantly — a paragraph points at a style id, a list at a
`numId`, a hyperlink at an `r:id` — and those targets live in sibling ZIP entries, not in
`document.xml`. The alternative is a custom URI resolver making `document('styles.xml')` work
inside the archive.

Composing instead makes the transform a **pure function of one input**. The composite can be dumped
to disk, diffed, and used directly as a test fixture; a conversion bug becomes "read the composite"
rather than "attach a debugger to a resolver."

### 4.4 Why annotation happens in C# (stage 3)

Resolving `w:basedOn` chains is a graph walk with cycle risk, and **both emitters need the identical
answer**. Computing effective `@docmd:outline-level` and `@docmd:slug` once during assembly means
the Markdown and the HTML companion cannot disagree about heading structure — which is exactly what
`report.review.html#q3-findings` cross-linking depends on. Two stylesheets computing slugs
independently would be a silent, permanent source of broken anchors.

Heading detection order, most to least reliable:

1. **`w:outlineLvl`** in `w:pPr` or inherited from the style. This is what Word's own navigation
   pane uses and it survives arbitrary style renaming.
2. **`w:basedOn` chain** resolving to a built-in heading style.
3. **Style name matching**, remembering that `w:styleId` is not `w:name` and that `w:name` is
   localised.
4. **Direct-format heuristic** — short, bold, larger than body text, not sentence-terminated,
   followed by body text.

Every heading records *which* rule fired, and that provenance is what the corpus audit (§9) reports.

### 4.5 Why Markdown is emitted via an intermediate tree (stage 5)

Markdown is whitespace-significant and escape-sensitive. List indentation must be exact, blank lines
separate blocks, and literal `*`, `_`, `|`, `#`, `[` must be escaped in body text but not inside
code spans. Doing that with `xsl:output method="text"` smears whitespace handling across every
template and is where converters classically produce subtly broken output.

Instead the stylesheet emits semantic md-XML — `<md:heading level="2">`, `<md:list ordered="true">`,
`<md:table>` — and a deterministic C# serialiser turns that into text. Every escaping and blank-line
rule lives in **one testable place**, and output flavour (GFM, CommonMark) becomes a serialiser
setting rather than a stylesheet fork.

## 5. Determinism

**Same input produces byte-identical output.** This is a testable property, not a marketing claim,
and it is what makes the tool safe in a pipeline: files land in git repos and RAG stores that
re-index on content change.

Consequences:

- **No conversion timestamp in frontmatter.** `--stamp` is available for callers who explicitly
  want one.
- **No audit or diagnostic data in the `.md`** — see §9.3. Anything in frontmatter is part of the
  file's hash, so diagnostics that improve between releases would force needless corpus re-embedding.
- Stable ordering everywhere; deterministic asset names.
- Culture-invariant formatting. Dates are ISO-8601; numbers are invariant. Tests run under
  `de-DE` and `tr-TR` as well as the default culture.
- `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` must never be set — the house ICU rule applies here too.

## 6. Frontmatter

From `docProps/core.xml` and `docProps/app.xml`: `title`, `author` (`dc:creator`), `created`,
`modified`, `revision`, `company`. Added by docmd: `source` (original filename) and `sha256` of the
source file, so a chunk retrieved months later traces to an exact document version.

`--front-matter none` suppresses the block entirely.

## 7. Tracked changes and comments

### 7.1 In the Markdown

`--revisions accept` is the **default**: the `.md` carries the final text. A RAG index must never
retrieve a deleted sentence as though it were current. `reject` yields the original text; `mark`
renders insertions and `~~deletions~~` inline for callers who want redlines in Markdown.

Correctness traps that must be handled, each with a fixture:

- Deleted text is `<w:delText>`, **not** `<w:t>`. A naive `//w:t` harvest silently accepts every
  change while appearing to work.
- A deleted paragraph mark — `<w:pPr><w:rPr><w:del/></w:rPr></w:pPr>` — **merges two paragraphs**.
- Formatting-only revisions are `<w:rPrChange>` / `<w:pPrChange>`.
- Moves are `<w:moveFrom>` / `<w:moveTo>`, distinct from insert/delete.
- Table row revisions live in `<w:trPr>`.

### 7.2 The HTML companion

Emitted with `--review` as `<name>.review.html`. **Self-contained**: inline CSS, images as `data:`
URIs, no sibling assets. It is an artifact people email, attach to tickets, and open from a network
share years later; one that breaks when moved is worse than none.

Content: a summary header (counts, jump links) over the full document rendered in the spirit of
Word's "All Markup" — insertions underlined, deletions struck through, each attributed and dated,
comments in a margin rail with threads and resolved state. A document with no revisions renders as
clean HTML.

Comments are **milestone-anchored**, not containing: `<w:commentRangeStart w:id="3"/>` and its
matching end appear as empty siblings among runs, so ranges can start mid-paragraph, end several
paragraphs later, and overlap each other. This is the overlapping-markup problem; `for-each-group
group-starting-with` plus keyed lookup is the mechanism.

Threading is **not** in `comments.xml`. Replies and resolved state live in `commentsExtended.xml`
(`w15`), linked by `paraId`; `commentsIds.xml` (`w16cid`) carries durable ids and
`commentsExtensible.xml` adds timestamps and mentions. Reconstructing a conversation is a join
across three parts, and **older documents have none of them** — the fallback path (flat, unthreaded
comments) must work and must be tested.

If a document has comments or revisions and `--review` was not passed, docmd says so on stderr.

## 8. Style mapping

A declarative map, passed as `--style-map <file>`, keyed by `w:styleId` or style name:

```yaml
CautionNote:   { as: blockquote, prefix: "**Caution:** " }
ProcedureStep: { as: ordered-list-item }
PartNumber:    { as: inline-code }
CorpTitle:     { as: heading, level: 1 }
```

The map is handed to the stylesheet as an `xsl:param` document and consulted by key lookup, so
supporting it costs very little. `as:` values correspond to md-XML element names.

`--stylesheet <file>` remains a documented escape hatch: user XSLT importing the default and
overriding selected templates.

**Unmapped styles are reported**, with frequency and example text, so a customer discovers what is
worth mapping instead of guessing.

## 9. Corpus audit

### 9.1 The run-level report

```
docmd audit <path> [-r] [--report audit.json] [--style-map <file>]
```

Runs stages 1–3 and stops before emission: fast, writes nothing by default. `--report` on the
convert path produces the same structure, so every batch conversion reports what it saw.

Collected per document and aggregated:

- **Heading provenance** — styled / `outlineLvl` / `basedOn`-derived / direct-format-guessed / **none**
- **Unmapped styles**, with frequency and example text
- **Table hazards** — merged cells, nested tables
- **Image formats** — including EMF/WMF counts and native chart parts
- **Comment and unresolved-revision counts**
- **Body text volume**

```
1,247 documents converted
   312  no heading structure at all      ← will chunk badly
   88   headings via direct formatting only
   41   tables with merged cells
   17   images are EMF/WMF (won't render)
   9    unmapped styles: RevBar, Applicability, ...
```

This tells a customer which documents will retrieve badly **before** they pay to embed them.
`--strict` turns any degradation into a non-zero exit for CI.

### 9.2 Per-document sidecar

`--audit-sidecar` additionally writes one `<stem>.audit.json` beside each converted document,
joining the existing naming family:

```
out/
├── report.md
├── report.review.html      # --review
├── report.audit.json       # --audit-sidecar
└── img/report/…
```

The sidecar record is **the same object** that appears in the run-level report's array — one schema,
one serialiser — so this is a small addition rather than a second feature. Both may be produced in
the same run.

The standalone `docmd audit` command writes sidecars **only when given `-o`**. Scattering
`.audit.json` files through a customer's source document tree uninvited is obnoxious, and the audit
command's default of writing nothing is deliberate.

Free tier. It is a local file, and it is the diagnostic that makes the product useful to someone who
has not yet decided to buy anything.

### 9.3 Audit data never enters the Markdown

Audit output is a sidecar and **never** appears in frontmatter or document body. This is a
determinism requirement, not a stylistic preference.

Frontmatter is inside the `.md` and therefore part of its content hash. If audit data lived there,
shipping an improved direct-formatting heuristic would change every document's audit block, change
every file's hash, and cause every pipeline watching for content change to **re-embed an entire
corpus whose text did not change** — a real and pointless bill at a few thousand documents.

A sidecar has the opposite property: `report.md` stays byte-identical across docmd versions unless
the *conversion* changed, while `report.audit.json` is free to improve every release. The two
artifacts version independently, which is what is wanted when one is a corpus and the other is
diagnostics about it.

Secondly, frontmatter is frequently indexed. "This document used 3 unmapped styles" would become
tokens competing at retrieval time with the text it describes.

The tempting middle path — a small "structure quality" summary field in frontmatter with detail in
the sidecar — is **rejected**: that field derives from the same heuristics, so it reintroduces the
whole re-embedding problem for one line of text.

## 10. Output destinations

### 10.1 The seam

```csharp
public interface IAssetSink
{
    Task<Uri> WriteAsync(string relativePath, Stream content, string contentType, CancellationToken ct);
}
```

Emit is two ordered phases — **5a** write assets, collecting `partId → Uri`; **5b** serialise
Markdown using those URIs. Where bytes land and what the Markdown says are separate decisions, and
the ordering is structural: rewriting URLs in finished Markdown with regexes fails the moment a
filename contains a bracket.

### 10.2 Images

Included by default. Extracted to `img/<document-stem>/`, per-document so batch runs never collide.
Alt text comes from `wp:docPr/@descr`, falling back to `@title`, then empty. `--no-images` omits
them; `--img-dir` renames the folder.

**EMF/WMF** files are passed through and **reported** — no Markdown renderer displays them, so a
silent pass-through produces a broken image on every viewer. Native **charts** are `chart1.xml`
DrawingML parts, not images, and often have no raster fallback; they are reported, not fabricated.

### 10.3 Free versus paid

| | Free (after registration) | Paid |
|---|---|---|
| Filesystem output | ✅ | |
| `--asset-base-url <url>` | ✅ | |
| `--sink pixault` | | ✅ |
| *(later)* S3 / Azure Blob sinks | | ✅ |

`--asset-base-url` writes assets locally and emits remote URLs, so the customer's existing
`aws s3 sync` / `azcopy` step moves the bytes. It stays **free deliberately**: it is string
concatenation a customer replaces with `sed` in a minute, and gating it would earn nothing while
inviting resentment. The paywall sits where docmd does real work — upload, credentials, retries,
manifests, idempotency, DAM metadata.

The `.md` files themselves are **not** sent to a DAM. They are a corpus's source of truth and want
versioning, diffing and incremental re-indexing, which git and object storage do well and a DAM does
not. The `.review.html` companion is a shareable document artifact and may sensibly go to one.

## 11. Licensing

The mechanism is **tabled** pending a standalone licensing project owned by a separate workstream.
docmd isolates it behind one seam so adoption later is a one-file change:

```csharp
public interface ILicenseGate { LicenseStatus Check(); }
public sealed record LicenseStatus(bool Allowed, string? Notice, LicenseClaims? Claims);
```

Called once in `Program.Main` before any conversion work. No converter code references it.

### 11.1 Compatibility contract

Whatever the shared project lands, these must hold:

1. **Stateless, offline verification.** Public key plus token, no database, no network. In
   particular **no dependency on `PhoenixmlDb.Storage`**, whose `LicenseReader` lives behind
   `LightningDB`, `PhoenixmlDb.Core` and `PhoenixmlDb.XQuery` — a Word converter must not ship an
   embedded database to check a 700-byte JWT.
2. **Audience-scoped tokens.** The token `LicenseService` emits today carries `sub`, `plan`, `iat`,
   `exp`, `features` and **no `aud` claim**, so any verifier holding the `2026-09` public key
   accepts any licence that key ever signed. docmd must reject a token that merely verifies but was
   not issued for docmd. **This negative case must be tested.**
3. **Register to run, then never block** on the free path.
4. **Resolution order:** `--license-key` → `DOCMD_LICENSE` → `~/.config/docmd/license`. The config
   file is deliberately reinstated relative to the engine's delivery spec: that spec removed files
   because an embedded library has no meaningful home directory, whereas for a CLI `~/.config/` is
   the correct and conventional location — `gh`, `aws` and `dotnet` all behave this way.

### 11.2 Failure posture

**The free path fails open; the paid path fails closed.** Core conversion never stops for any
licensing reason — expired, unverifiable, offline, clock skew all degrade to a stderr notice. A paid
sink requires a *positive* entitlement assertion, because "cannot verify, so proceed" is the gate
not existing. `LicenseStatus` therefore carries entitlements drawn from token claims.

## 12. CLI surface

```
docmd <input> [options]                # .docx/.docm/.dotx/.dotm, or a directory

  -o, --output <path>        file or directory (default: alongside the input)
  -r, --recursive            walk directories
      --review               also emit <name>.review.html
      --style-map <file>     YAML style map
      --stylesheet <file>    override stylesheet (escape hatch)
      --revisions <mode>     accept | reject | mark        (default: accept)
      --img-dir <name>       image folder name             (default: img)
      --no-images            omit images entirely
      --asset-base-url <url> emit remote URLs for local assets
      --sink <name>          pixault                       (paid)
      --sink-config <file>   sink credentials/settings
      --flavour <name>       gfm | commonmark              (default: gfm)
      --front-matter <mode>  yaml | none                   (default: yaml)
      --stamp                include a conversion timestamp (breaks determinism)
      --strict               degradations become a non-zero exit
      --report <file>        write the run-level audit report as JSON
      --audit-sidecar        also write <stem>.audit.json per document
  -q, --quiet   -v, --verbose

docmd audit <path> [-r] [--report <file>] [-o <dir> --audit-sidecar]
docmd register --email <addr> | --key <key>
docmd license                          # show current licence status
```

### Exit codes

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | Unexpected internal error |
| 2 | Bad input — not OOXML, corrupt package, path not found |
| 3 | Not registered |
| 4 | Completed with degradations, under `--strict` |
| 5 | Paid feature requested without entitlement |

## 13. Testing

### 13.1 Fixtures as unzipped XML directories

Fixtures are stored as **directories of OPC parts** (`[Content_Types].xml`, `word/document.xml`,
`word/styles.xml`, …) and zipped in memory at test time — not as committed `.docx` binaries. A
binary fixture is opaque: a reviewer cannot see what it tests and a change appears in git as "binary
file differs." Unzipped fixtures are readable, diffable, and hand-editable, so a regression test for
a bug is eight lines of XML rather than a session in Word.

Paired with a **small set of real `.docx` files produced by actual Word**, because Word's output is
messier than anything hand-written — `w:proofErr` noise, runs split mid-word by spell-check, `rsid`
attributes throughout, `w:bookmarkStart` scattered through the flow. Hand-written fixtures test
intent; real files test reality. Both are required.

### 13.2 Markdig as a differential oracle

The serialiser emits Markdown text from an md-XML tree. Parse that text **back** with Markdig
(BSD-2-Clause; test-only, not a runtime dependency) and compare the resulting document tree against
the md-XML it came from. This answers what a golden file cannot — *does the Markdown mean what was
intended* — and catches every escaping bug in one mechanism: an unescaped `|` inventing a table
cell, a `#` at line start inventing a heading, a `_` in a filename italicising a paragraph.

### 13.3 Other required tests

- **Determinism:** convert twice, assert byte-identical; repeat under `de-DE` and `tr-TR`.
- **Audit isolation:** converting with and without `--audit-sidecar` produces a byte-identical
  `.md`. This pins §9.3 directly — the guarantee is that diagnostics can never perturb the corpus,
  and it is only a guarantee if a test fails when someone adds a helpful field to the frontmatter.
- **Golden files** for whole documents, with the composite XML dumped on failure so a break
  localises to a stage.
- **Licence gate negatives:** a token that verifies under the engine key but carries the wrong
  audience is rejected; a missing key blocks; a valid-but-expired key never blocks the free path; a
  paid sink without entitlement exits 5.
- **Comment fallback:** documents lacking `commentsExtended.xml` still produce correct unthreaded
  comments.
- **No vacuous passes.** `phoenixml/CLAUDE.md` documents this failure twice — suites returning green
  having executed nothing. A missing fixture corpus fails, or skips loudly via `Assert.Skip`; it
  never returns early into a recorded pass.

## 14. Error handling

Fatal only at the package boundary — a corrupt ZIP, a non-OOXML file, a missing path. Everything
inside a valid document **degrades and is reported**: an unknown style becomes a paragraph, a
dangling `r:id` becomes plain text, an unrenderable image is passed through and counted. For a RAG
corpus the dangerous failure is never a crash; it is content quietly vanishing.

## 15. Dependencies

docmd is commercial, so **every dependency needs a commercial-clean licence**. This is not
discretionary.

| Package | Licence | Note |
|---|---|---|
| `PhoenixmlDb.Xslt` 1.6.13 | Apache-2.0 | First-party |
| `YamlDotNet` | MIT | Frontmatter and style map |
| `Markdig` | BSD-2-Clause | **Test-only** — differential oracle |
| `FluentAssertions` | **pin 6.12.2** | 7.x/8.x moved to the paid Xceed licence. Do not bump. |

## 16. Open items

- **The exact free/commercial line.** "Commercial use requires a commercial licence" is materially
  different from the engine's revenue/team threshold. Needs settling in terms before launch, and
  must not be baked into code.
- **Whether docmd and pptmd are one entitlement or two.** Affects the audience claim's shape.
- **Pixault's API surface** — not represented in this workspace. The sink cannot be built until it is
  known.
- **Sequencing:** no paid sink can ship before the entitlement mechanism exists. Free tier is
  therefore the first shippable milestone.
- **Table handling for RAG.** Emitting correct GFM is right, but a pipe table chunked at 512 tokens
  loses its header row. Whether docmd offers row-flattening or per-table chunk hints is deferred, and
  marketing should not imply tables are a solved problem.

## 17. Explicitly out of scope

- `.doc`, `.rtf`, `.odt` — see §3, decision 1.
- Rendering EMF/WMF or native charts to raster. Reported, never fabricated.
- Sending `.md` files to a DAM — see §10.3.
- `pptmd`'s conversion design. The core is structured for it; the tool is a separate effort.
- Any runtime telemetry, machine fingerprinting, or identity collection. The licensing model design's principle 5 ("No telemetry about human identity. Ever.")
  applies here without exception.

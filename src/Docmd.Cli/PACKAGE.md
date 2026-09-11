# docmd

Converts Microsoft Word documents to Markdown — for retrieval indexes, documentation pipelines,
or simply to read them somewhere other than Word.

```console
$ dotnet tool install -g Docmd.Cli
$ docmd report.docx -o out/
```

Accepts `.docx`, `.docm`, `.dotx` and `.dotm`. Word 97-2003 `.doc` is a different, binary format —
re-save it as `.docx` first.

## It tells you what it could not read

Most converters lose content silently: a callout in a text box, a customer name in a content
control, a table row behind a legacy wrapper. The output still looks like a document.

docmd measures every conversion. It compares the words a reader can see in the `.docx` against the
words a Markdown parser recovers from its own output, and reports what did not survive, naming the
structure responsible:

```
! 2 of 3754 words did not survive conversion (99.9 % kept).
!   missing 'Here' near "the Discount List Click Here Click 'Add' to use"
!   one of them sits inside <drawing>.
```

Silent when nothing is lost. On a 49-document sample of real business documents spanning 2008 to
2024, measured 2026-09-10: all 49 convert, **43 lose not one word**, and total loss is twelve
words — under 0.01%. Those figures are a dated measurement against a fixed corpus, not a promise
about your documents, which is why the tool measures yours too.

## The conversion is a stylesheet you can edit

The entire semantic layer — which paragraph is a heading, which run is emphasis, how a Word list
becomes a Markdown one — is one XSLT 3.0 stylesheet, not compiled code.

```console
$ docmd --print-stylesheet > mine.xslt
$ docmd report.docx --stylesheet mine.xslt
```

`--print-stylesheet` emits the copy that actually ran, so you edit what executed. There is no
plugin API to learn: the extension point is the implementation.

For smaller adjustments, a **style map** tells docmd what your template's own styles mean:

```yaml
CautionNote:   { as: blockquote, prefix: "Caution: " }
ProcedureStep: { as: ordered-list-item }
PartNumber:    { as: inline-code }
```

```console
$ docmd report.docx --style-map house-styles.yaml
```

## Deterministic

The same input, version and options produce byte-identical Markdown: LF endings everywhere,
culture-invariant formatting, no timestamps, UTF-8 without a BOM. These files land in git repos
and RAG stores that re-index on content change; one unstable byte re-embeds a corpus whose text
never moved.

## Options

| Option | Description |
|--------|-------------|
| `-o, --output <path>` | Output directory (default: `.`) |
| `--style-map <file>` | Map house styles to Markdown constructs (YAML) |
| `--stylesheet <file>` | Run your own stylesheet instead of the built-in one |
| `--print-stylesheet` | Write the built-in stylesheet to stdout and exit |
| `--asset-base-url <url>` | Emit remote URLs for images while writing them locally |
| `--img-dir <name>` | Image folder name (default: `img`) |
| `--no-images` | Omit images entirely |
| `--flavour <name>` | `gfm` or `commonmark` (default: `gfm`) |
| `--front-matter <mode>` | `yaml` or `none` (default: `yaml`) |

## Links

- **Source and full documentation:** https://github.com/phoenixmldb/docmd
- **What it knowingly drops, and the measured figures:**
  https://github.com/phoenixmldb/docmd/blob/main/docs/limitations.md
- **Found a document it handles badly?**
  https://github.com/phoenixmldb/docmd/blob/main/CONTRIBUTING.md — you can report a loss without
  sending us the document, and that is the single most useful thing you can contribute.

Built on the [PhoenixmlDb.Xslt](https://github.com/phoenixmldb/phoenixmldb-xslt) engine.
Apache-2.0.

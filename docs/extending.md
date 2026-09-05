# Extending docmd

There are three extension points, in increasing order of effort:

| You want to | Use | Needs |
|---|---|---|
| Tell docmd what your house styles mean | `--style-map` | a YAML file |
| Change how anything is converted | `--stylesheet` | XSLT 3.0 |
| Send assets somewhere other than disk | `IAssetSink` | C# |

We are not going to anticipate every house style or every storage backend. What we can do is
meet the technical interpretation of what is in the document and hand you the transform.

## Style maps

A Word style carries meaning the file format does not. `CautionNote` is a warning; nothing in
OOXML says so. A style map is how you tell docmd what your template means, without writing XSLT.

```yaml
# house-styles.yaml
CautionNote:   { as: blockquote, prefix: "Caution: " }
ProcedureStep: { as: ordered-list-item }
PartNumber:    { as: inline-code }
CorpTitle:     { as: heading, level: 1 }
Heading1:      { as: para }
```

```console
$ docmd report.docx --style-map house-styles.yaml
```

Keys match a `w:styleId` **or** the style name Word shows in its UI, whichever you have; the id
wins if both match, because names are localised and two styles can share one.

| `as:` | Applies to | Notes |
|---|---|---|
| `heading` | paragraph | takes `level:` 1–6, default 1 |
| `blockquote`, `code-block`, `para` | paragraph | |
| `list-item`, `ordered-list-item` | paragraph | consecutive items form one list |
| `inline-code`, `strong`, `em` | character | replaces the run's inferred formatting |

A mapped style **beats** what docmd would have inferred, which is why `Heading1: { as: para }`
above demotes a heading on purpose.

`prefix:` is literal text, escaped like any other document text — Markdown in a prefix would
have to be injected raw, which is how one style map corrupts every document it touches. It
applies to `heading`, `blockquote`, `code-block` and `para`. On a list or character style it is
**refused at parse time** rather than accepted and silently dropped, as is `level:` on anything
but a heading. A map that parses is a map that does what it says.

Because a map fails silently by nature — a misspelled id simply never matches, and the output is
identical to passing no map — docmd reports both directions:

```
! style map: 'CorpTitel' matched no style in this document.
? style map: 'Style17 (Corporate Heading)' is used 24 time(s) and is not mapped.
```

The first line is your typo. The second names styles the document leans on, counting only the
uses docmd left alone — a style it already read as a heading is not a gap in your map.

## Custom stylesheets

When a map is not enough, replace the transform. Start from the one that actually runs:

```console
$ docmd --print-stylesheet > mine.xslt
$ # edit mine.xslt
$ docmd report.docx --stylesheet mine.xslt
```

`--print-stylesheet` emits the built-in stylesheet, so you are editing what really executed
rather than reconstructing it from the repository at whatever version you happen to have.

The stylesheet's own directory is its base URI, so relative `xsl:import` and `xsl:include`
resolve against where your files live, not against wherever you ran `docmd`.

### What your stylesheet receives

One composite document, described in [architecture.md](architecture.md#12-why-one-composite-document):

```xml
<docmd:package xmlns:docmd="https://phoenixml.dev/docmd" xmlns:w="…wordprocessingml/2006/main">
  <docmd:body><w:body>…</w:body></docmd:body>
  <docmd:styles><w:styles>…</w:styles></docmd:styles>
  <docmd:numbering><w:numbering>…</w:numbering></docmd:numbering>
  <docmd:relationships>
    <docmd:relationship id="rId7" type="…/image" target="word/media/image1.png" external="false"/>
  </docmd:relationships>
  <docmd:properties>…</docmd:properties>
  <docmd:style-map><docmd:style key="CautionNote" as="blockquote" prefix="Caution: "/></docmd:style-map>
</docmd:package>
```

Paragraphs arrive pre-annotated by stage 3: `@docmd:heading-source` (`None` when it is not a
heading), `@docmd:outline-level`, and `@docmd:slug`.

### What your stylesheet must produce

md-XML, not Markdown. The serialiser owns all whitespace and escaping — emit `<md:text>` and it
will be escaped correctly for its position, which is not something you want to reimplement.

`md:document`, `md:heading` (`@level`, `@slug`), `md:para`, `md:list` (`@ordered`), `md:item`,
`md:table`, `md:row`, `md:cell`, `md:code-block`, `md:blockquote`, `md:hr`, `md:text`,
`md:strong`, `md:em`, `md:code`, `md:link` (`@href`), `md:image` (`@src`, `@alt`), `md:br`.

### Two traps worth knowing

**Never chain predicates in a match pattern.** `w:p[A][B]` costs this engine a document-wide scan
per candidate node and makes the whole transform quadratic. `w:p[A and B]` computes the same
answer in constant time — 169× apart on a 500-paragraph document. Joining with `and` is only
safe when neither predicate is positional; with `position()` or a numeric predicate the two
forms genuinely differ.

**`--` cannot appear inside an XML comment.** Your stylesheet will fail to parse on every
document, with an error that looks like a data problem.

## Custom asset sinks

By default images are written to `img/` beside the Markdown. `IAssetSink` is the seam between
*where the bytes land* and *what the Markdown says*:

```csharp
namespace Ooxml.Md.Core.Assets;

public interface IAssetSink
{
    Task<Uri> WriteAsync(string relativePath, Stream content, string contentType, CancellationToken ct);
}
```

One method. It receives an asset and returns **the URI the Markdown should reference** — so the
sink decides both destination and link, and the document follows.

```csharp
public sealed class BlobAssetSink(BlobContainerClient container, Uri publicBase) : IAssetSink
{
    public async Task<Uri> WriteAsync(
        string relativePath, Stream content, string contentType, CancellationToken ct)
    {
        var blob = container.GetBlobClient(relativePath);
        await blob.UploadAsync(
            content, new BlobHttpHeaders { ContentType = contentType }, cancellationToken: ct);

        return new Uri($"{publicBase.OriginalString.TrimEnd('/')}/{relativePath}");
    }
}
```

```csharp
var result = await DocumentConverter.ConvertAsync(
    "report.docx",
    new ConversionOptions
    {
        OutputDirectory = outputDirectory,
        AssetSink = new BlobAssetSink(container, publicBase),
    },
    cancellationToken);
```

### The contract

- **Return the URI to reference, not the one you wrote to.** These differ whenever a CDN, a
  reverse proxy, or a later sync step sits in front of storage. Returning a relative `Uri` is
  fine and is what the default sink does.
- **`relativePath` uses forward slashes** (`img/report/image1.png`). Translate if your target
  needs otherwise; the filesystem sink translates to `Path.DirectorySeparatorChar`.
- **You own idempotency.** docmd may hand you the same content twice, from two documents or two
  runs. Deduplicating on a content hash is a reasonable thing for a sink to do.
- **Throwing aborts the conversion.** To skip an asset without failing the document, return a URI
  anyway — an empty relative one, or a placeholder.
- **Not called when `IncludeImages` is false**, because nothing is extracted at all.

### Two things to get right

**Building a URI from a base.** `new Uri(baseUri, relativePath)` treats the base as a *file*
unless it ends in `/`, so `https://cdn.example.com/docs` + `img/x.png` silently yields
`https://cdn.example.com/img/x.png` — the `docs` segment is gone. Concatenate the literal text
instead, as both sinks above do.

**Determinism.** docmd guarantees that the same input produces byte-identical output. A sink that
returns a timestamped or randomly-named URI breaks that guarantee for the document, not just for
the asset. If you need uniqueness, derive it from the content.

## What is deliberately not extensible

**Escaping and whitespace.** `MarkdownSerializer` owns them. That is what allows the output to be
verified against an independent Markdown parser, and the guarantee is worth more than the
flexibility.

**The heading detection order.** Cascading detection rules with user overrides would make the
`docmd:heading-source` audit trail meaningless. Use `--style-map` to override a specific style,
or `--stylesheet` if you need to replace the logic wholesale.

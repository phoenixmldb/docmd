# PhoenixmlDb.Xslt 1.6.13 — a node passed to `SetParameter` is not usable as a node

**Filed:** [phoenixmldb-xslt#12](https://github.com/phoenixmldb/phoenixmldb-xslt/issues/12) on 2026-09-10.
**Re-verified on 1.6.15** that day: still reproduces, behaviour unchanged from 1.6.13.
This file is the working detail; the issue is the report. Update both.

Found by `docmd` while designing `--style-map`, which wanted to hand the stylesheet a small
configuration document as an `xsl:param`. This is the ordinary XSLT way to pass structured
configuration into a transform, and it does not work on this engine.

Not a crash. A capability gap, with a good error message.

## Symptom

```
XQueryException: An axis step (Child::e) was used when the context item is not a node
                 (got item of type XDocument)
  ↳ in expression (PathExpression): $map/e
```

## What a node-valued parameter actually becomes

```csharp
transformer.SetParameter("map", XDocument.Parse("<m><e id='a'>ALPHA</e></m>"));
```

| expression | result |
|---|---|
| `count($map)` | `1` — it is a single item |
| `string($map)` | the serialised XML — so atomisation works |
| `$map instance of node()` | **false** |
| `$map instance of xs:string` | **false** |
| `$map/e` | **error** — "context item is not a node (got item of type XDocument)" |

The parameter holds the raw CLR object rather than an XDM node. It is in a third state that
XPath cannot reason about: not a node, not an atomic value, and `instance of` denies both.

Every input form behaves identically — a string of XML, `XDocument`, `XElement`,
`XmlDocument`, and `XmlDocument.DocumentElement` all fail the same way. The same lookup
against the *input* document works perfectly, so the fault is in parameter binding rather
than in path evaluation.

## Why it matters beyond us

Passing a document as a stylesheet parameter is a standard XSLT idiom — it is how configuration,
lookup tables and vocabularies reach a transform without being baked into it. `xsl:param` with
`as="node()"` or `as="document-node()"` is the declared shape for it. A stylesheet that takes a
lookup document cannot currently be driven by this engine from .NET.

Two things would close it: converting a supplied `XDocument`/`XElement`/`XmlDocument` (and a
string of XML, given an opt-in) into an XDM node at bind time, and honouring a declared
`as="node()"` on `xsl:param` so a mismatch is a clear type error rather than a surprising axis
failure deeper in the expression.

## Repro

Self-contained; no docmd code involved.

```csharp
var t = new XsltTransformer();
await t.LoadStylesheetAsync("""
    <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
      <xsl:output method="text"/>
      <xsl:param name="map"/>
      <xsl:template match="/r"><xsl:value-of select="count($map//e)"/></xsl:template>
    </xsl:stylesheet>
    """);
t.SetParameter("map", XDocument.Parse("<m><e id='a'>ALPHA</e></m>"));
await t.TransformAsync("<r/>");   // throws
```

## What docmd did instead

The style map rides inside the composite document as `<docmd:style-map>`, alongside the styles,
numbering and relationships that already travel that way, and the stylesheet reads it with an
ordinary XPath lookup.

This is not purely a workaround — it is arguably the better design, and it is what the composite
exists for: the transform stays a pure function of exactly one input, the map appears in a dumped
composite when something needs debugging, and there is no parameter-binding API in the path at
all. The project's design spec §8 originally specified an `xsl:param`; §8 now records why it does
not use one.

No revert is required when this is fixed.

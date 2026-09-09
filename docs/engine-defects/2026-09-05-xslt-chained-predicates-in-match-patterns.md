# Chained predicates in a match pattern make the transform quadratic

**Engine:** `PhoenixmlDb.Xslt` 1.6.13
**Found:** 2026-09-05, converting a 1,000-paragraph Word document with docmd
**Severity:** High. Turns a 19-second conversion into 124 seconds, and gets worse with size.

## Summary

A template match pattern carrying **two chained predicates** costs something proportional to
the whole document for every candidate node, making the transform O(n²). Writing the
identical logic as **one predicate joined with `and`** is O(1) per node.

Nothing about the axes, the functions, or the predicate order matters. Only the count.

## Minimal reproduction

Input: `n` sibling `<w:p>` elements, each holding one run of text. No drawings, no styles,
no numbering. `w:drawing` never appears in the document.

```xml
<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:xs="http://www.w3.org/2001/XMLSchema"
                xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"
                xmlns:d="urn:x">
  <xsl:function name="d:txt" as="xs:string">
    <xsl:param name="node" as="node()"/>
    <xsl:sequence select="string-join($node//w:t, '')"/>
  </xsl:function>
  <xsl:template match="/"><out><xsl:apply-templates select="//w:p"/></out></xsl:template>
  <xsl:template match="w:p"><p><xsl:value-of select="."/></p></xsl:template>

  <!-- Swap this line for each row of the table below. -->
  <xsl:template match="w:p[not(normalize-space(d:txt(.)))][not(.//w:drawing)]" priority="1"/>
</xsl:stylesheet>
```

## Measurements

Milliseconds per paragraph, minimum of two runs, same engine instance, same inputs:

| match pattern | n=125 | n=250 | n=500 | shape |
|---|---|---|---|---|
| `w:p[not(normalize-space(d:txt(.)))]` | 0.17 | 0.21 | 0.36 | linear |
| `w:p[not(.//w:drawing)]` | 0.13 | 0.13 | 0.24 | linear |
| `w:p[A][B]` (both of the above) | 13.9 | 27.0 | 54.1 | **quadratic** |
| `w:p[B][A]` (order reversed) | 18.9 | 38.1 | 75.5 | **quadratic** |
| `w:p[A][not(descendant::w:drawing)]` | 13.6 | 27.1 | 53.9 | **quadratic** |
| `w:p[A and B]` (one predicate) | 0.18 | 0.25 | 0.32 | linear |

Each predicate is cheap alone. Chaining two makes the per-node cost grow with the document:
it doubles as `n` doubles, which is the signature of a document-wide scan per candidate node.

At n=500 the two-predicate form is **169x** slower than the `and` form that computes the
same answer.

## What is probably happening

The per-node cost growing linearly with document size suggests that a second predicate
defeats whatever indexing normally restricts a pattern to its candidate nodes, and the
engine falls back to evaluating the pattern against a document-wide node set for each node
it tests. The first predicate alone stays indexed; adding the second does not.

Worth checking whether the same applies to `select` expressions, or only to patterns. We
only measured patterns.

## Ruling out the obvious

These were each measured and are **not** the cause:

- **Template count / pattern indexing generally.** Adding 40 templates matching element
  names that never occur costs nothing (0.24 ms/para against a 0.25 baseline), so patterns
  *are* normally indexed by name.
- **Calling a user function from a pattern.** Costs about 3x, stays flat.
- **The `ancestor::` axis**, `.//` on its own, `xsl:strip-space elements="*"`,
  `xsl:for-each-group`, and the number of templates. All linear.
- **The engine's core dispatch.** A two-template stylesheet processes the same documents at
  a flat 0.23 ms/paragraph at every size tested.

## Impact on docmd

`markdown.xslt` had exactly one two-predicate pattern, for dropping empty paragraphs. It
was costing 84% of every conversion:

| | before | after |
|---|---|---|
| A 1,000-paragraph design document | 124 s | **19 s** |
| 32-document corpus, end to end | over 600 s (timed out) | **182 s** |

Output is byte-identical across all 32 documents. The workaround is in place
(`markdown.xslt`, the `w:p` empty-paragraph template), so docmd is not blocked. It is
recorded here because the next stylesheet author will write two predicates without knowing
they must not.

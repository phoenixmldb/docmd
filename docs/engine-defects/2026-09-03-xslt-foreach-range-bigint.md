# PhoenixmlDb.Xslt 1.6.13 — `xsl:for-each` over a range crashes when an operand is a BigInteger

**Filed:** [phoenixmldb-xslt#11](https://github.com/phoenixmldb/phoenixmldb-xslt/issues/11) on 2026-09-10.
**Re-verified on 1.6.15** that day: still reproduces, behaviour unchanged from 1.6.13.
This file is the working detail; the issue is the report. Update both.

Found by `docmd` (Word→Markdown converter) while dogfooding the engine against WordprocessingML.


## Symptom

`InvalidCastException: Unable to cast object of type 'System.Numerics.BigInteger' to type
'System.IConvertible'.` — unhandled, kills the transform.

## Precise trigger

`xsl:for-each` whose `@select` is a **range expression** (`A to B`) where an operand is an
`xs:integer` cast **from a string or untypedAtomic** (attribute values are untypedAtomic).
Those casts yield `BigInteger`; casts from an integer literal do not.

| Expression under `xsl:for-each` | Result |
|---|---|
| `2 to 3` | OK |
| `2 to xs:integer(3)` | OK |
| `2 to xs:integer("3")` | **InvalidCastException** |
| `2 to xs:integer(@v)` | **InvalidCastException** |
| `2 to xs:integer((@gs, 1)[1])` with `@gs` present | **InvalidCastException** |
| `2 to xs:integer((@gs, 1)[1])` with `@gs` absent | OK (falls back to integer literal `1`) |

## `xsl:for-each` is the ONLY affected construct

The identical range is fine everywhere else, which is what localises this to `ForEachAsync`'s
range fast path rather than to the range operator or the cast:

| Same range, different construct | Result |
|---|---|
| `<xsl:value-of select="2 to xs:integer(@v)"/>` | OK → `2,3` |
| `count(2 to xs:integer(@v))` | OK → `2` |
| `for $i in 2 to xs:integer(@v) return $i` | OK → `2,3` |
| `<xsl:iterate select="2 to xs:integer(@v)">` | OK |

**`xsl:iterate` is therefore a working user-facing workaround.**

## Suspected cause

The range-expression fast path in `ForEachAsync` calls `Convert.ToInt64` on the operand
unguarded. `BigInteger` does not implement `IConvertible`, so the cast throws. Every other
consumer of the same value goes down a path that handles it.

## Minimal repro

Self-contained console app: `repro.csproj` + `Program.cs` in this directory. `dotnet run` prints
the isolation table above. No docmd code, no ZIP, no XML input beyond `<r v='3'/>`.

## Impact on docmd

Hit by `<xsl:for-each select="2 to xs:integer((w:tcPr/w:gridSpan/@w:val, 1)[1])">`, used to pad
horizontally-merged table cells so every GFM row keeps the same width. Worked around with a
recursive named template, documented in `markdown.xslt` so it can be reverted when a fix ships.

## Status

Reported to the `parsers` session 2026-09-03; cross-session delivery is pending the recipient's
approval, so treat it as **not yet acknowledged by the engine team**.

Not filed in `phoenixmldb-xslt/BUGS.md` on purpose: that repo had uncommitted changes in
`src/PhoenixmlDb.Xslt/Engine/XsltTransformer.cs` on branch `fix/xspec-corpus-defects-2` — live work
in the same area. Writing to another team's active branch unasked is not ours to do.

This file is the durable record, kept in docmd because docmd carries the workaround. Delete it,
and revert the workaround in `src/Docmd.Word/Stylesheets/markdown.xslt`, once a fixed engine
package ships.

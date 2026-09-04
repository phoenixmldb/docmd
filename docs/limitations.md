# Known limitations

Behaviour docmd currently gets wrong, deliberately recorded rather than quietly carried.
Each entry says what is lost, why it is not fixed yet, and what the corpus audit must count
so the decision to leave it can be revisited with numbers instead of guesses.

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
| `w:sdt` | a content control (inline or block) | the control's current text |
| `w:fldSimple` | a field with its cached result | the result, e.g. a page number or a cross-reference |
| `w:smartTag` | a recognised entity | the words, unchanged |

Measured against the stylesheet at the time of writing, this input:

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
noise (`PAGE`, `DATE`), `w:sdt` needs a decision about the block-level form as well as the
inline one, and `w:hyperlink` already sets a precedent for how a wrapper contributes its
children. That work belongs with the plan that also builds the corpus audit, so the choice
can be made against measured frequencies.

### What the audit must count

- paragraphs containing at least one `w:sdt`, `w:fldSimple` or `w:smartTag`, as a share of all paragraphs
- characters of `w:t` unreachable from the child axis, as a share of all `w:t` characters
- `w:fldSimple` occurrences by `w:instr` keyword, so the "which fields carry content" policy
  is chosen from data
- paragraphs emitting an empty `md:para` despite having non-empty `docmd:visible-text` — the
  stray-blank-line case above, which is a direct count of the inconsistency

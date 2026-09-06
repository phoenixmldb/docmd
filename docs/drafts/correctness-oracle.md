---
title: How do you know a converter is correct?
description: Building a differential oracle for document conversion, and the two bugs it found
status: DRAFT. Destined for phoenixml.dev/guides and a LinkedIn edit. Held in this private
  repo rather than staged in phoenixmldb/phoenixml-docs, which is a PUBLIC repository:
  committing it there would announce docmd on GitHub before the decision to go public.
---

# How do you know a converter is correct?

We built a tool that converts Word documents to Markdown. It had 232 tests, a clean build with
analyzers set to fail on any diagnostic, and it converted a 49-document corpus without crashing.

It was also silently losing 3,440 words out of 4,664 in one of those documents, and nothing in
the test suite could tell.

## The gap that tests do not cover

The suite tested what its fixtures contained. That is the normal thing to do and it is not
enough, because a fixture only contains what its author thought to put in it. Ours had headings,
lists, tables, tracked changes, images. It did not have a text box, so nothing noticed that text
inside a text box was dropped entirely.

Golden files do not close the gap either. A golden file tells you the output *changed*. It never
tells you the output is *wrong* — and for a converter, those are completely different questions.
Both of the bugs below predated every golden file we had, so a golden file would have recorded
the corrupted output as the expected answer and gone green on it forever.

What we wanted was a property that holds for **any** document, including ones we have never seen:

> Every word a reader can see in the `.docx` still appears, in order, in the Markdown.

That is checkable without knowing what the document says.

## Reading your own output with someone else's parser

The naive implementation is to search the Markdown for each source word. It does not work,
because Markdown escapes things. A literal `_` in the source becomes `\_` in the output, and
string matching reports a loss that is not there.

So the oracle parses the output back with **Markdig** — a Markdown implementation we did not
write — and compares the text Markdig recovers against the text in the Word file. That inverts
the failure mode in exactly the right way:

- a `_` we escaped correctly reads back as `_`, and matches
- a `*` we *failed* to escape reads back as emphasis markup, and its word goes missing

The bug reveals itself as a lost word rather than hiding as a passing string comparison. An
independent implementation is doing the judging, so our own escaping bugs cannot vote on whether
our escaping is correct.

## Two mistakes worth describing

The first version of the oracle reported false losses in both directions, and both were the same
mistake wearing different clothes.

On the Word side, I put a separator between text runs. Word splits text at rsid and
spell-check boundaries constantly, so a single word arrives as two runs — `conver` and `sion` —
and the oracle demanded two words from a document whose Markdown correctly said `conversion`.

On the Markdown side, I put a separator between adjacent literals. Markdig splits text at every
escape, so `file\_name.txt` arrives as three pieces, and the oracle invented two words that were
never there.

The rule that fixes both: **text inside a block concatenates with nothing; only blocks are
separated.** Same bug, opposite ends of the pipeline.

The second mistake was worse, because it produced numbers that looked like findings. Matching
each source word against the rest of the output greedily meant a common token — a comma, "the" —
could match an occurrence thousands of words later, dragging the cursor past everything in
between and reporting all of it as lost. One document was reported as losing **3,991 words**. The
true figure was **one**. Another reported a missing comma that was sitting in the output six
words further along, with identical text on either side of it.

Bounded lookahead fixed it: a source word must appear within the next hundred output words. That
is not an arbitrary tolerance, it models the real relation. The text Markdown legitimately adds —
list markers, image alt text, link text — is always local. Nothing legitimate inserts thousands
of words. And a window makes a desync self-correcting instead of terminal.

Had I trusted the first run, I would have filed ten product defects, most of which were my own
bugs in the checker.

## What it found

Two real bugs on its first corpus run.

**Headings welded words across tabs.** The function that extracts a heading's text selected only
`w:t` elements, so a tab contributed nothing and `Name<tab>Value` became `NameValue`. What makes
this one instructive is that the *same defect* had already been found and fixed twice — once for
inline runs, once for table cells — and the code comments describe it in detail. It survived in a
third place because nobody had a way to ask the question "is any text being lost anywhere?"

**Italic full stops destroyed sentences.** This one cost 3,440 words in a single document.

Word leaves a full stop italic whenever the sentence before it was italicised. That is an
artifact of how people select text, not something an author intends, and it is extremely common.
Emitting it faithfully produces `**newsletter***.*`, which renders as `newsletter*.*` — asterisks
visible, sentence corrupted.

## Getting the diagnosis wrong

I first described this as "adjacent emphasis spans produce ambiguous delimiter runs" and proposed
three fixes: underscore delimiters, an HTML comment separator, or emitting delimiters
conditionally. All three address adjacency.

Before implementing, I mapped the actual behaviour against Markdig:

```
**bold***italic*   →  <strong>bold</strong><em>italic</em>     correct
**bold***.*        →  <strong>bold</strong>*.*                 broken
x*.*y              →  x*.*y                                    broken
x**.**y            →  x**.**y                                  broken
```

Adjacency is fine. What breaks is emphasis whose content carries **no letter or digit** — and it
breaks intraword too, where there is no adjacency at all.

**All three of my proposed fixes would have left the intraword cases broken.** The matrix took
ten minutes and changed the answer from a context-sensitive delimiter scheme to a one-line
condition: do not emit emphasis delimiters around a span with no letters or digits. The text is
preserved exactly; the only thing dropped is markup that Markdown cannot express reliably, and an
italic full stop is not worth corrupting a sentence over.

That is the real argument for building the oracle first. It did not just find the bug — it made
the wrong fix visible before it shipped.

## The number we publish

On a 49-document corpus of real business documents spanning 2008 to 2024:

**38 convert without losing a single word. Eleven do not.**

We publish the eleven. The remaining losses are text inside text boxes, inside inline content
controls, and inside fields — each documented, each with a note on what an audit must count so
the decision to leave it can be revisited with numbers rather than opinions.

Thirty-eight out of forty-nine is a worse-sounding number than "all tests pass," and it is a far
more useful one. It is the difference between a claim and a measurement.

## What transfers

The specifics are about Word and Markdown. The shape is not.

- **Ask what property holds for any input**, not what your fixtures happen to contain. Fixtures
  test intent; properties test reality.
- **Have something you did not write do the judging.** If your own code decides whether your own
  output is right, a shared assumption can make both agree and both be wrong.
- **Distrust the first run of a new checker.** Its early findings say more about the checker than
  about the code. Ours reported 3,991 lost words in a document that had lost one.
- **A test that has never failed is not evidence.** Break the code on purpose and confirm it goes
  red for the reason you expect.
- **Publish the number that is true**, not the number that sounds finished.

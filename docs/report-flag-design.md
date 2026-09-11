# `--report`: a coverage diagnostic that is safe to share

**Status:** design, not implemented. `--report` is reserved in the CLI surface and listed in
the README as not yet implemented.

## The problem

docmd already measures every conversion and prints what it lost. That warning is the closest
thing the project has to a distributed test: it runs on every document every user converts,
including the ones we will never see.

It is not reportable, though. The stderr block mixes two very different kinds of information:

```
! 2 of 3754 words did not survive conversion (99.9 % kept).      counts        — safe
!   missing 'Here' near "the Discount List Click Here Click…"    their text    — NOT safe
!   one of them sits inside <txbxContent>.                       element name  — safe
```

The middle lines quote the user's document. Anyone who wants to help us has to hand-redact
them or stay quiet, and most people will reasonably stay quiet. Documents that fail to convert
well are disproportionately contracts, invoices and internal procedures — exactly the documents
nobody pastes into a public issue.

So the obstacle to getting real-world feedback is not that user data is sensitive. It is that
**we have welded the diagnostic to the content**, and only we can unweld it.

## What we actually need

To fix a coverage defect we need to know which OOXML construct swallowed the text. `txbxContent`
is the entire actionable fact. That the missing word was "Here" tells us nothing — those lines
exist so the *user* can find the spot in their own file, which is a different job.

The half we need is the half that is already safe.

## Principle: redaction by construction, not by filtering

The report builder must never be handed document text in the first place, rather than being
handed it and stripping it out. "We remove the sensitive parts" is a claim that decays with every
future edit; "the sensitive parts are never in scope" is a property of the code's shape.

Concretely: `--report` renders from `TextCoverage.SourceWords`, `LostWords.Count` and `Causes`.
It never receives `LostWord.Word` or `LostWord.Context`.

This is testable, which matters more than it being stated. See *Verification* below.

## What the report may contain

| Field | Why it is safe |
|---|---|
| docmd version, engine version, runtime, OS | ours |
| Word count, lost count, percentage | integers |
| Cause element local names + counts | closed public vocabulary — see below |
| A per-document index (`document 3 of 49`) | ordinal, carries nothing |

## What it must never contain

- **Document text**, including the missing words and their context.
- **Filenames and paths.** A corpus filename typically carries a client name, a project and a
  revision. This repository already forbids naming real documents (`NoRealDocumentNamesTests`);
  a diagnostic that prints them would route around that rule rather than honour it.
- **Document properties** — title, author, company — which are metadata, not text, and are
  exactly as identifying.

For a per-document identity that survives across runs without naming anything, a truncated hash
of the file's *content* works: stable, comparable between two runs of the same document, and
reversible only by someone who already has the file.

## The one genuine leak risk: element names

The current cause derivation filters ancestors to the WordprocessingML namespace:

```csharp
.Where(a => a.Name.Namespace == Word && !PlainFlow.Contains(a.Name))
.Select(a => a.Name.LocalName)
```

That filter is load-bearing for privacy, and it is worth saying so where someone might otherwise
"improve" it. `w:` local names come from a schema Microsoft publishes; there are a few hundred
and none of them is user-authored. Widen the filter to all namespaces and the property is lost
immediately, because a custom XML part can carry elements named by whoever authored the template
— `<AcmeCorpContractValue>` is a perfectly ordinary thing to find in a document, and a namespace
URI is no better, since it is usually a company domain.

This matters because the filter also costs us something. Text lost inside a **non-`w:`** wrapper
is counted but unattributed — and the exotic wrapper nobody has heard of is precisely the case
this whole mechanism exists to surface.

The resolution is an allowlist rather than a namespace equality test:

- Ancestors in a **known public vocabulary** (`w:`, `mc:`, `wp:`, `a:`, `r:`, `v:`, `w14:` …)
  report their local name as now.
- Ancestors in **any other namespace** report as `<foreign>` with a count, and nothing else.

That keeps every name we emit drawn from a published schema, while still telling us *that* an
unrecognised wrapper cost someone a word — which is enough to start a conversation with the
person who has the document.

## Shape

`--report` writes the digest to stdout and leaves the conversion otherwise unchanged. Converting
a folder aggregates, because one page covering 500 documents is far more useful than 500 blocks,
and an organisation can audit its whole estate and send a single safe artefact.

```
docmd coverage report
  docmd 0.1.0 · PhoenixmlDb.Xslt 1.6.15 · .NET 10.0.401 · linux-x64

  49 documents, 128,904 words
  43 intact · 6 with losses · 12 words lost (0.009%)

  causes, by documents affected
    drawing        4
    txbxContent    3
    foreign        1

  losses by document
    #07  3,754 words   2 lost   drawing, txbxContent
    #19  8,120 words   4 lost   drawing
    …
```

No filenames, no text, nothing a schema does not already define.

`--report` does not change the exit code. Exit code 4 stays reserved for degradation under
`--strict`, which is a separate decision about whether a lossy conversion should fail a pipeline.

## Verification

The privacy claim is machine-checkable, and should be checked rather than asserted:

> Convert a document whose text is known, with `--report`, and assert that **no word from the
> source appears in the report**, excluding the fixed vocabulary of element names and docmd's own
> output.

That test needs no synthetic corpus and no real one — it tests docmd's own output against docmd's
own input, and it fails the moment someone reintroduces context into the digest. A privacy
property that is only written down in a design document is a privacy property that regresses.

A second test should assert that a document inside a namespace docmd does not know reports
`foreign` rather than that namespace's local name.

## What this does not solve

Nothing here gets us a corpus. We will still never run CI against real documents, and the
43-of-49 figure will remain a dated measurement rather than a gate. What it changes is the cost
of someone *telling us they found a case we do not handle* — from "hand-redact a warning, or
share a contract" to "paste this block".

That is the only feedback channel available for documents we are not allowed to see.

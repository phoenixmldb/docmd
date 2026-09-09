# Contributing

Thanks for looking. This is a short guide to the things that will trip you up, which are mostly
not the things a general .NET guide would warn you about.

## Getting started

```console
$ dotnet build docmd.slnx
$ dotnet test  docmd.slnx
$ dotnet run --project src/Docmd.Cli -- sample.docx -o out/
```

.NET 10; `global.json` pins the SDK. Nothing else to install.

**Never set `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`.** It overrides
`InvariantGlobalization=false` and produces *wrong answers rather than errors* — Unicode
normalisation, collation and regex character classes all quietly change. CI refuses to build
when it is set, which is the only reason it is worth mentioning: the failure is otherwise
invisible.

## What will fail your build

- **`TreatWarningsAsErrors` with `AnalysisLevel=latest-all`.** Any analyzer diagnostic is an
  error. This is the most common surprise — correct code gets rejected for a CA rule.
- **Central Package Management.** Versions go in `Directory.Packages.props`. A `Version` on a
  `PackageReference` is an error.
- **Lock files.** Adding a package changes `packages.lock.json`; commit it. CI restores with
  `--locked-mode` and fails otherwise. (`RestorePackagesWithLockFile` lives in
  `Directory.Build.targets`, not `.props` — in `.props` it breaks restore with `NETSDK1013`,
  because it is imported before `TargetFramework` exists.)

## How we test

The house style is stricter than "write a test," in ways that have each been earned:

**Prove the test can fail.** A test that has never failed is not evidence. When you add a
regression test, run it against the unfixed code first and confirm it fails for the reason you
expect. This is not ceremony — the performance gate initially could not fail at all, because the
budget it relied on never fired, and only breaking the code on purpose revealed it.

**Prefer an oracle to a golden file.** A golden file says "the output changed"; an oracle says
"the output is wrong." Two exist:

- `MarkdigOracleTests` serialises, re-parses with an independent Markdown implementation, and
  compares — so escaping bugs are caught by one mechanism rather than one test per character.
- `TextCoverageReport` checks that every word a reader sees in the `.docx` still appears in the
  Markdown. This is the end-to-end correctness check, and it ships: docmd reports coverage on
  every conversion rather than only under test.

**Beware the vacuous pass.** Several tests in this repository would once have passed while
proving nothing. If your test could pass with the feature deleted, it is not testing the feature.

**Fixtures are unzipped directories of OPC parts**, zipped in memory at test time, so a
regression test is a few lines of readable XML instead of a binary nobody can review. Two are
deliberately not: `fixtures/real/sample.docx` is a genuine third-party-written document, and
`word-noise` carries Word's own mess — `w:proofErr`, rsids, bookmarks between runs, words split
mid-word at spell-check boundaries. Those two find bugs hand-written XML never does.

### Running the corpus audit

Against your own documents:

```console
$ DOCMD_CORPUS=/path/to/docx/folder dotnet test tests/Docmd.Word.Tests \
    --filter "FullyQualifiedName~Corpus_Audit"
```

It writes a report naming every document that lost text and the first words lost. It reports
rather than gates: on the sample it was built against, 43 of 49 documents lose nothing and the
residue is 12 words, with causes recorded in [docs/limitations.md](docs/limitations.md). Its one
hard assertion is that no document *throws*, because an exception ends a batch run.

## Working on the stylesheet

`src/Docmd.Word/Stylesheets/markdown.xslt` is the semantic recovery layer and where most of the
interesting work is. Four traps, all of which have cost real time:

**Never write `--` inside an XML comment.** XML forbids it. The stylesheet is an embedded
resource parsed at transform time, so a stray double hyphen fails on *every* document with an
error that reads like a data problem. `StylesheetOverrideTests` parses the shipped stylesheet to
catch it, but only once tests run.

**Never chain predicates in a match pattern.** `w:p[A][B]` costs this engine a document-wide scan
per candidate node and makes the whole transform quadratic; `w:p[A and B]` computes the same
answer in constant time. They were 169× apart on a 500-paragraph document. Joining with `and` is
only safe when neither predicate is positional — with `position()` or a numeric predicate the two
forms genuinely differ. See
[docs/engine-defects/2026-09-05-xslt-chained-predicates-in-match-patterns.md](docs/engine-defects/2026-09-05-xslt-chained-predicates-in-match-patterns.md).

**OOXML toggles are not flags.** `<w:b w:val="0"/>` means *not* bold — testing for the element's
existence marks every opted-out run inside a bold block as bold. Likewise `w:numId="0"` means
numbering was cancelled, not list id zero, and `w:outlineLvl="9"` means body text.

**Whitespace-only text nodes are load-bearing.** A run containing only a space is what Word puts
between a bold phrase and the next word. `XDocument.Parse` discards those by default, which welds
words together. Every parse of transform output uses `LoadOptions.PreserveWhitespace`, and there
is exactly one such parse (`MarkdownTransform`) so it cannot drift.

## Performance

`TransformScalingTests` asserts that a 4× larger document costs under 10× more — linear never
approaches that, quadratic never survives it. It exists because a one-character stylesheet change
once made conversion quadratic and **no correctness test could see it**, since the output was
byte-identical.

If you change the stylesheet or the pipeline, watch that gate. `DOCMD_PERF_BUDGET_MS` raises its
wall-clock budget on slow machines; the ratio assertion is what actually detects a regression.

## Found an engine bug?

docmd is built on our own XSLT engine, and building it has found several. If you hit one, add a
writeup to `docs/engine-defects/` with a **minimal reproduction** and, if you can, the things you
ruled out — the existing entries follow that shape, and the ruled-out list is what saves the next
person from repeating the search.

## Pull requests

- Keep the change focused; a bug fix and a refactor in one diff are hard to review and harder to
  revert.
- Explain *why* in the commit message. The code says what it does; the message is the only place
  the reasoning survives.
- Tests pass, build is clean with zero warnings, and CI is green.
- If you changed conversion output, say so and show a before/after. Byte-identical output across
  a document corpus is a property worth protecting; changing it is sometimes right, but never
  accidental.

## Releasing

The version lives once, in `Directory.Build.props`. `dotnet pack src/Docmd.Cli` produces the
tool package; nothing else in the repository is published.

```console
$ dotnet pack src/Docmd.Cli -c Release -o out
$ dotnet tool install --tool-path ./verify --add-source ./out Docmd.Cli --version <v>
$ ./verify/docmd --version && ./verify/docmd sample.docx -o /tmp/check
```

Install from the package before pushing it. Building a library proves less than it looks: the
stylesheet is an embedded resource, so a packaging mistake shows up when the tool runs and
nowhere earlier.

Then tag (`v<version>`), and update `CHANGELOG.md` — moving the entry out of `Unreleased` and
dating it — before the tag rather than after, so the tag points at the changelog it describes.

**Pushing to nuget.org publishes the tool to everyone.** It is public whatever the repository's
visibility, so it is the moment docmd becomes public, not a step after that decision. Treat it
accordingly.

While docmd is `0.x`, a minor bump may change conversion output. The md-XML vocabulary a custom
stylesheet writes against is not stable until `1.0`.

## Licence

Apache-2.0. Contributions are accepted under the same licence.

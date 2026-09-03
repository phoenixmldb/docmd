# docmd Core Conversion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the free tier of `docmd` end to end — a .NET CLI that converts OOXML Word documents into deterministic Markdown with YAML frontmatter and extracted images.

**Architecture:** A five-stage pipeline. A `.docx` is a ZIP of XML parts, so we open it with `System.IO.Compression`, compose the relevant parts into **one** XML document, annotate that document in C# with resolved style inheritance and heading slugs, transform it with `PhoenixmlDb.Xslt` into a semantic `md:` XML tree, and serialise that tree to Markdown text with a C# writer that owns every escaping and blank-line rule.

**Tech Stack:** .NET 10 (`net10.0`), C# `preview`, `PhoenixmlDb.Xslt` 1.6.13, `YamlDotNet`, xunit v3, FluentAssertions 6.12.2, Markdig (test-only).

**Spec:** `docs/superpowers/specs/2026-09-03-docmd-design.md` — read it alongside this plan. Every task argues from it.

## Global Constraints

Every task's requirements implicitly include this section.

- **Target framework is `net10.0` only.** No multi-targeting.
- **`TreatWarningsAsErrors=true` with `AnalysisLevel=latest-all`.** Any analyzer diagnostic fails the build. This is the most common source of surprise failures in this workspace — expect CA-rule violations in otherwise-correct code and fix them rather than suppressing broadly.
- **Central Package Management.** Never put `Version=` on a `PackageReference`; add a `PackageVersion` to `Directory.Packages.props`.
- **`FluentAssertions` is pinned at `6.12.2`.** 7.x and 8.x moved to the paid Xceed licence and docmd is a commercial product. Do not bump it.
- **Never set `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`.** ICU is required. `InvariantGlobalization` is `false` in `Directory.Build.props` and the env var silently overrides it.
- **Determinism is a hard requirement.** Same input produces byte-identical output. No timestamps, no `DateTime.Now`, no `Guid.NewGuid()`, no hash-order-dependent iteration, no culture-sensitive formatting. Use `CultureInfo.InvariantCulture` explicitly and ISO-8601 for dates.
- **`PhoenixmlDb.Xslt` is consumed as a NuGet package.** No `ProjectReference` across repo boundaries, no symlinks into sibling repos.
- **Namespaces used throughout:**
  - WordprocessingML: `http://schemas.openxmlformats.org/wordprocessingml/2006/main` (prefix `w`)
  - Relationships: `http://schemas.openxmlformats.org/package/2006/relationships` (prefix `pr`)
  - Document relationships: `http://schemas.openxmlformats.org/officeDocument/2006/relationships` (prefix `r`)
  - docmd composite: `https://phoenixml.dev/docmd` (prefix `docmd`)
  - md-XML: `https://phoenixml.dev/docmd/md` (prefix `md`)
- **Commit after every task**, using the message given in the task's final step.

---

## Engine defects: this product dogfoods the parsers

docmd is the first thing to drive `PhoenixmlDb.Xslt` through WordprocessingML, which is far
messier than the hand-authored stylesheets crucible feeds it: deeply nested grouping,
recursive templates, `xsl:function` with sequence parameters, and namespace-heavy input.
**Expect to find engine bugs.** They are a normal outcome here, not a sign the task is going
wrong.

### Triage: is it docmd or the engine?

The composite design (§4.3 of the spec) pays off here. Because the transform is a pure
function of **one** XML input, a failing stylesheet test is already close to a minimal engine
repro — there is no ZIP, no resolver, and no C# state involved in the failure.

1. Dump the composite and the stylesheet to files.
2. Reproduce outside docmd with the `xslt` CLI:
   `xslt -s composite.xml -x markdown.xslt`
3. If the CLI reproduces it, it is an engine defect. If it does not, it is docmd's wiring.
4. Minimise: strip the composite to the smallest input that still shows the behaviour, and
   the stylesheet to the smallest template set. Cite the spec rule being violated
   (XSLT 3.0/4.0, XPath 3.1/4.0) — "this is wrong" is much weaker than "§14.4 says the
   grouping key is evaluated once per item."

### Handoff

Report it in **`/repos/phoenixml/phoenixmldb-xslt/BUGS.md`**, the existing cross-repo defect
register — its own header states it spans repos deliberately, because the engines are split
but the defects are not. Add an entry under `## Open — engine` carrying: the minimal
stylesheet, the minimal input, expected output, actual output, and the spec citation. Then
tell the parser agent, referencing the entry.

**Do not fix engine bugs in this repo.** A workaround inside `markdown.xslt` is acceptable
as a temporary measure, but it must carry a comment naming the BUGS.md entry so it can be
removed when the fix ships. A silent workaround becomes permanent and hides the defect from
every other consumer of the engine.

### Quarantining a blocked test — without a vacuous pass

A test blocked on an engine defect is **skipped loudly and tracked**, never deleted, and
never weakened until it passes. Weakening the assertion is the failure mode this rule exists
to prevent: it converts a known defect into a silent wrong answer that ships.

Add to `tests/Docmd.Word.Tests/EngineDefect.cs`:
```csharp
namespace Docmd.Word.Tests;

using Xunit;

/// <summary>
/// Skips a test blocked on a known PhoenixmlDb.Xslt defect.
/// </summary>
/// <remarks>
/// Assert.Skip, never an early return: a returning test is recorded as a PASS, so a
/// quarantined case would silently report success having verified nothing. The workspace
/// has been bitten by exactly that twice — see phoenixml/CLAUDE.md on conformance suites.
/// </remarks>
internal static class EngineDefect
{
    internal static void Skip(string bugsEntry, string summary)
        => Assert.Skip($"Blocked on engine defect '{bugsEntry}' (phoenixmldb-xslt/BUGS.md): {summary}");
}
```

Use it as the first line of the affected test:
```csharp
[Fact]
public async Task NestedGroupingKeepsItsContext()
{
    EngineDefect.Skip("for-each-group context in recursive templates",
                      "current-group() empties on the second recursion");
    // ... the test body stays intact and unmodified, ready to run when the fix ships
}
```

A skipped test must have a matching BUGS.md entry. When the engine package is bumped, remove
the `EngineDefect.Skip` line first and see what passes — that is the cheapest possible
regression check on the fix.

---

## File Structure

```
docmd/
├── Directory.Build.props            # net10.0, analyzers, determinism, company metadata
├── Directory.Build.rsp              # -nodeReuse:false
├── Directory.Packages.props         # every package version, centrally
├── docmd.slnx
├── src/
│   ├── Ooxml.Md.Core/               # format-agnostic; pptmd will reuse all of it
│   │   ├── Opc/
│   │   │   ├── OpcPackage.cs        # open a ZIP, read parts, resolve relationships
│   │   │   ├── OpcRelationship.cs   # id/type/target record
│   │   │   └── OpcFormatException.cs
│   │   ├── Markdown/
│   │   │   ├── MdNames.cs           # md-XML element/attribute names in one place
│   │   │   ├── MarkdownEscaper.cs   # text escaping, the single source of truth
│   │   │   ├── MarkdownOptions.cs   # flavour
│   │   │   └── MarkdownSerializer.cs# md-XML tree -> Markdown text
│   │   ├── Assets/
│   │   │   ├── IAssetSink.cs        # the seam; cloud sinks implement it later
│   │   │   └── FileSystemAssetSink.cs
│   │   ├── Frontmatter/
│   │   │   └── FrontmatterWriter.cs
│   │   ├── Licensing/
│   │   │   ├── ILicenseGate.cs      # the seam; real gate arrives in Plan 4
│   │   │   └── PermissiveLicenseGate.cs
│   │   └── Slugger.cs               # stable heading slugs, shared by both emitters
│   ├── Docmd.Word/                  # WordprocessingML-specific
│   │   ├── Assembly/
│   │   │   ├── WordCompositeBuilder.cs  # parts -> one <docmd:package>
│   │   │   ├── StyleResolver.cs         # w:basedOn chains, effective outlineLvl
│   │   │   └── HeadingAnnotator.cs      # 4-rule detection + provenance + slugs
│   │   ├── Stylesheets/
│   │   │   └── markdown.xslt            # embedded resource
│   │   ├── DocumentConverter.cs         # orchestrates stages 1-5
│   │   └── ConversionOptions.cs
│   └── Docmd.Cli/
│       ├── Program.cs
│       ├── CommandLine.cs           # parsing + exit codes
│       └── Docmd.Cli.csproj         # PackAsTool, ToolCommandName=docmd
└── tests/
    ├── Ooxml.Md.Core.Tests/
    │   ├── FixtureZip.cs            # unzipped fixture dir -> in-memory .docx
    │   ├── OpcPackageTests.cs
    │   ├── MarkdownSerializerTests.cs
    │   ├── MarkdownEscaperTests.cs
    │   └── MarkdigOracleTests.cs
    └── Docmd.Word.Tests/
        ├── fixtures/                # unzipped OPC parts, one dir per scenario
        ├── StyleResolverTests.cs
        ├── HeadingAnnotatorTests.cs
        ├── ConversionGoldenTests.cs
        └── DeterminismTests.cs
```

**Why `Ooxml.Md.Core` is separate from `Docmd.Word` from day one:** `pptmd` differs from `docmd` in exactly one pipeline stage (assembly) plus its stylesheets. Everything above — ZIP handling, relationship resolution, the Markdown serialiser, escaping, frontmatter, asset sinks, the licence gate — is identical for both. Extracting this boundary later would mean pulling a shared core out of code that assumed `w:p` everywhere. The boundary falls where the pipeline already has a stage break, so it costs nothing now.

---

### Task 1: Repository scaffolding and a build harness that proves the premise

**Files:**
- Create: `Directory.Build.props`, `Directory.Build.rsp`, `Directory.Packages.props`, `docmd.slnx`
- Create: `src/Ooxml.Md.Core/Ooxml.Md.Core.csproj`, `src/Docmd.Word/Docmd.Word.csproj`, `src/Docmd.Cli/Docmd.Cli.csproj`
- Create: `tests/Ooxml.Md.Core.Tests/Ooxml.Md.Core.Tests.csproj`, `tests/Docmd.Word.Tests/Docmd.Word.Tests.csproj`
- Test: `tests/Docmd.Word.Tests/EnginePremiseTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: a buildable solution; the package pin `PhoenixmlDb.Xslt` 1.6.13.

**Context:** The entire product rests on the assumption that `PhoenixmlDb.Xslt` 1.6.13 can be restored from nuget.org and can run a stylesheet producing text output. If that assumption is wrong, everything downstream is wasted. So the first test is not a placeholder — it is a load-bearing check of the premise, and it stays in the suite permanently as a canary for a bad package bump.

- [ ] **Step 1: Create the build property files**

`Directory.Build.props`:
```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>preview</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <InvariantGlobalization>false</InvariantGlobalization>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <NoWarn>$(NoWarn);CS1591</NoWarn>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <AnalysisLevel>latest-all</AnalysisLevel>
  </PropertyGroup>
  <PropertyGroup>
    <Authors>Endpoint Systems</Authors>
    <Company>Endpoint Systems</Company>
    <Product>docmd</Product>
    <Copyright>Copyright © Endpoint Systems 2026. All rights reserved.</Copyright>
    <RepositoryType>git</RepositoryType>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
  </PropertyGroup>
  <!-- Deterministic in every configuration, not just Release: docmd's own output
       determinism is a product guarantee, and a non-deterministic build is a
       confusing thing to debug underneath it. -->
  <PropertyGroup>
    <Deterministic>true</Deterministic>
    <DebugType>embedded</DebugType>
    <DebugSymbols>true</DebugSymbols>
  </PropertyGroup>
</Project>
```

`Directory.Build.rsp`:
```
-nodeReuse:false
```

`Directory.Packages.props`:
```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="PhoenixmlDb.Xslt" Version="1.6.13" />
    <PackageVersion Include="YamlDotNet" Version="18.1.0" />
  </ItemGroup>
  <ItemGroup>
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="18.8.1" />
    <PackageVersion Include="xunit.v3" Version="3.2.2" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="3.1.5" />
    <PackageVersion Include="coverlet.collector" Version="10.0.1" />
    <!-- Intentionally pinned. 7.x/8.x moved to the Xceed licence (paid for commercial
         use); 6.12.2 is the last MIT release, and docmd is a commercial product.
         Do not bump without a licensing decision. -->
    <PackageVersion Include="FluentAssertions" Version="6.12.2" />
    <!-- Test-only: differential oracle for the Markdown serialiser (Task 7). -->
    <PackageVersion Include="Markdig" Version="1.3.2" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create the three source projects**

`src/Ooxml.Md.Core/Ooxml.Md.Core.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="YamlDotNet" />
  </ItemGroup>
</Project>
```

`src/Docmd.Word/Docmd.Word.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="../Ooxml.Md.Core/Ooxml.Md.Core.csproj" />
    <PackageReference Include="PhoenixmlDb.Xslt" />
  </ItemGroup>
  <ItemGroup>
    <EmbeddedResource Include="Stylesheets/*.xslt" />
  </ItemGroup>
</Project>
```

`src/Docmd.Cli/Docmd.Cli.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <AssemblyName>docmd</AssemblyName>
    <RootNamespace>Docmd.Cli</RootNamespace>
    <PackAsTool>true</PackAsTool>
    <ToolCommandName>docmd</ToolCommandName>
    <PackageId>Docmd.Cli</PackageId>
    <Description>Converts Microsoft Word documents to Markdown for AI/RAG indexing and human reading.</Description>
    <!-- CA1303: literal strings in console output are the product's UI, not
         localisable resources. -->
    <NoWarn>$(NoWarn);CA1303</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../Docmd.Word/Docmd.Word.csproj" />
  </ItemGroup>
</Project>
```

Create `src/Docmd.Cli/Program.cs` with a placeholder that Task 14 replaces:
```csharp
namespace Docmd.Cli;

internal static class Program
{
    internal static int Main(string[] args)
    {
        Console.Error.WriteLine("docmd: not yet implemented");
        return 1;
    }
}
```

- [ ] **Step 3: Create the two test projects**

Both `tests/Ooxml.Md.Core.Tests/Ooxml.Md.Core.Tests.csproj` and `tests/Docmd.Word.Tests/Docmd.Word.Tests.csproj` use this shape, changing only the `ProjectReference`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <!-- Analyzer rules tuned for shipping libraries fight ordinary test code
         (CA1707 underscores in test names, CA2007 ConfigureAwait). -->
    <AnalysisLevel>none</AnalysisLevel>
    <EnableNETAnalyzers>false</EnableNETAnalyzers>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="coverlet.collector" />
    <PackageReference Include="FluentAssertions" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/Ooxml.Md.Core/Ooxml.Md.Core.csproj" />
  </ItemGroup>
</Project>
```
For `Docmd.Word.Tests`, reference `../../src/Docmd.Word/Docmd.Word.csproj` instead, and add `<PackageReference Include="Markdig" />` to `Ooxml.Md.Core.Tests` only.

Create `docmd.slnx`:
```xml
<Solution>
  <Project Path="src/Ooxml.Md.Core/Ooxml.Md.Core.csproj" />
  <Project Path="src/Docmd.Word/Docmd.Word.csproj" />
  <Project Path="src/Docmd.Cli/Docmd.Cli.csproj" />
  <Project Path="tests/Ooxml.Md.Core.Tests/Ooxml.Md.Core.Tests.csproj" />
  <Project Path="tests/Docmd.Word.Tests/Docmd.Word.Tests.csproj" />
</Solution>
```

- [ ] **Step 4: Write the failing premise test**

`tests/Docmd.Word.Tests/EnginePremiseTests.cs`:
```csharp
namespace Docmd.Word.Tests;

using System.Threading.Tasks;
using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

/// <summary>
/// The whole product assumes PhoenixmlDb.Xslt can run a stylesheet that produces text.
/// This is a canary, not a formality: if a package bump breaks text output or parameter
/// passing, every golden-file test in the suite fails at once with a confusing diff, and
/// this test says why in one line.
/// </summary>
public sealed class EnginePremiseTests
{
    [Fact]
    public async Task Engine_TransformsToText_AndAcceptsAParameter()
    {
        const string stylesheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:param name="greeting" select="'unset'"/>
              <xsl:template match="/doc">
                <xsl:value-of select="concat($greeting, ':', @name)"/>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(stylesheet);
        transformer.SetParameter("greeting", "hello");

        var result = await transformer.TransformAsync("""<doc name="world"/>""");

        result.Should().Be("hello:world");
    }
}
```

- [ ] **Step 5: Run the test to verify it fails**

Run: `cd /repos/phoenixml/docmd && dotnet test docmd.slnx`
Expected: FAIL — the projects do not exist yet on the first run of this step, or the test fails to compile. Once Steps 1–3 are done it should build; if the assertion fails, the package pin or the API has changed and that must be resolved before continuing.

- [ ] **Step 6: Run the test to verify it passes**

Run: `cd /repos/phoenixml/docmd && dotnet test docmd.slnx`
Expected: PASS, 1 test.

If `LoadStylesheetAsync` or `TransformAsync` do not exist with these signatures, check the installed package surface with `dotnet list package` and inspect `XsltFacade` in the sibling `phoenixmldb-xslt` repo — but do **not** switch to a `ProjectReference` to fix it.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Scaffold the repo, and prove the engine premise with a canary test

The product assumes PhoenixmlDb.Xslt 1.6.13 restores from nuget.org and can
run a stylesheet producing text with a parameter. That assumption carries
everything downstream, so it gets a permanent test rather than a one-time
check: when a later package bump breaks it, one canary fails with a clear
message instead of every golden-file test failing with a confusing diff."
```

---

### Task 2: OPC package reader and the fixture harness

**Files:**
- Create: `src/Ooxml.Md.Core/Opc/OpcPackage.cs`, `src/Ooxml.Md.Core/Opc/OpcRelationship.cs`, `src/Ooxml.Md.Core/Opc/OpcFormatException.cs`
- Test: `tests/Ooxml.Md.Core.Tests/FixtureZip.cs`, `tests/Ooxml.Md.Core.Tests/OpcPackageTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces:
  - `OpcPackage.Open(Stream stream, bool leaveOpen = false) : OpcPackage`
  - `OpcPackage.OpenFile(string path) : OpcPackage`
  - `OpcPackage.ContainsPart(string partName) : bool`
  - `OpcPackage.ReadXmlPart(string partName) : XDocument` — throws `OpcFormatException` if absent
  - `OpcPackage.TryReadXmlPart(string partName) : XDocument?`
  - `OpcPackage.OpenPartStream(string partName) : Stream`
  - `OpcPackage.RelationshipsFor(string partName) : IReadOnlyList<OpcRelationship>`
  - `OpcPackage.ResolveTarget(string sourcePartName, string relationshipId) : string?`
  - `record OpcRelationship(string Id, string Type, string Target, bool IsExternal)`
  - `FixtureZip.FromDirectory(string fixtureDirectory) : MemoryStream` (test helper)

**Context for someone new to OOXML:** A `.docx` is a ZIP. Part names are ZIP entry paths without a leading slash — `word/document.xml`, `word/styles.xml`, `docProps/core.xml`. Parts do not link to each other by path; they link by **relationship id**. For a part `word/document.xml`, its relationships live in a *sibling* file at `word/_rels/document.xml.rels`; the package root's relationships live at `_rels/.rels`. A relationship's `Target` is usually relative to the *directory of the source part*, so `rId7 → media/image1.png` inside `word/document.xml` means the part `word/media/image1.png`. Resolving that correctly is this task's real work; getting it wrong produces missing images that look like a stylesheet bug three tasks later.

**Fixture strategy (spec §13.1):** fixtures are directories of real OPC parts, zipped in memory at test time — never committed `.docx` binaries. A binary fixture is opaque in review and shows up in git as "binary file differs"; an unzipped one is readable, diffable, and hand-editable, so adding a regression test is eight lines of XML rather than a session in Word.

- [ ] **Step 1: Write the fixture harness**

`tests/Ooxml.Md.Core.Tests/FixtureZip.cs`:
```csharp
namespace Ooxml.Md.Core.Tests;

using System.IO;
using System.IO.Compression;
using System.Linq;

/// <summary>
/// Turns a directory of OPC parts into an in-memory .docx. Fixtures are stored unzipped
/// so they are reviewable and diffable; see spec §13.1.
/// </summary>
public static class FixtureZip
{
    public static MemoryStream FromDirectory(string fixtureDirectory)
    {
        if (!Directory.Exists(fixtureDirectory))
        {
            // Loud, not vacuous. A missing fixture must never let a test pass having
            // examined nothing — see spec §13.3.
            throw new DirectoryNotFoundException($"Fixture directory not found: {fixtureDirectory}");
        }

        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            // Ordered so the fixture zip itself is deterministic.
            var files = Directory.GetFiles(fixtureDirectory, "*", SearchOption.AllDirectories)
                                 .OrderBy(f => f, StringComparer.Ordinal);
            foreach (var file in files)
            {
                var entryName = Path.GetRelativePath(fixtureDirectory, file).Replace('\\', '/');
                var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                using var source = File.OpenRead(file);
                source.CopyTo(entryStream);
            }
        }

        stream.Position = 0;
        return stream;
    }

    public static string FixtureRoot(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", name);
}
```

- [ ] **Step 2: Create the first fixture and write the failing tests**

Create `tests/Ooxml.Md.Core.Tests/fixtures/minimal/[Content_Types].xml`:
```xml
<?xml version="1.0" encoding="UTF-8"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
  <Default Extension="xml" ContentType="application/xml"/>
  <Default Extension="png" ContentType="image/png"/>
  <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
</Types>
```

Create `tests/Ooxml.Md.Core.Tests/fixtures/minimal/_rels/.rels`:
```xml
<?xml version="1.0" encoding="UTF-8"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
</Relationships>
```

Create `tests/Ooxml.Md.Core.Tests/fixtures/minimal/word/document.xml`:
```xml
<?xml version="1.0" encoding="UTF-8"?>
<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
  <w:body>
    <w:p><w:r><w:t>Hello</w:t></w:r></w:p>
  </w:body>
</w:document>
```

Create `tests/Ooxml.Md.Core.Tests/fixtures/minimal/word/_rels/document.xml.rels`:
```xml
<?xml version="1.0" encoding="UTF-8"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rId7" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="media/image1.png"/>
  <Relationship Id="rId8" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink" Target="https://example.com/spec" TargetMode="External"/>
</Relationships>
```

Add to `Ooxml.Md.Core.Tests.csproj` so fixtures reach the output directory:
```xml
<ItemGroup>
  <None Include="fixtures/**/*" CopyToOutputDirectory="PreserveNewest" LinkBase="fixtures" />
</ItemGroup>
```

`tests/Ooxml.Md.Core.Tests/OpcPackageTests.cs`:
```csharp
namespace Ooxml.Md.Core.Tests;

using FluentAssertions;
using Ooxml.Md.Core.Opc;
using Xunit;

public sealed class OpcPackageTests
{
    private static OpcPackage OpenMinimal() =>
        OpcPackage.Open(FixtureZip.FromDirectory(FixtureZip.FixtureRoot("minimal")));

    [Fact]
    public void Fixture_IsStaged()
    {
        // Guards the build wiring. Without this every test below could pass vacuously.
        Directory.Exists(FixtureZip.FixtureRoot("minimal")).Should().BeTrue();
    }

    [Fact]
    public void ReadXmlPart_ReturnsTheParsedPart()
    {
        using var package = OpenMinimal();

        var document = package.ReadXmlPart("word/document.xml");

        document.Root!.Name.LocalName.Should().Be("document");
    }

    [Fact]
    public void ContainsPart_IsFalseForAnAbsentPart()
    {
        using var package = OpenMinimal();

        package.ContainsPart("word/numbering.xml").Should().BeFalse();
    }

    [Fact]
    public void ReadXmlPart_ThrowsForAnAbsentPart()
    {
        using var package = OpenMinimal();

        var act = () => package.ReadXmlPart("word/numbering.xml");

        act.Should().Throw<OpcFormatException>().WithMessage("*word/numbering.xml*");
    }

    [Fact]
    public void ResolveTarget_ResolvesRelativeToTheSourcePartDirectory()
    {
        using var package = OpenMinimal();

        // rId7 targets "media/image1.png" from within word/document.xml, so the part is
        // word/media/image1.png -- NOT media/image1.png. Getting this wrong produces
        // missing images that look like a stylesheet bug much later.
        package.ResolveTarget("word/document.xml", "rId7").Should().Be("word/media/image1.png");
    }

    [Fact]
    public void ResolveTarget_ReturnsExternalTargetsUnchanged()
    {
        using var package = OpenMinimal();

        package.ResolveTarget("word/document.xml", "rId8").Should().Be("https://example.com/spec");
    }

    [Fact]
    public void RelationshipsFor_ReportsExternalTargets()
    {
        using var package = OpenMinimal();

        var relationships = package.RelationshipsFor("word/document.xml");

        relationships.Should().ContainSingle(r => r.Id == "rId8")
                     .Which.IsExternal.Should().BeTrue();
    }

    [Fact]
    public void RelationshipsFor_IsEmptyWhenThePartHasNoRelsFile()
    {
        using var package = OpenMinimal();

        // Absence of a .rels sibling is normal and must not throw.
        package.RelationshipsFor("word/styles.xml").Should().BeEmpty();
    }

    [Fact]
    public void Open_ThrowsForSomethingThatIsNotAZip()
    {
        using var notAZip = new MemoryStream("this is not a zip"u8.ToArray());

        var act = () => OpcPackage.Open(notAZip);

        act.Should().Throw<OpcFormatException>();
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/Ooxml.Md.Core.Tests/Ooxml.Md.Core.Tests.csproj`
Expected: FAIL — `OpcPackage`, `OpcFormatException`, and `OpcRelationship` do not exist.

- [ ] **Step 4: Implement the reader**

`src/Ooxml.Md.Core/Opc/OpcFormatException.cs`:
```csharp
namespace Ooxml.Md.Core.Opc;

/// <summary>
/// The package is not a readable OPC container, or a required part is missing.
/// Maps to CLI exit code 2 (bad input).
/// </summary>
public sealed class OpcFormatException : Exception
{
    public OpcFormatException(string message) : base(message) { }
    public OpcFormatException(string message, Exception innerException) : base(message, innerException) { }
    public OpcFormatException() { }
}
```

`src/Ooxml.Md.Core/Opc/OpcRelationship.cs`:
```csharp
namespace Ooxml.Md.Core.Opc;

/// <summary>A single entry from a <c>.rels</c> part.</summary>
/// <param name="Target">
/// Verbatim from the XML. Relative targets are resolved against the source part's
/// directory by <see cref="OpcPackage.ResolveTarget"/>; external ones are absolute URIs.
/// </param>
public sealed record OpcRelationship(string Id, string Type, string Target, bool IsExternal);
```

`src/Ooxml.Md.Core/Opc/OpcPackage.cs`:
```csharp
namespace Ooxml.Md.Core.Opc;

using System.Collections.Concurrent;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

/// <summary>
/// Read-only access to an OPC (Open Packaging Conventions) container — the ZIP that a
/// .docx or .pptx actually is.
/// </summary>
/// <remarks>
/// Parts reference each other by relationship id, never by path, and a relationship's
/// target is relative to the directory of the part that declares it. That resolution is
/// the reason this type exists rather than callers using ZipArchive directly.
/// </remarks>
public sealed class OpcPackage : IDisposable
{
    private static readonly XNamespace RelationshipsNamespace =
        "http://schemas.openxmlformats.org/package/2006/relationships";

    private readonly ZipArchive _archive;
    private readonly Stream? _ownedStream;
    private readonly ConcurrentDictionary<string, IReadOnlyList<OpcRelationship>> _relationshipCache = new(StringComparer.Ordinal);

    private OpcPackage(ZipArchive archive, Stream? ownedStream)
    {
        _archive = archive;
        _ownedStream = ownedStream;
    }

    public static OpcPackage Open(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen);
            return new OpcPackage(archive, leaveOpen ? null : stream);
        }
        catch (InvalidDataException ex)
        {
            throw new OpcFormatException(
                "The file is not a readable OPC package. Word 97-2003 (.doc) files are a " +
                "different, binary format; re-save as .docx.", ex);
        }
    }

    public static OpcPackage OpenFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path))
        {
            throw new OpcFormatException($"File not found: {path}");
        }

        return Open(File.OpenRead(path));
    }

    public bool ContainsPart(string partName) => _archive.GetEntry(Normalise(partName)) is not null;

    public Stream OpenPartStream(string partName)
    {
        var entry = _archive.GetEntry(Normalise(partName))
            ?? throw new OpcFormatException($"Package part not found: {partName}");
        return entry.Open();
    }

    public XDocument ReadXmlPart(string partName)
        => TryReadXmlPart(partName)
           ?? throw new OpcFormatException($"Package part not found: {partName}");

    public XDocument? TryReadXmlPart(string partName)
    {
        var entry = _archive.GetEntry(Normalise(partName));
        if (entry is null)
        {
            return null;
        }

        using var stream = entry.Open();
        // DtdProcessing.Prohibit: OOXML parts never legitimately carry a DTD, and
        // honouring one in an untrusted document is an entity-expansion vector.
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreWhitespace = false,
        });

        try
        {
            return XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException ex)
        {
            throw new OpcFormatException($"Package part is not well-formed XML: {partName}", ex);
        }
    }

    public IReadOnlyList<OpcRelationship> RelationshipsFor(string partName)
        => _relationshipCache.GetOrAdd(Normalise(partName), LoadRelationships);

    /// <summary>
    /// Resolves a relationship id declared by <paramref name="sourcePartName"/> to either a
    /// package part name or, for external relationships, the target URI verbatim.
    /// Returns null when the id is not declared.
    /// </summary>
    public string? ResolveTarget(string sourcePartName, string relationshipId)
    {
        var relationship = RelationshipsFor(sourcePartName)
            .FirstOrDefault(r => string.Equals(r.Id, relationshipId, StringComparison.Ordinal));

        if (relationship is null)
        {
            return null;
        }

        if (relationship.IsExternal)
        {
            return relationship.Target;
        }

        var target = relationship.Target;
        if (target.StartsWith('/'))
        {
            return target.TrimStart('/');
        }

        var directory = GetDirectory(Normalise(sourcePartName));
        var combined = directory.Length == 0 ? target : $"{directory}/{target}";
        return NormaliseDotSegments(combined);
    }

    private IReadOnlyList<OpcRelationship> LoadRelationships(string partName)
    {
        var relsPartName = $"{GetDirectory(partName)}/_rels/{GetFileName(partName)}.rels".TrimStart('/');
        var document = TryReadXmlPart(relsPartName);
        if (document?.Root is null)
        {
            // No .rels sibling is normal, not an error.
            return [];
        }

        return document.Root
            .Elements(RelationshipsNamespace + "Relationship")
            .Select(e => new OpcRelationship(
                Id: (string?)e.Attribute("Id") ?? "",
                Type: (string?)e.Attribute("Type") ?? "",
                Target: (string?)e.Attribute("Target") ?? "",
                IsExternal: string.Equals((string?)e.Attribute("TargetMode"), "External", StringComparison.Ordinal)))
            .ToArray();
    }

    private static string Normalise(string partName) => partName.Replace('\\', '/').TrimStart('/');

    private static string GetDirectory(string partName)
    {
        var index = partName.LastIndexOf('/');
        return index < 0 ? "" : partName[..index];
    }

    private static string GetFileName(string partName)
    {
        var index = partName.LastIndexOf('/');
        return index < 0 ? partName : partName[(index + 1)..];
    }

    private static string NormaliseDotSegments(string path)
    {
        var segments = new List<string>();
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == ".." && segments.Count > 0)
            {
                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        return string.Join('/', segments);
    }

    public void Dispose()
    {
        _archive.Dispose();
        _ownedStream?.Dispose();
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Ooxml.Md.Core.Tests/Ooxml.Md.Core.Tests.csproj`
Expected: PASS, 8 tests.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Read OPC packages, and resolve relationships the way OOXML means them

Parts in a .docx never reference each other by path -- they reference a
relationship id, and the target is relative to the directory of the part
that declared it. So rId7 -> media/image1.png inside word/document.xml is
the part word/media/image1.png. Getting that wrong yields missing images
that present as a stylesheet bug several stages downstream, which is why
it is the behaviour this reader exists to own and the case the tests lead
with.

Fixtures are unzipped directories of OPC parts, zipped in memory at test
time. A committed .docx is opaque in review and diffs as 'binary file
differs'; this way a regression test is eight lines of readable XML.

A missing fixture directory throws rather than yielding an empty package,
so a broken build wiring can never let these tests pass having examined
nothing."
```

---
### Task 3: Compose the Word parts into one XML document

**Files:**
- Create: `src/Docmd.Word/Assembly/WordCompositeBuilder.cs`, `src/Docmd.Word/WordNames.cs`
- Test: `tests/Docmd.Word.Tests/WordCompositeBuilderTests.cs`, `tests/Docmd.Word.Tests/fixtures/composite-basic/**`

**Interfaces:**
- Consumes: `OpcPackage` (Task 2).
- Produces:
  - `WordCompositeBuilder.Build(OpcPackage package) : XDocument`
  - `WordNames.W`, `WordNames.R`, `WordNames.Docmd`, `WordNames.Md` — `XNamespace` constants
  - `WordNames.MainDocumentRelationshipType` — the officeDocument relationship type URI

**Context:** Stage 2 of the pipeline. Rather than teaching the stylesheet to reach into the ZIP for `styles.xml` and `numbering.xml`, we build **one** XML document containing everything the transform needs. This makes the transform a pure function of a single input: the composite can be written to disk, diffed, and used directly as a fixture, so a conversion bug becomes "read the composite" rather than "debug a URI resolver." See spec §4.3.

**Do not hardcode `word/document.xml`.** The main part is whatever the package-level relationship of type `.../officeDocument` points at. Most files use `word/document.xml`, but the format does not require it, and files produced by non-Microsoft tools sometimes differ.

The composite shape:

```xml
<docmd:package xmlns:docmd="https://phoenixml.dev/docmd">
  <docmd:body>          <!-- the w:body element, copied -->
  <docmd:styles>        <!-- the w:styles element, or empty -->
  <docmd:numbering>     <!-- the w:numbering element, or empty -->
  <docmd:relationships> <!-- flattened, targets already resolved to part names -->
    <docmd:relationship id="rId7" type="…/image" target="word/media/image1.png" external="false"/>
  </docmd:relationships>
  <docmd:properties>
    <docmd:core> <!-- docProps/core.xml root, or absent --> </docmd:core>
    <docmd:app>  <!-- docProps/app.xml root, or absent  --> </docmd:app>
  </docmd:properties>
</docmd:package>
```

- [ ] **Step 1: Create the fixture**

Copy `tests/Ooxml.Md.Core.Tests/fixtures/minimal/` to `tests/Docmd.Word.Tests/fixtures/composite-basic/`, then add two parts.

`tests/Docmd.Word.Tests/fixtures/composite-basic/word/styles.xml`:
```xml
<?xml version="1.0" encoding="UTF-8"?>
<w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
  <w:docDefaults>
    <w:rPrDefault><w:rPr><w:sz w:val="22"/></w:rPr></w:rPrDefault>
  </w:docDefaults>
  <w:style w:type="paragraph" w:styleId="Heading1">
    <w:name w:val="heading 1"/>
    <w:pPr><w:outlineLvl w:val="0"/></w:pPr>
  </w:style>
</w:styles>
```

`tests/Docmd.Word.Tests/fixtures/composite-basic/docProps/core.xml`:
```xml
<?xml version="1.0" encoding="UTF-8"?>
<cp:coreProperties
    xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties"
    xmlns:dc="http://purl.org/dc/elements/1.1/"
    xmlns:dcterms="http://purl.org/dc/terms/">
  <dc:title>Q3 Safety Review</dc:title>
  <dc:creator>A. Whitfield</dc:creator>
  <dcterms:created xsi:type="dcterms:W3CDTF" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">2026-04-11T09:14:00Z</dcterms:created>
</cp:coreProperties>
```

Add the fixture copy item to `Docmd.Word.Tests.csproj` (same shape as Task 2 Step 2) and add a `FixtureZip` for this project — reference the one in `Ooxml.Md.Core.Tests` is not possible across test projects, so copy `FixtureZip.cs` into `tests/Docmd.Word.Tests/FixtureZip.cs` with namespace `Docmd.Word.Tests`. It is 30 lines of test scaffolding; sharing it via a third project is not worth a package boundary.

- [ ] **Step 2: Write the failing tests**

`tests/Docmd.Word.Tests/WordCompositeBuilderTests.cs`:
```csharp
namespace Docmd.Word.Tests;

using System.Xml.Linq;
using Docmd.Word;
using Docmd.Word.Assembly;
using FluentAssertions;
using Ooxml.Md.Core.Opc;
using Xunit;

public sealed class WordCompositeBuilderTests
{
    private static XDocument BuildComposite(string fixture)
    {
        using var package = OpcPackage.Open(FixtureZip.FromDirectory(FixtureZip.FixtureRoot(fixture)));
        return WordCompositeBuilder.Build(package);
    }

    [Fact]
    public void Build_PlacesTheBodyUnderTheCompositeRoot()
    {
        var composite = BuildComposite("composite-basic");

        composite.Root!.Name.Should().Be(WordNames.Docmd + "package");
        composite.Root.Element(WordNames.Docmd + "body")!
                 .Element(WordNames.W + "p").Should().NotBeNull();
    }

    [Fact]
    public void Build_IncludesStylesAndTolerAtesAbsentNumbering()
    {
        var composite = BuildComposite("composite-basic");

        composite.Root!.Element(WordNames.Docmd + "styles")!
                 .Element(WordNames.W + "style").Should().NotBeNull();
        // numbering.xml is absent from this fixture; the element must still exist and be
        // empty, so the stylesheet never has to test for its presence.
        composite.Root.Element(WordNames.Docmd + "numbering")!.Elements().Should().BeEmpty();
    }

    [Fact]
    public void Build_FlattensRelationshipsWithResolvedTargets()
    {
        var composite = BuildComposite("composite-basic");

        var image = composite.Root!.Element(WordNames.Docmd + "relationships")!
            .Elements(WordNames.Docmd + "relationship")
            .Single(e => (string?)e.Attribute("id") == "rId7");

        // Already resolved -- the stylesheet must never do path arithmetic.
        image.Attribute("target")!.Value.Should().Be("word/media/image1.png");
        image.Attribute("external")!.Value.Should().Be("false");
    }

    [Fact]
    public void Build_IncludesCorePropertiesWhenPresent()
    {
        var composite = BuildComposite("composite-basic");

        composite.Root!.Element(WordNames.Docmd + "properties")!
                 .Element(WordNames.Docmd + "core").Should().NotBeNull();
    }

    [Fact]
    public void Build_ThrowsWhenThereIsNoMainDocumentRelationship()
    {
        using var empty = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(empty, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntry("placeholder.txt");
        }

        empty.Position = 0;
        using var package = OpcPackage.Open(empty);

        var act = () => WordCompositeBuilder.Build(package);

        act.Should().Throw<OpcFormatException>().WithMessage("*main document*");
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/Docmd.Word.Tests/Docmd.Word.Tests.csproj`
Expected: FAIL — `WordCompositeBuilder` and `WordNames` do not exist.

- [ ] **Step 4: Implement `WordNames` and the builder**

`src/Docmd.Word/WordNames.cs`:
```csharp
namespace Docmd.Word;

using System.Xml.Linq;

/// <summary>XML namespaces used across assembly and the stylesheets, declared once.</summary>
public static class WordNames
{
    public static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    public static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public static readonly XNamespace Docmd = "https://phoenixml.dev/docmd";
    public static readonly XNamespace Md = "https://phoenixml.dev/docmd/md";

    public const string MainDocumentRelationshipType =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";
}
```

`src/Docmd.Word/Assembly/WordCompositeBuilder.cs`:
```csharp
namespace Docmd.Word.Assembly;

using System.Globalization;
using System.Xml.Linq;
using Ooxml.Md.Core.Opc;

/// <summary>
/// Pipeline stage 2: composes the WordprocessingML parts a transform needs into a single
/// XML document.
/// </summary>
/// <remarks>
/// The alternative — a custom URI resolver letting the stylesheet call
/// <c>document('styles.xml')</c> inside the archive — was rejected. Composing makes the
/// transform a pure function of one input, so the composite can be dumped, diffed and used
/// directly as a fixture. See spec §4.3.
/// </remarks>
public static class WordCompositeBuilder
{
    public static XDocument Build(OpcPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        var mainPartName = FindMainDocumentPart(package);
        var mainDocument = package.ReadXmlPart(mainPartName);
        var body = mainDocument.Root?.Element(WordNames.W + "body")
            ?? throw new OpcFormatException($"The main document part '{mainPartName}' has no w:body.");

        var mainDirectory = mainPartName.Contains('/', StringComparison.Ordinal)
            ? mainPartName[..mainPartName.LastIndexOf('/')]
            : "";

        var styles = package.TryReadXmlPart(Sibling(mainDirectory, "styles.xml"))?.Root;
        var numbering = package.TryReadXmlPart(Sibling(mainDirectory, "numbering.xml"))?.Root;
        var core = package.TryReadXmlPart("docProps/core.xml")?.Root;
        var app = package.TryReadXmlPart("docProps/app.xml")?.Root;

        var relationships = package.RelationshipsFor(mainPartName).Select(relationship =>
            new XElement(WordNames.Docmd + "relationship",
                new XAttribute("id", relationship.Id),
                new XAttribute("type", relationship.Type),
                new XAttribute("target", package.ResolveTarget(mainPartName, relationship.Id) ?? relationship.Target),
                new XAttribute("external", relationship.IsExternal ? "true" : "false")));

        var properties = new XElement(WordNames.Docmd + "properties");
        if (core is not null)
        {
            properties.Add(new XElement(WordNames.Docmd + "core", new XElement(core)));
        }

        if (app is not null)
        {
            properties.Add(new XElement(WordNames.Docmd + "app", new XElement(app)));
        }

        return new XDocument(
            new XElement(WordNames.Docmd + "package",
                new XAttribute(XNamespace.Xmlns + "docmd", WordNames.Docmd.NamespaceName),
                new XAttribute("main-part", mainPartName),
                new XElement(WordNames.Docmd + "body", new XElement(body)),
                // Always present even when the source part is absent, so the stylesheet
                // never has to test for existence before selecting into them.
                new XElement(WordNames.Docmd + "styles", styles is null ? null : new XElement(styles)),
                new XElement(WordNames.Docmd + "numbering", numbering is null ? null : new XElement(numbering)),
                new XElement(WordNames.Docmd + "relationships", relationships),
                properties));
    }

    private static string FindMainDocumentPart(OpcPackage package)
    {
        // The main part is whatever the package relationship points at. Hardcoding
        // word/document.xml works for Word's own output but not for every producer.
        var relationship = package.RelationshipsFor("_rels/.rels".Replace("_rels/", "", StringComparison.Ordinal))
            .FirstOrDefault(r => string.Equals(r.Type, WordNames.MainDocumentRelationshipType, StringComparison.Ordinal));

        var resolved = relationship is null
            ? null
            : package.ResolveTarget("", relationship.Id);

        return resolved
            ?? throw new OpcFormatException(
                "The package declares no main document relationship, so it is not a Word document.");
    }

    private static string Sibling(string directory, string fileName) =>
        directory.Length == 0 ? fileName : string.Create(CultureInfo.InvariantCulture, $"{directory}/{fileName}");
}
```

**Note on `FindMainDocumentPart`:** the package-level relationships live at `_rels/.rels`, which `OpcPackage.RelationshipsFor("")` produces because `GetDirectory("")` is `""` and `GetFileName("")` is `""`, yielding `_rels/.rels`. Call it as `package.RelationshipsFor("")` — simplify the expression above to exactly that when implementing.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Docmd.Word.Tests/Docmd.Word.Tests.csproj`
Expected: PASS, 5 tests.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Compose the Word parts into one document the transform can be pure over

A paragraph points at a style id, a list at a numId, a hyperlink at an
r:id -- and every one of those targets lives in a sibling ZIP entry. The
tempting fix is a URI resolver that makes document('styles.xml') work
inside the archive. Composing instead makes the transform a pure function
of a single input, so the composite can be written out, diffed, and used
as a fixture: a conversion bug becomes 'read the composite' rather than
'attach a debugger to a resolver'.

Relationship targets are resolved during assembly, so no stylesheet ever
does path arithmetic. styles and numbering elements are always present
even when their parts are absent, so no stylesheet tests for existence
before selecting.

The main part is found through the package relationship rather than
hardcoded to word/document.xml, which the format does not require and
non-Microsoft producers do not always honour."
```

---

### Task 4: Resolve style inheritance and annotate headings

**Files:**
- Create: `src/Docmd.Word/Assembly/StyleResolver.cs`, `src/Docmd.Word/Assembly/HeadingAnnotator.cs`, `src/Ooxml.Md.Core/Slugger.cs`
- Test: `tests/Docmd.Word.Tests/StyleResolverTests.cs`, `tests/Docmd.Word.Tests/HeadingAnnotatorTests.cs`, `tests/Ooxml.Md.Core.Tests/SluggerTests.cs`

**Interfaces:**
- Consumes: the composite from Task 3, `WordNames`.
- Produces:
  - `StyleResolver.FromComposite(XElement stylesElement) : StyleResolver`
  - `StyleResolver.EffectiveOutlineLevel(string styleId) : int?`
  - `StyleResolver.DefaultFontHalfPoints : int`
  - `HeadingAnnotator.Annotate(XDocument composite) : void` — mutates in place
  - `enum HeadingSource { None, OutlineLevel, BasedOn, StyleName, DirectFormat }`
  - `Slugger.Slug(string text) : string` (instance method; deduplicates across calls)

**Context:** Stage 3, and the highest-leverage correctness work in the product. Heading structure determines RAG chunk boundaries: a missed heading silently merges two chunks, a false positive shatters a paragraph. Spec §4.4 fixes the detection order, most reliable first:

1. **`w:outlineLvl`** on the paragraph, or inherited from its style. This is what Word's own navigation pane uses and it survives arbitrary renaming.
2. **`w:basedOn` chain** resolving to a style that has an outline level.
3. **Style name matching** — remembering `w:styleId` is *not* `w:name`, and `w:name` is localised (`heading 1`, `berschrift 1`).
4. **Direct-format heuristic** — the person who bolded a 16pt line instead of applying a style.

Annotation happens **once in C#** so the Markdown emitter and the review emitter cannot disagree about structure — which is what their cross-links depend on. Two stylesheets computing slugs independently would be a silent, permanent source of broken anchors.

**On direct-format heading depth:** these are emitted at **level 2**, always. Inferring depth from font size is guesswork that fails differently on every document. A flat level 2 still produces correct chunk *boundaries*, which is the actual goal, and the `DirectFormat` provenance lets the audit (Plan 3) report exactly how many headings were guessed.

- [ ] **Step 1: Write the failing slugger tests**

`tests/Ooxml.Md.Core.Tests/SluggerTests.cs`:
```csharp
namespace Ooxml.Md.Core.Tests;

using FluentAssertions;
using Ooxml.Md.Core;
using Xunit;

public sealed class SluggerTests
{
    [Theory]
    [InlineData("Q3 Findings", "q3-findings")]
    [InlineData("  Leading and trailing  ", "leading-and-trailing")]
    [InlineData("Punctuation: removed!", "punctuation-removed")]
    [InlineData("Multiple   spaces", "multiple-spaces")]
    [InlineData("Hyphen-already", "hyphen-already")]
    public void Slug_NormalisesText(string input, string expected)
        => new Slugger().Slug(input).Should().Be(expected);

    [Fact]
    public void Slug_DeduplicatesRepeats()
    {
        var slugger = new Slugger();

        slugger.Slug("Overview").Should().Be("overview");
        slugger.Slug("Overview").Should().Be("overview-1");
        slugger.Slug("Overview").Should().Be("overview-2");
    }

    [Fact]
    public void Slug_FallsBackWhenNothingSurvivesNormalisation()
    {
        // A heading of only punctuation must still get a stable, unique anchor rather
        // than an empty string, or two such headings would collide silently.
        var slugger = new Slugger();

        slugger.Slug("***").Should().Be("section");
        slugger.Slug("###").Should().Be("section-1");
    }

    [Fact]
    public void Slug_IsCultureInvariant()
    {
        // Turkish lowercases 'I' to dotless 'i'. Without an invariant culture the same
        // document would slug differently on a Turkish machine, breaking determinism.
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
            new Slugger().Slug("INDEX").Should().Be("index");
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }
}
```

- [ ] **Step 2: Run to verify failure, then implement `Slugger`**

Run: `dotnet test tests/Ooxml.Md.Core.Tests/Ooxml.Md.Core.Tests.csproj --filter "FullyQualifiedName~Slugger"`
Expected: FAIL — `Slugger` does not exist.

`src/Ooxml.Md.Core/Slugger.cs`:
```csharp
namespace Ooxml.Md.Core;

using System.Globalization;
using System.Text;

/// <summary>
/// Produces stable, unique anchors for headings. One instance per document.
/// </summary>
/// <remarks>
/// Deliberately stateful: uniqueness requires remembering what has already been issued,
/// and the Markdown and the HTML companion must receive the <em>same</em> slug for the
/// same heading, which is why this runs once during annotation rather than inside each
/// emitter. See spec §4.4.
/// </remarks>
public sealed class Slugger
{
    private const string Fallback = "section";
    private readonly Dictionary<string, int> _seen = new(StringComparer.Ordinal);

    public string Slug(string text)
    {
        var builder = new StringBuilder(text?.Length ?? 0);
        var lastWasHyphen = true; // suppresses a leading hyphen

        foreach (var ch in (text ?? "").ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                lastWasHyphen = false;
            }
            else if (!lastWasHyphen)
            {
                builder.Append('-');
                lastWasHyphen = true;
            }
        }

        var candidate = builder.ToString().Trim('-');
        if (candidate.Length == 0)
        {
            candidate = Fallback;
        }

        if (!_seen.TryGetValue(candidate, out var count))
        {
            _seen[candidate] = 0;
            return candidate;
        }

        count++;
        _seen[candidate] = count;
        return string.Create(CultureInfo.InvariantCulture, $"{candidate}-{count}");
    }
}
```

Run the filter again. Expected: PASS, 8 tests.

- [ ] **Step 3: Write the failing style resolver tests**

`tests/Docmd.Word.Tests/StyleResolverTests.cs`:
```csharp
namespace Docmd.Word.Tests;

using System.Xml.Linq;
using Docmd.Word;
using Docmd.Word.Assembly;
using FluentAssertions;
using Xunit;

public sealed class StyleResolverTests
{
    private static StyleResolver Resolve(string stylesXml) =>
        StyleResolver.FromComposite(XElement.Parse(stylesXml));

    private const string Ns = @"xmlns:w=""http://schemas.openxmlformats.org/wordprocessingml/2006/main""";

    [Fact]
    public void EffectiveOutlineLevel_ReadsAnExplicitLevel()
    {
        var resolver = Resolve($"""
            <w:styles {Ns}>
              <w:style w:styleId="Heading1"><w:pPr><w:outlineLvl w:val="0"/></w:pPr></w:style>
            </w:styles>
            """);

        resolver.EffectiveOutlineLevel("Heading1").Should().Be(0);
    }

    [Fact]
    public void EffectiveOutlineLevel_WalksTheBasedOnChain()
    {
        // A corporate style named nothing like a heading, based on one that is. Name
        // matching would never find this; the chain does.
        var resolver = Resolve($"""
            <w:styles {Ns}>
              <w:style w:styleId="Heading2"><w:pPr><w:outlineLvl w:val="1"/></w:pPr></w:style>
              <w:style w:styleId="ProcedureTitle"><w:basedOn w:val="Heading2"/></w:style>
            </w:styles>
            """);

        resolver.EffectiveOutlineLevel("ProcedureTitle").Should().Be(1);
    }

    [Fact]
    public void EffectiveOutlineLevel_SurvivesACyclicBasedOnChain()
    {
        // Malformed documents exist. A naive walk here is an infinite loop that hangs
        // the converter on a customer's corpus with no diagnostic.
        var resolver = Resolve($"""
            <w:styles {Ns}>
              <w:style w:styleId="A"><w:basedOn w:val="B"/></w:style>
              <w:style w:styleId="B"><w:basedOn w:val="A"/></w:style>
            </w:styles>
            """);

        resolver.EffectiveOutlineLevel("A").Should().BeNull();
    }

    [Fact]
    public void LookupOutlineLevel_ReportsWhetherTheLevelCameFromAnAncestor()
    {
        var resolver = Resolve($"""
            <w:styles {Ns}>
              <w:style w:styleId="Heading2"><w:pPr><w:outlineLvl w:val="1"/></w:pPr></w:style>
              <w:style w:styleId="ProcedureTitle"><w:basedOn w:val="Heading2"/></w:style>
            </w:styles>
            """);

        resolver.LookupOutlineLevel("Heading2")!.Value.FromAncestor.Should().BeFalse();
        resolver.LookupOutlineLevel("ProcedureTitle")!.Value.FromAncestor.Should().BeTrue();
    }

    [Fact]
    public void EffectiveOutlineLevel_IsNullForAnUnknownStyle()
        => Resolve($"<w:styles {Ns}/>").EffectiveOutlineLevel("Nope").Should().BeNull();

    [Fact]
    public void DefaultFontHalfPoints_ReadsDocDefaults()
    {
        var resolver = Resolve($"""
            <w:styles {Ns}>
              <w:docDefaults><w:rPrDefault><w:rPr><w:sz w:val="20"/></w:rPr></w:rPrDefault></w:docDefaults>
            </w:styles>
            """);

        resolver.DefaultFontHalfPoints.Should().Be(20);
    }

    [Fact]
    public void DefaultFontHalfPoints_FallsBackToWordsDefault()
        => Resolve($"<w:styles {Ns}/>").DefaultFontHalfPoints.Should().Be(22);

    [Fact]
    public void NameOf_ReturnsTheLocalisedStyleName()
    {
        var resolver = Resolve($"""
            <w:styles {Ns}>
              <w:style w:styleId="berschrift1"><w:name w:val="heading 1"/></w:style>
            </w:styles>
            """);

        // styleId and name differ, and the name is what carries meaning across locales.
        resolver.NameOf("berschrift1").Should().Be("heading 1");
    }
}
```

- [ ] **Step 4: Run to verify failure, then implement `StyleResolver`**

Run: `dotnet test tests/Docmd.Word.Tests/Docmd.Word.Tests.csproj --filter "FullyQualifiedName~StyleResolver"`
Expected: FAIL — `StyleResolver` does not exist.

`src/Docmd.Word/Assembly/StyleResolver.cs`:
```csharp
namespace Docmd.Word.Assembly;

using System.Globalization;
using System.Xml.Linq;

/// <summary>
/// Flattens WordprocessingML style inheritance so callers see effective values.
/// </summary>
/// <remarks>
/// Resolution happens once, here, because both emitters need the identical answer.
/// Computing it inside each stylesheet would let the Markdown and the review companion
/// disagree about heading structure, which is exactly what their cross-links depend on.
/// </remarks>
public sealed class StyleResolver
{
    /// <summary>Word's default body size when docDefaults says nothing: 11pt.</summary>
    private const int WordDefaultHalfPoints = 22;

    private readonly Dictionary<string, XElement> _styles;

    private StyleResolver(Dictionary<string, XElement> styles, int defaultFontHalfPoints)
    {
        _styles = styles;
        DefaultFontHalfPoints = defaultFontHalfPoints;
    }

    public int DefaultFontHalfPoints { get; }

    public static StyleResolver FromComposite(XElement stylesElement)
    {
        ArgumentNullException.ThrowIfNull(stylesElement);

        // The composite wraps w:styles in docmd:styles; accept either as the entry point.
        var root = stylesElement.Name == WordNames.W + "styles"
            ? stylesElement
            : stylesElement.Element(WordNames.W + "styles") ?? stylesElement;

        var styles = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var style in root.Elements(WordNames.W + "style"))
        {
            var id = (string?)style.Attribute(WordNames.W + "styleId");
            if (!string.IsNullOrEmpty(id))
            {
                styles[id] = style;
            }
        }

        var defaultSize = root.Element(WordNames.W + "docDefaults")
                              ?.Element(WordNames.W + "rPrDefault")
                              ?.Element(WordNames.W + "rPr")
                              ?.Element(WordNames.W + "sz");

        return new StyleResolver(styles, ParseInt(defaultSize) ?? WordDefaultHalfPoints);
    }

    public string? NameOf(string styleId)
        => _styles.TryGetValue(styleId, out var style)
            ? (string?)style.Element(WordNames.W + "name")?.Attribute(WordNames.W + "val")
            : null;

    /// <summary>
    /// An outline level found on a style, and whether it came from an ancestor rather than
    /// the style itself. The caller needs both: the audit reports "styled" and
    /// "basedOn-derived" headings separately.
    /// </summary>
    public readonly record struct OutlineLookup(int Level, bool FromAncestor);

    /// <summary>
    /// The outline level this style confers, following w:basedOn until one is found.
    /// Null means the style is not a heading. Zero-based, as in the XML: w:outlineLvl 0
    /// is Heading 1.
    /// </summary>
    public int? EffectiveOutlineLevel(string styleId) => LookupOutlineLevel(styleId)?.Level;

    /// <summary>
    /// As <see cref="EffectiveOutlineLevel"/>, but also reports whether the level came
    /// from the style itself or from something it is based on.
    /// </summary>
    public OutlineLookup? LookupOutlineLevel(string styleId)
    {
        // Cycle guard. Malformed documents with A basedOn B basedOn A exist, and a naive
        // walk hangs the converter on a customer's corpus with no diagnostic at all.
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = styleId;
        var hops = 0;

        while (current is not null && visited.Add(current))
        {
            if (!_styles.TryGetValue(current, out var style))
            {
                return null;
            }

            var level = ParseInt(style.Element(WordNames.W + "pPr")?.Element(WordNames.W + "outlineLvl"));
            if (level is not null)
            {
                return new OutlineLookup(level.Value, FromAncestor: hops > 0);
            }

            current = (string?)style.Element(WordNames.W + "basedOn")?.Attribute(WordNames.W + "val");
            hops++;
        }

        return null;
    }

    private static int? ParseInt(XElement? element)
    {
        var raw = (string?)element?.Attribute(WordNames.W + "val");
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}
```

Run the filter again. Expected: PASS, 8 tests.

- [ ] **Step 5: Write the failing heading annotator tests**

`tests/Docmd.Word.Tests/HeadingAnnotatorTests.cs`:
```csharp
namespace Docmd.Word.Tests;

using System.Xml.Linq;
using Docmd.Word;
using Docmd.Word.Assembly;
using FluentAssertions;
using Xunit;

public sealed class HeadingAnnotatorTests
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static XDocument Annotate(string bodyInner, string stylesInner = "")
    {
        var composite = XDocument.Parse($"""
            <docmd:package xmlns:docmd="https://phoenixml.dev/docmd" xmlns:w="{W}">
              <docmd:body><w:body>{bodyInner}</w:body></docmd:body>
              <docmd:styles><w:styles>{stylesInner}</w:styles></docmd:styles>
              <docmd:numbering/>
              <docmd:relationships/>
              <docmd:properties/>
            </docmd:package>
            """);

        HeadingAnnotator.Annotate(composite);
        return composite;
    }

    private static XElement FirstParagraph(XDocument composite)
        => composite.Descendants(WordNames.W + "p").First();

    [Fact]
    public void Annotate_UsesAnExplicitParagraphOutlineLevel()
    {
        var composite = Annotate("""
            <w:p><w:pPr><w:outlineLvl w:val="1"/></w:pPr><w:r><w:t>Findings</w:t></w:r></w:p>
            """);

        var paragraph = FirstParagraph(composite);
        paragraph.Attribute(WordNames.Docmd + "outline-level")!.Value.Should().Be("1");
        paragraph.Attribute(WordNames.Docmd + "heading-source")!.Value.Should().Be("OutlineLevel");
        paragraph.Attribute(WordNames.Docmd + "slug")!.Value.Should().Be("findings");
    }

    [Fact]
    public void Annotate_ResolvesAHeadingThroughTheBasedOnChain()
    {
        var composite = Annotate(
            """<w:p><w:pPr><w:pStyle w:val="ProcedureTitle"/></w:pPr><w:r><w:t>Bleed</w:t></w:r></w:p>""",
            """
            <w:style w:styleId="Heading2"><w:pPr><w:outlineLvl w:val="1"/></w:pPr></w:style>
            <w:style w:styleId="ProcedureTitle"><w:basedOn w:val="Heading2"/></w:style>
            """);

        var paragraph = FirstParagraph(composite);
        paragraph.Attribute(WordNames.Docmd + "outline-level")!.Value.Should().Be("1");
        paragraph.Attribute(WordNames.Docmd + "heading-source")!.Value.Should().Be("BasedOn");
    }

    [Fact]
    public void Annotate_FallsBackToTheLocalisedStyleName()
    {
        var composite = Annotate(
            """<w:p><w:pPr><w:pStyle w:val="berschrift1"/></w:pPr><w:r><w:t>Einleitung</w:t></w:r></w:p>""",
            """<w:style w:styleId="berschrift1"><w:name w:val="heading 1"/></w:style>""");

        var paragraph = FirstParagraph(composite);
        paragraph.Attribute(WordNames.Docmd + "outline-level")!.Value.Should().Be("0");
        paragraph.Attribute(WordNames.Docmd + "heading-source")!.Value.Should().Be("StyleName");
    }

    [Fact]
    public void Annotate_DetectsAHeadingMadeWithDirectFormattingOnly()
    {
        // The single most common real-world case: someone bolded a large line instead of
        // applying a style. Missing it silently merges two RAG chunks.
        var composite = Annotate("""
            <w:p>
              <w:r><w:rPr><w:b/><w:sz w:val="32"/></w:rPr><w:t>Maintenance Procedure</w:t></w:r>
            </w:p>
            <w:p><w:r><w:t>Disconnect power before servicing the unit.</w:t></w:r></w:p>
            """);

        var paragraph = FirstParagraph(composite);
        paragraph.Attribute(WordNames.Docmd + "heading-source")!.Value.Should().Be("DirectFormat");
        // Outline levels are zero-based throughout, so 1 here is Markdown "##".
        // Always this level: inferring depth from font size fails differently on every
        // document, and a flat level still yields correct chunk boundaries.
        paragraph.Attribute(WordNames.Docmd + "outline-level")!.Value.Should().Be("1");
    }

    [Fact]
    public void Annotate_DoesNotTreatALongBoldSentenceAsAHeading()
    {
        var composite = Annotate("""
            <w:p>
              <w:r><w:rPr><w:b/><w:sz w:val="32"/></w:rPr>
                <w:t>This is a long emphatic sentence that runs on well past what any reasonable heading would, and it ends with a full stop.</w:t>
              </w:r>
            </w:p>
            """);

        FirstParagraph(composite).Attribute(WordNames.Docmd + "heading-source")!.Value.Should().Be("None");
    }

    [Fact]
    public void Annotate_MarksOrdinaryParagraphsAsNotHeadings()
    {
        var composite = Annotate("""<w:p><w:r><w:t>Ordinary body text.</w:t></w:r></w:p>""");

        var paragraph = FirstParagraph(composite);
        paragraph.Attribute(WordNames.Docmd + "heading-source")!.Value.Should().Be("None");
        paragraph.Attribute(WordNames.Docmd + "outline-level").Should().BeNull();
        paragraph.Attribute(WordNames.Docmd + "slug").Should().BeNull();
    }

    [Fact]
    public void Annotate_GivesRepeatedHeadingsDistinctSlugs()
    {
        var composite = Annotate("""
            <w:p><w:pPr><w:outlineLvl w:val="0"/></w:pPr><w:r><w:t>Overview</w:t></w:r></w:p>
            <w:p><w:pPr><w:outlineLvl w:val="0"/></w:pPr><w:r><w:t>Overview</w:t></w:r></w:p>
            """);

        var slugs = composite.Descendants(WordNames.W + "p")
            .Select(p => p.Attribute(WordNames.Docmd + "slug")!.Value).ToArray();

        slugs.Should().Equal("overview", "overview-1");
    }
}
```

- [ ] **Step 6: Run to verify failure, then implement `HeadingAnnotator`**

Run: `dotnet test tests/Docmd.Word.Tests/Docmd.Word.Tests.csproj --filter "FullyQualifiedName~HeadingAnnotator"`
Expected: FAIL — `HeadingAnnotator` does not exist.

`src/Docmd.Word/Assembly/HeadingAnnotator.cs`:
```csharp
namespace Docmd.Word.Assembly;

using System.Globalization;
using System.Xml.Linq;
using Ooxml.Md.Core;

/// <summary>Where a paragraph's heading level came from. Reported by the corpus audit.</summary>
public enum HeadingSource
{
    None,
    OutlineLevel,
    BasedOn,
    StyleName,
    DirectFormat,
}

/// <summary>
/// Pipeline stage 3: stamps each body paragraph with its effective heading level, the rule
/// that determined it, and a stable slug.
/// </summary>
/// <remarks>
/// Heading structure decides RAG chunk boundaries, so this is the highest-leverage
/// correctness work in the product: a missed heading merges two chunks, a false positive
/// shatters a paragraph. Detection order is spec §4.4, most reliable rule first.
/// </remarks>
public static class HeadingAnnotator
{
    /// <summary>Longest text still plausible as a heading.</summary>
    private const int MaxDirectFormatHeadingLength = 120;

    /// <summary>How much larger than body text a direct-formatted line must be, in half-points.</summary>
    private const int DirectFormatSizeMargin = 4;

    /// <summary>
    /// Level assigned to every direct-format heading. Zero-based, so this is Markdown "##".
    /// Inferring depth from font size is guesswork that fails differently on each document;
    /// a flat level still produces correct chunk boundaries, which is the actual goal.
    /// </summary>
    private const int DirectFormatLevel = 1;

    public static void Annotate(XDocument composite)
    {
        ArgumentNullException.ThrowIfNull(composite);

        var stylesElement = composite.Root?.Element(WordNames.Docmd + "styles");
        var resolver = StyleResolver.FromComposite(stylesElement ?? new XElement(WordNames.W + "styles"));
        var slugger = new Slugger();

        var body = composite.Root?.Element(WordNames.Docmd + "body");
        if (body is null)
        {
            return;
        }

        // Document order matters: slug deduplication and therefore anchor stability
        // depend on it.
        foreach (var paragraph in body.Descendants(WordNames.W + "p"))
        {
            var (level, source) = Detect(paragraph, resolver);

            paragraph.SetAttributeValue(WordNames.Docmd + "heading-source", source.ToString());

            if (source == HeadingSource.None)
            {
                continue;
            }

            paragraph.SetAttributeValue(
                WordNames.Docmd + "outline-level",
                level.ToString(CultureInfo.InvariantCulture));
            paragraph.SetAttributeValue(WordNames.Docmd + "slug", slugger.Slug(TextOf(paragraph)));
        }
    }

    private static (int Level, HeadingSource Source) Detect(XElement paragraph, StyleResolver resolver)
    {
        var properties = paragraph.Element(WordNames.W + "pPr");

        // Rule 1: an explicit outline level on the paragraph itself.
        var explicitLevel = ParseInt(properties?.Element(WordNames.W + "outlineLvl"));
        if (explicitLevel is not null)
        {
            return (explicitLevel.Value, HeadingSource.OutlineLevel);
        }

        var styleId = (string?)properties?.Element(WordNames.W + "pStyle")?.Attribute(WordNames.W + "val");
        if (!string.IsNullOrEmpty(styleId))
        {
            // Rule 2: the style, or something it is based on, confers a level. The lookup
            // reports which, because the audit counts them separately -- a heading found
            // only through an ancestor is a weaker signal than one the style declares.
            var lookup = resolver.LookupOutlineLevel(styleId);
            if (lookup is not null)
            {
                return (
                    lookup.Value.Level,
                    lookup.Value.FromAncestor ? HeadingSource.BasedOn : HeadingSource.OutlineLevel);
            }

            // Rule 3: the localised style name. w:styleId is not w:name, and w:name is
            // what survives translation.
            var nameLevel = LevelFromStyleName(resolver.NameOf(styleId) ?? styleId);
            if (nameLevel is not null)
            {
                return (nameLevel.Value, HeadingSource.StyleName);
            }
        }

        // Rule 4: direct formatting, with no style involved at all.
        return LooksLikeADirectFormatHeading(paragraph, resolver)
            ? (DirectFormatLevel, HeadingSource.DirectFormat)
            : (0, HeadingSource.None);
    }

    private static int? LevelFromStyleName(string name)
    {
        // Matches "heading 1", "Heading1", "Heading 3". Localised names are handled by
        // rules 1 and 2; this is the English fallback of last resort.
        var trimmed = name.Replace(" ", "", StringComparison.Ordinal);
        if (!trimmed.StartsWith("heading", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var digits = trimmed["heading".Length..];
        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var oneBased)
               && oneBased is >= 1 and <= 9
            ? oneBased - 1 // w:outlineLvl is zero-based; Heading 1 is level 0
            : null;
    }

    private static bool LooksLikeADirectFormatHeading(XElement paragraph, StyleResolver resolver)
    {
        var runs = paragraph.Elements(WordNames.W + "r").ToArray();
        if (runs.Length == 0)
        {
            return false;
        }

        var text = TextOf(paragraph).Trim();
        if (text.Length == 0 || text.Length > MaxDirectFormatHeadingLength)
        {
            return false;
        }

        // A trailing full stop is the strongest single signal that this is a sentence.
        if (text.EndsWith('.'))
        {
            return false;
        }

        var allBold = runs.All(r => r.Element(WordNames.W + "rPr")?.Element(WordNames.W + "b") is not null);
        if (!allBold)
        {
            return false;
        }

        var largest = runs
            .Select(r => ParseInt(r.Element(WordNames.W + "rPr")?.Element(WordNames.W + "sz")))
            .Where(size => size is not null)
            .Select(size => size!.Value)
            .DefaultIfEmpty(resolver.DefaultFontHalfPoints)
            .Max();

        return largest >= resolver.DefaultFontHalfPoints + DirectFormatSizeMargin;
    }

    private static string TextOf(XElement paragraph)
        => string.Concat(paragraph.Descendants(WordNames.W + "t").Select(t => t.Value));

    private static int? ParseInt(XElement? element)
    {
        var raw = (string?)element?.Attribute(WordNames.W + "val");
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}
```

Run the filter again. Expected: PASS, 7 tests.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Resolve heading structure once, in C#, where both emitters can trust it

Heading structure decides RAG chunk boundaries, so this is the highest
leverage correctness work in the product: a missed heading silently merges
two chunks and a false positive shatters a paragraph.

Detection runs most-reliable-first. w:outlineLvl is what Word's own
navigation pane uses and survives arbitrary renaming, so it leads; then
the w:basedOn chain, which is the only thing that finds a corporate
'ProcedureTitle' based on Heading2; then the localised style name,
because w:styleId is not w:name; and last the direct-formatting
heuristic, for the very common case of someone bolding a 16pt line
instead of applying a style.

The basedOn walk carries a cycle guard. Documents with A basedOn B basedOn
A exist, and a naive walk is an infinite loop that hangs the converter
mid-corpus with no diagnostic.

Direct-format headings are always level 2. Inferring depth from font size
fails differently on every document, while a flat level still produces
correct chunk boundaries -- and the recorded provenance lets the audit
report exactly how many were guessed.

Slugs are issued here rather than in each stylesheet so the Markdown and
the review companion cannot disagree about anchors, which is what their
cross-links depend on."
```

---
### Task 5: The md-XML vocabulary and the block serialiser

**Files:**
- Create: `src/Ooxml.Md.Core/Markdown/MdNames.cs`, `src/Ooxml.Md.Core/Markdown/MarkdownOptions.cs`, `src/Ooxml.Md.Core/Markdown/MarkdownSerializer.cs`
- Test: `tests/Ooxml.Md.Core.Tests/MarkdownSerializerTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces:
  - `MdNames.Md : XNamespace`, and `XName` constants for every element
  - `enum MarkdownFlavour { Gfm, CommonMark }`
  - `record MarkdownOptions(MarkdownFlavour Flavour = MarkdownFlavour.Gfm)`
  - `MarkdownSerializer.Serialize(XDocument mdXml, MarkdownOptions? options = null) : string`

**Context:** Stage 5b, and spec §4.5's key decision. The stylesheet does **not** emit Markdown text — it emits a semantic tree, and this serialiser turns that tree into text. Markdown is whitespace-significant and escape-sensitive; doing it with `xsl:output method="text"` smears whitespace handling across every template and is where converters classically produce subtly broken output. Here, every blank-line and escaping rule lives in one testable place, and output flavour becomes a setting rather than a stylesheet fork.

**The vocabulary** (namespace `https://phoenixml.dev/docmd/md`, prefix `md`):

| Block | Attributes |
|---|---|
| `md:document` | — |
| `md:heading` | `level` (1–6), `slug` |
| `md:para` | — |
| `md:list` | `ordered` (`true`/`false`), `start` |
| `md:item` | — (contains blocks) |
| `md:table` | — |
| `md:row` | `header` (`true`/`false`) |
| `md:cell` | — |
| `md:code-block` | `language` |
| `md:blockquote` | — |
| `md:hr` | — |

| Inline | Attributes |
|---|---|
| `md:text` | — (the literal, unescaped) |
| `md:strong`, `md:em`, `md:code` | — |
| `md:link` | `href` |
| `md:image` | `src`, `alt` |
| `md:br` | — |

**Line endings are always `\n`**, never `\r\n`, on every platform. A byte-identical guarantee that changes with the host OS is not a guarantee.

- [ ] **Step 1: Write the failing tests**

`tests/Ooxml.Md.Core.Tests/MarkdownSerializerTests.cs`:
```csharp
namespace Ooxml.Md.Core.Tests;

using System.Xml.Linq;
using FluentAssertions;
using Ooxml.Md.Core.Markdown;
using Xunit;

public sealed class MarkdownSerializerTests
{
    private static string Serialize(string inner)
        => MarkdownSerializer.Serialize(XDocument.Parse(
            $"""<md:document xmlns:md="https://phoenixml.dev/docmd/md">{inner}</md:document>"""));

    [Fact]
    public void Heading_UsesHashesForItsLevel()
        => Serialize("""<md:heading level="2"><md:text>Findings</md:text></md:heading>""")
            .Should().Be("## Findings\n");

    [Fact]
    public void Paragraph_IsEmittedPlainly()
        => Serialize("""<md:para><md:text>Body text.</md:text></md:para>""")
            .Should().Be("Body text.\n");

    [Fact]
    public void Blocks_AreSeparatedByExactlyOneBlankLine()
        => Serialize("""
            <md:heading level="1"><md:text>Title</md:text></md:heading>
            <md:para><md:text>One.</md:text></md:para>
            <md:para><md:text>Two.</md:text></md:para>
            """)
            .Should().Be("# Title\n\nOne.\n\nTwo.\n");

    [Fact]
    public void Output_EndsWithExactlyOneNewline()
    {
        var result = Serialize("""<md:para><md:text>Text.</md:text></md:para>""");

        result.Should().EndWith("\n");
        result.Should().NotEndWith("\n\n");
    }

    [Fact]
    public void Output_NeverUsesCarriageReturns()
    {
        // A byte-identical guarantee that changes with the host OS is not a guarantee.
        Serialize("""
            <md:para><md:text>A.</md:text></md:para>
            <md:para><md:text>B.</md:text></md:para>
            """).Should().NotContain("\r");
    }

    [Fact]
    public void Inline_EmitsStrongEmphasisAndCode()
        => Serialize("""
            <md:para><md:strong><md:text>bold</md:text></md:strong><md:text> and </md:text><md:em><md:text>italic</md:text></md:em><md:text> and </md:text><md:code><md:text>code</md:text></md:code></md:para>
            """)
            .Should().Be("**bold** and *italic* and `code`\n");

    [Fact]
    public void Link_EmitsInlineForm()
        => Serialize("""<md:para><md:link href="https://example.com/spec"><md:text>the spec</md:text></md:link></md:para>""")
            .Should().Be("[the spec](https://example.com/spec)\n");

    [Fact]
    public void Image_EmitsAltTextAndSource()
        => Serialize("""<md:para><md:image src="img/report/image1.png" alt="Figure 1"/></md:para>""")
            .Should().Be("![Figure 1](img/report/image1.png)\n");

    [Fact]
    public void Image_WithNoAltTextStillEmitsEmptyBrackets()
        => Serialize("""<md:para><md:image src="img/report/image2.png" alt=""/></md:para>""")
            .Should().Be("![](img/report/image2.png)\n");

    [Fact]
    public void Blockquote_PrefixesEveryLine()
        => Serialize("""<md:blockquote><md:para><md:text>Caution.</md:text></md:para></md:blockquote>""")
            .Should().Be("> Caution.\n");

    [Fact]
    public void CodeBlock_UsesFencesAndCarriesItsLanguage()
        => Serialize("""<md:code-block language="csharp"><md:text>var x = 1;</md:text></md:code-block>""")
            .Should().Be("```csharp\nvar x = 1;\n```\n");

    [Fact]
    public void ThematicBreak_IsEmitted()
        => Serialize("<md:hr/>").Should().Be("---\n");

    [Fact]
    public void EmptyDocument_ProducesEmptyString()
        => Serialize("").Should().BeEmpty();
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Ooxml.Md.Core.Tests/Ooxml.Md.Core.Tests.csproj --filter "FullyQualifiedName~MarkdownSerializer"`
Expected: FAIL — `MarkdownSerializer` does not exist.

- [ ] **Step 3: Implement the names and options**

`src/Ooxml.Md.Core/Markdown/MdNames.cs`:
```csharp
namespace Ooxml.Md.Core.Markdown;

using System.Xml.Linq;

/// <summary>
/// The md-XML vocabulary: the intermediate tree a stylesheet emits and the serialiser
/// consumes. Declared once so a typo is a compile error rather than a silently dropped
/// element.
/// </summary>
public static class MdNames
{
    public static readonly XNamespace Md = "https://phoenixml.dev/docmd/md";

    public static readonly XName Document = Md + "document";
    public static readonly XName Heading = Md + "heading";
    public static readonly XName Para = Md + "para";
    public static readonly XName List = Md + "list";
    public static readonly XName Item = Md + "item";
    public static readonly XName Table = Md + "table";
    public static readonly XName Row = Md + "row";
    public static readonly XName Cell = Md + "cell";
    public static readonly XName CodeBlock = Md + "code-block";
    public static readonly XName Blockquote = Md + "blockquote";
    public static readonly XName Hr = Md + "hr";

    public static readonly XName Text = Md + "text";
    public static readonly XName Strong = Md + "strong";
    public static readonly XName Em = Md + "em";
    public static readonly XName Code = Md + "code";
    public static readonly XName Link = Md + "link";
    public static readonly XName Image = Md + "image";
    public static readonly XName Br = Md + "br";
}
```

`src/Ooxml.Md.Core/Markdown/MarkdownOptions.cs`:
```csharp
namespace Ooxml.Md.Core.Markdown;

/// <summary>Which Markdown dialect to emit.</summary>
public enum MarkdownFlavour
{
    /// <summary>GitHub Flavored Markdown: pipe tables and strikethrough.</summary>
    Gfm,

    /// <summary>Strict CommonMark: no tables, which are emitted as HTML instead.</summary>
    CommonMark,
}

/// <summary>
/// Serialisation settings. Flavour lives here rather than in the stylesheet so a dialect
/// change never forks the transform.
/// </summary>
public sealed record MarkdownOptions
{
    public MarkdownFlavour Flavour { get; init; } = MarkdownFlavour.Gfm;

    public static MarkdownOptions Default { get; } = new();
}
```

- [ ] **Step 4: Implement the serialiser**

`src/Ooxml.Md.Core/Markdown/MarkdownSerializer.cs`:
```csharp
namespace Ooxml.Md.Core.Markdown;

using System.Globalization;
using System.Text;
using System.Xml.Linq;

/// <summary>
/// Turns an md-XML tree into Markdown text.
/// </summary>
/// <remarks>
/// This exists so that every whitespace, blank-line and escaping rule lives in one place.
/// The alternative — emitting text directly from XSLT — spreads those rules across every
/// template, which is where converters classically produce subtly broken output. See
/// spec §4.5.
/// </remarks>
public static class MarkdownSerializer
{
    /// <summary>Always LF. A byte-identical guarantee that varies by host OS is not one.</summary>
    private const string Newline = "\n";

    public static string Serialize(XDocument mdXml, MarkdownOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(mdXml);
        var effective = options ?? MarkdownOptions.Default;

        var root = mdXml.Root ?? throw new ArgumentException("The md-XML document has no root.", nameof(mdXml));
        var builder = new StringBuilder();
        WriteBlocks(builder, root.Elements(), effective, indent: "");

        var text = builder.ToString().TrimEnd('\n');
        return text.Length == 0 ? "" : text + Newline;
    }

    private static void WriteBlocks(StringBuilder builder, IEnumerable<XElement> blocks, MarkdownOptions options, string indent)
    {
        var first = true;
        foreach (var block in blocks)
        {
            if (!first)
            {
                builder.Append(Newline);
            }

            first = false;
            WriteBlock(builder, block, options, indent);
        }
    }

    private static void WriteBlock(StringBuilder builder, XElement block, MarkdownOptions options, string indent)
    {
        if (block.Name == MdNames.Heading)
        {
            var level = Math.Clamp(ReadInt(block, "level") ?? 1, 1, 6);
            builder.Append(indent).Append('#', level).Append(' ');
            WriteInline(builder, block.Nodes(), options);
            builder.Append(Newline);
        }
        else if (block.Name == MdNames.Para)
        {
            builder.Append(indent);
            WriteInline(builder, block.Nodes(), options);
            builder.Append(Newline);
        }
        else if (block.Name == MdNames.Blockquote)
        {
            var inner = new StringBuilder();
            WriteBlocks(inner, block.Elements(), options, indent: "");
            foreach (var line in inner.ToString().TrimEnd('\n').Split('\n'))
            {
                builder.Append(indent).Append("> ").Append(line).Append(Newline);
            }
        }
        else if (block.Name == MdNames.CodeBlock)
        {
            var language = (string?)block.Attribute("language") ?? "";
            builder.Append(indent).Append("```").Append(language).Append(Newline);
            foreach (var line in block.Value.TrimEnd('\n').Split('\n'))
            {
                builder.Append(indent).Append(line).Append(Newline);
            }

            builder.Append(indent).Append("```").Append(Newline);
        }
        else if (block.Name == MdNames.Hr)
        {
            builder.Append(indent).Append("---").Append(Newline);
        }
        else if (block.Name == MdNames.List)
        {
            WriteList(builder, block, options, indent);
        }
        else if (block.Name == MdNames.Table)
        {
            WriteTable(builder, block, options, indent);
        }
        // Unknown block elements are skipped rather than throwing: a stylesheet ahead of
        // this serialiser must degrade, never crash a batch conversion.
    }

    private static void WriteInline(StringBuilder builder, IEnumerable<XNode> nodes, MarkdownOptions options)
    {
        foreach (var node in nodes)
        {
            if (node is not XElement element)
            {
                continue;
            }

            if (element.Name == MdNames.Text)
            {
                builder.Append(MarkdownEscaper.EscapeInline(element.Value));
            }
            else if (element.Name == MdNames.Strong)
            {
                builder.Append("**");
                WriteInline(builder, element.Nodes(), options);
                builder.Append("**");
            }
            else if (element.Name == MdNames.Em)
            {
                builder.Append('*');
                WriteInline(builder, element.Nodes(), options);
                builder.Append('*');
            }
            else if (element.Name == MdNames.Code)
            {
                // Code spans are NOT escaped -- that is their entire purpose. The fence
                // is widened instead if the content contains backticks.
                builder.Append(MarkdownEscaper.CodeSpan(element.Value));
            }
            else if (element.Name == MdNames.Link)
            {
                builder.Append('[');
                WriteInline(builder, element.Nodes(), options);
                builder.Append("](").Append(MarkdownEscaper.EscapeUrl((string?)element.Attribute("href") ?? "")).Append(')');
            }
            else if (element.Name == MdNames.Image)
            {
                builder.Append("![")
                       .Append(MarkdownEscaper.EscapeInline((string?)element.Attribute("alt") ?? ""))
                       .Append("](")
                       .Append(MarkdownEscaper.EscapeUrl((string?)element.Attribute("src") ?? ""))
                       .Append(')');
            }
            else if (element.Name == MdNames.Br)
            {
                // Two trailing spaces is the portable hard break.
                builder.Append("  ").Append(Newline);
            }
        }
    }

    private static void WriteList(StringBuilder builder, XElement list, MarkdownOptions options, string indent)
    {
        var ordered = string.Equals((string?)list.Attribute("ordered"), "true", StringComparison.Ordinal);
        var number = ReadInt(list, "start") ?? 1;

        foreach (var item in list.Elements(MdNames.Item))
        {
            var marker = ordered
                ? string.Create(CultureInfo.InvariantCulture, $"{number}. ")
                : "- ";
            number++;

            // Continuation lines align under the marker, which is what makes nesting work.
            var childIndent = indent + new string(' ', marker.Length);
            var inner = new StringBuilder();
            WriteBlocks(inner, item.Elements(), options, indent: "");

            var lines = inner.ToString().TrimEnd('\n').Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                builder.Append(i == 0 ? indent + marker : childIndent)
                       .Append(lines[i])
                       .Append(Newline);
            }
        }
    }

    private static void WriteTable(StringBuilder builder, XElement table, MarkdownOptions options, string indent)
    {
        var rows = table.Elements(MdNames.Row).ToArray();
        if (rows.Length == 0)
        {
            return;
        }

        var columnCount = rows.Max(r => r.Elements(MdNames.Cell).Count());

        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            var cells = rows[rowIndex].Elements(MdNames.Cell).ToArray();
            builder.Append(indent).Append("| ");
            for (var column = 0; column < columnCount; column++)
            {
                if (column < cells.Length)
                {
                    WriteInline(builder, cells[column].Nodes(), options);
                }

                builder.Append(" | ");
            }

            builder.Length -= 1; // drop the trailing space
            builder.Append(Newline);

            // GFM requires the delimiter row immediately after the header row.
            if (rowIndex == 0)
            {
                builder.Append(indent).Append('|');
                for (var column = 0; column < columnCount; column++)
                {
                    builder.Append(" --- |");
                }

                builder.Append(Newline);
            }
        }
    }

    private static int? ReadInt(XElement element, string attributeName)
        => int.TryParse((string?)element.Attribute(attributeName), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
}
```

This references `MarkdownEscaper`, which Task 6 builds. **Create a minimal placeholder now** so this task compiles and its tests pass, then Task 6 replaces it test-first:

```csharp
namespace Ooxml.Md.Core.Markdown;

internal static class MarkdownEscaper
{
    // Replaced properly in Task 6.
    internal static string EscapeInline(string text) => text;
    internal static string EscapeUrl(string url) => url;
    internal static string CodeSpan(string content) => $"`{content}`";
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Ooxml.Md.Core.Tests/Ooxml.Md.Core.Tests.csproj --filter "FullyQualifiedName~MarkdownSerializer"`
Expected: PASS, 13 tests.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Serialise Markdown from a tree, so whitespace rules live in one place

The stylesheet emits semantic md-XML and this turns it into text. The
obvious alternative, xsl:output method='text', spreads blank-line and
indentation handling across every template, and that is where converters
classically produce subtly broken output.

Concentrating it here also makes dialect a setting rather than a
stylesheet fork, and gives escaping a single testable home -- which the
next commit fills in.

Line endings are LF on every platform. A byte-identical output guarantee
that changes with the host OS is not a guarantee."
```

---

### Task 6: Escaping, and a differential oracle that proves it

**Files:**
- Modify: `src/Ooxml.Md.Core/Markdown/MarkdownEscaper.cs` (replace the Task 5 placeholder)
- Modify: `src/Ooxml.Md.Core/Markdown/MarkdownSerializer.cs` — line-start escaping in `WriteBlock`
- Test: `tests/Ooxml.Md.Core.Tests/MarkdownEscaperTests.cs`, `tests/Ooxml.Md.Core.Tests/MarkdigOracleTests.cs`

**Interfaces:**
- Consumes: `MarkdownSerializer`, `MdNames` (Task 5).
- Produces:
  - `MarkdownEscaper.EscapeInline(string text) : string`
  - `MarkdownEscaper.EscapeLineStart(string line) : string`
  - `MarkdownEscaper.EscapeUrl(string url) : string`
  - `MarkdownEscaper.CodeSpan(string content) : string`

**Context:** Spec §13.2. Word documents contain literal `*`, `_`, `|`, `#`, `[` in ordinary prose — part numbers, file paths, wildcards, regexes. Emitted unescaped they become markup: an unescaped `|` invents a table cell, a `#` at line start invents a heading, an `_` in a filename italicises the rest of a paragraph.

The test strategy is the interesting part. Rather than one test per character, **parse the output back with Markdig and compare**. That answers the question a golden file cannot — *does this Markdown mean what was intended* — and catches escaping bugs we did not think to enumerate. Markdig is test-only; it never ships in the product.

**Code spans are deliberately exempt.** Escaping inside backticks would emit literal backslashes to the reader. Content containing backticks is handled by widening the fence, which is what CommonMark specifies.

- [ ] **Step 1: Write the failing escaper tests**

`tests/Ooxml.Md.Core.Tests/MarkdownEscaperTests.cs`:
```csharp
namespace Ooxml.Md.Core.Tests;

using FluentAssertions;
using Ooxml.Md.Core.Markdown;
using Xunit;

public sealed class MarkdownEscaperTests
{
    [Theory]
    [InlineData("plain text", "plain text")]
    [InlineData("a*b", @"a\*b")]
    [InlineData("file_name_here", @"file\_name\_here")]
    [InlineData("a|b", @"a\|b")]
    [InlineData("[bracket]", @"\[bracket\]")]
    [InlineData("back`tick", @"back\`tick")]
    [InlineData(@"back\slash", @"back\\slash")]
    [InlineData("<tag>", @"\<tag\>")]
    public void EscapeInline_EscapesMarkupCharacters(string input, string expected)
        => MarkdownEscaper.EscapeInline(input).Should().Be(expected);

    [Theory]
    [InlineData("# not a heading", @"\# not a heading")]
    [InlineData("> not a quote", @"\> not a quote")]
    [InlineData("- not a list", @"\- not a list")]
    [InlineData("+ not a list", @"\+ not a list")]
    [InlineData("1. not a list", @"1\. not a list")]
    [InlineData("ordinary text", "ordinary text")]
    [InlineData("mid # hash is fine", "mid # hash is fine")]
    public void EscapeLineStart_EscapesOnlyLeadingBlockMarkers(string input, string expected)
        => MarkdownEscaper.EscapeLineStart(input).Should().Be(expected);

    [Fact]
    public void CodeSpan_LeavesContentUnescaped()
        => MarkdownEscaper.CodeSpan("a*b_c").Should().Be("`a*b_c`");

    [Fact]
    public void CodeSpan_WidensTheFenceWhenContentHasBackticks()
        // CommonMark: a span containing a backtick is delimited by a longer run, with
        // padding spaces so the inner backtick is not consumed by the fence.
        => MarkdownEscaper.CodeSpan("a`b").Should().Be("`` a`b ``");

    [Fact]
    public void EscapeUrl_EncodesSpacesAndParentheses()
        => MarkdownEscaper.EscapeUrl("img/my report/fig (1).png")
            .Should().Be("img/my%20report/fig%20%281%29.png");
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Ooxml.Md.Core.Tests/Ooxml.Md.Core.Tests.csproj --filter "FullyQualifiedName~MarkdownEscaper"`
Expected: FAIL — the placeholder returns input unchanged.

- [ ] **Step 3: Implement the escaper**

Replace `src/Ooxml.Md.Core/Markdown/MarkdownEscaper.cs`:
```csharp
namespace Ooxml.Md.Core.Markdown;

using System.Text;

/// <summary>
/// The single source of truth for turning literal document text into safe Markdown.
/// </summary>
/// <remarks>
/// Word documents carry literal <c>*</c>, <c>_</c>, <c>|</c>, <c>#</c> and <c>[</c> in
/// ordinary prose — part numbers, paths, wildcards. Emitted raw they become markup. The
/// proof that this is right is not the tests below but the Markdig differential oracle:
/// serialise, parse back, compare.
/// </remarks>
public static class MarkdownEscaper
{
    /// <summary>
    /// Characters that can begin inline markup anywhere in a line. Deliberately narrower
    /// than CommonMark's full punctuation set: escaping everything is legal but produces
    /// backslash-strewn output that reads badly for humans, and these are the characters
    /// that actually change meaning.
    /// </summary>
    private static readonly char[] InlineSpecials = ['\\', '`', '*', '_', '[', ']', '<', '>', '|'];

    public static string EscapeInline(string text)
    {
        if (string.IsNullOrEmpty(text) || text.AsSpan().IndexOfAny(InlineSpecials) < 0)
        {
            return text ?? "";
        }

        var builder = new StringBuilder(text.Length + 8);
        foreach (var ch in text)
        {
            if (Array.IndexOf(InlineSpecials, ch) >= 0)
            {
                builder.Append('\\');
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Escapes markers that only mean something at the start of a line. Applied by the
    /// serialiser after inline escaping, because whether a character is line-leading is a
    /// property of the assembled line, not of any single text node.
    /// </summary>
    public static string EscapeLineStart(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return line ?? "";
        }

        var leading = line.Length - line.TrimStart(' ').Length;
        var rest = line[leading..];
        if (rest.Length == 0)
        {
            return line;
        }

        // Block markers: heading, quote, bullet, thematic break.
        if (rest[0] is '#' or '>' or '-' or '+')
        {
            return string.Concat(line.AsSpan(0, leading), "\\", rest);
        }

        // Ordered-list marker: digits followed by '.' or ')'.
        var digits = 0;
        while (digits < rest.Length && char.IsAsciiDigit(rest[digits]))
        {
            digits++;
        }

        if (digits > 0 && digits < rest.Length && rest[digits] is '.' or ')')
        {
            return string.Concat(line.AsSpan(0, leading), rest.AsSpan(0, digits), "\\", rest.AsSpan(digits));
        }

        return line;
    }

    /// <summary>
    /// Wraps content as a code span. Content is never escaped — that is what a code span
    /// is for — so a backtick inside is handled by widening the fence, per CommonMark.
    /// </summary>
    public static string CodeSpan(string content)
    {
        var text = content ?? "";
        var longestRun = 0;
        var currentRun = 0;
        foreach (var ch in text)
        {
            currentRun = ch == '`' ? currentRun + 1 : 0;
            longestRun = Math.Max(longestRun, currentRun);
        }

        var fence = new string('`', longestRun + 1);

        // Padding spaces stop the fence from swallowing a leading/trailing backtick.
        return longestRun == 0 ? $"{fence}{text}{fence}" : $"{fence} {text} {fence}";
    }

    /// <summary>
    /// Percent-encodes the characters that would terminate an inline link destination.
    /// </summary>
    public static string EscapeUrl(string url)
        => (url ?? "")
            .Replace("%", "%25", StringComparison.Ordinal)
            .Replace(" ", "%20", StringComparison.Ordinal)
            .Replace("(", "%28", StringComparison.Ordinal)
            .Replace(")", "%29", StringComparison.Ordinal);
}
```

- [ ] **Step 4: Apply line-start escaping in the serialiser**

In `MarkdownSerializer.WriteBlock`, the `MdNames.Para` branch becomes:
```csharp
        else if (block.Name == MdNames.Para)
        {
            var line = new StringBuilder();
            WriteInline(line, block.Nodes(), options);
            builder.Append(indent)
                   .Append(MarkdownEscaper.EscapeLineStart(line.ToString()))
                   .Append(Newline);
        }
```

Only paragraphs need this. A heading already starts with `#` by construction, and list items and table cells are not at line start once their marker or pipe precedes them.

- [ ] **Step 5: Write the Markdig differential oracle**

`tests/Ooxml.Md.Core.Tests/MarkdigOracleTests.cs`:
```csharp
namespace Ooxml.Md.Core.Tests;

using System.Xml.Linq;
using FluentAssertions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Ooxml.Md.Core.Markdown;
using Xunit;

/// <summary>
/// Serialise, parse back with an independent Markdown implementation, and compare. This
/// answers what a golden file cannot: does the Markdown we wrote MEAN what we intended?
/// One mechanism catches every escaping bug rather than one test per character.
/// Markdig is test-only and never ships in the product. See spec §13.2.
/// </summary>
public sealed class MarkdigOracleTests
{
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    private static string RoundTripParagraphText(string literal)
    {
        var mdXml = new XDocument(
            new XElement(MdNames.Document,
                new XElement(MdNames.Para,
                    new XElement(MdNames.Text, literal))));

        var markdown = MarkdownSerializer.Serialize(mdXml);
        var parsed = Markdig.Markdown.Parse(markdown, Pipeline);

        var paragraph = parsed.Descendants<ParagraphBlock>().Single();
        return string.Concat(paragraph.Inline!.Descendants<LiteralInline>().Select(l => l.ToString()));
    }

    [Theory]
    [InlineData("plain text")]
    [InlineData("part number A*B*C")]
    [InlineData("path/to/my_file_name.txt")]
    [InlineData("a | b | c")]
    [InlineData("see [note] below")]
    [InlineData("regex ^[a-z]+$ matches")]
    [InlineData("100% of 5 < 10 > 2")]
    [InlineData(@"windows\path\here")]
    [InlineData("# not a heading")]
    [InlineData("1. not a list item")]
    [InlineData("- not a bullet")]
    [InlineData("> not a quote")]
    [InlineData("emphasis*without*spaces")]
    [InlineData("back`tick inside")]
    public void ParagraphText_SurvivesTheRoundTrip(string literal)
    {
        // If this fails, the serialiser emitted something that a real Markdown parser
        // read as markup rather than as the document's words.
        RoundTripParagraphText(literal).Should().Be(literal);
    }

    [Fact]
    public void CodeSpanContent_SurvivesUnescaped()
    {
        var mdXml = new XDocument(
            new XElement(MdNames.Document,
                new XElement(MdNames.Para,
                    new XElement(MdNames.Code, new XElement(MdNames.Text, "a*b_c")))));

        var markdown = MarkdownSerializer.Serialize(mdXml);
        var parsed = Markdig.Markdown.Parse(markdown, Pipeline);

        // The reader must see a*b_c, with no backslashes introduced.
        parsed.Descendants<CodeInline>().Single().Content.Should().Be("a*b_c");
    }

    [Fact]
    public void HeadingLevel_SurvivesTheRoundTrip()
    {
        var mdXml = new XDocument(
            new XElement(MdNames.Document,
                new XElement(MdNames.Heading, new XAttribute("level", 3),
                    new XElement(MdNames.Text, "Findings"))));

        var parsed = Markdig.Markdown.Parse(MarkdownSerializer.Serialize(mdXml), Pipeline);

        var heading = parsed.Descendants<HeadingBlock>().Single();
        heading.Level.Should().Be(3);
    }

    [Fact]
    public void TableStructure_SurvivesTheRoundTrip()
    {
        var mdXml = new XDocument(
            new XElement(MdNames.Document,
                new XElement(MdNames.Table,
                    new XElement(MdNames.Row, new XAttribute("header", "true"),
                        new XElement(MdNames.Cell, new XElement(MdNames.Text, "Item")),
                        new XElement(MdNames.Cell, new XElement(MdNames.Text, "Status"))),
                    new XElement(MdNames.Row,
                        new XElement(MdNames.Cell, new XElement(MdNames.Text, "Vent")),
                        new XElement(MdNames.Cell, new XElement(MdNames.Text, "Pass"))))));

        var parsed = Markdig.Markdown.Parse(MarkdownSerializer.Serialize(mdXml), Pipeline);

        parsed.Descendants<Markdig.Extensions.Tables.Table>().Should().ContainSingle();
    }
}
```

- [ ] **Step 6: Run all serialiser tests to verify they pass**

Run: `dotnet test tests/Ooxml.Md.Core.Tests/Ooxml.Md.Core.Tests.csproj`
Expected: PASS. If a `ParagraphText_SurvivesTheRoundTrip` case fails, the escaper is wrong for that character class — fix `MarkdownEscaper`, not the test.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Escape document text, and prove it by parsing the output back

Word documents carry literal *, _, |, # and [ in ordinary prose: part
numbers, paths, wildcards, regexes. Emitted raw they become markup -- an
unescaped pipe invents a table cell, a leading hash invents a heading, an
underscore in a filename italicises the rest of the paragraph.

The proof is a differential oracle rather than one test per character.
Serialise, parse the result back with Markdig, and compare: that answers
whether the Markdown MEANS what was intended, and catches escaping bugs
nobody thought to enumerate. Markdig is test-only and never ships.

Code spans are deliberately exempt, since escaping inside backticks would
show the reader literal backslashes; a backtick in the content widens the
fence instead, as CommonMark specifies.

Line-start markers are escaped on the assembled line, not per text node,
because whether a character leads a line is a property of the line."
```

---
### Task 7: The stylesheet — blocks and inline formatting

**Files:**
- Create: `src/Docmd.Word/Stylesheets/markdown.xslt`, `src/Docmd.Word/StylesheetLoader.cs`
- Test: `tests/Docmd.Word.Tests/MarkdownStylesheetTests.cs`

**Interfaces:**
- Consumes: the annotated composite (Tasks 3–4), `MdNames` (Task 5).
- Produces:
  - `StylesheetLoader.Read(string resourceName) : string`
  - `markdown.xslt` — transforms `docmd:package` into an `md:document` tree

**Context:** Stage 4. The stylesheet's output is md-XML, never text — the serialiser owns text. Its job is purely semantic recovery: which paragraph is a heading, which run is bold, which text is code.

**Headings emit plain text only.** A bold run inside a `Heading 1` would otherwise produce `# **Title**` — valid but noisy, and it adds nothing since the heading is already prominent. A separate mode handles this rather than a conditional in the shared run template.

- [ ] **Step 1: Write the stylesheet loader**

`src/Docmd.Word/StylesheetLoader.cs`:
```csharp
namespace Docmd.Word;

using System.Reflection;

/// <summary>Reads a stylesheet embedded in this assembly.</summary>
/// <remarks>
/// Stylesheets ship as embedded resources rather than loose files so a `dotnet tool`
/// install is a single artifact with nothing to lose on the way.
/// </remarks>
public static class StylesheetLoader
{
    public static string Read(string resourceName)
    {
        ArgumentException.ThrowIfNullOrEmpty(resourceName);

        var assembly = typeof(StylesheetLoader).Assembly;
        var qualified = $"{typeof(StylesheetLoader).Namespace}.Stylesheets.{resourceName}";

        using var stream = assembly.GetManifestResourceStream(qualified)
            ?? throw new InvalidOperationException(
                $"Embedded stylesheet '{qualified}' not found. Available: " +
                string.Join(", ", assembly.GetManifestResourceNames()));

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
```

- [ ] **Step 2: Write the failing tests**

`tests/Docmd.Word.Tests/MarkdownStylesheetTests.cs`:
```csharp
namespace Docmd.Word.Tests;

using System.Threading.Tasks;
using System.Xml.Linq;
using Docmd.Word;
using Docmd.Word.Assembly;
using FluentAssertions;
using Ooxml.Md.Core.Markdown;
using PhoenixmlDb.Xslt;
using Xunit;

public sealed class MarkdownStylesheetTests
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    /// <summary>Runs the real stylesheet over a hand-built composite and returns md-XML.</summary>
    private static async Task<XDocument> TransformAsync(string bodyInner, string stylesInner = "", string numberingInner = "")
    {
        var composite = XDocument.Parse($"""
            <docmd:package xmlns:docmd="https://phoenixml.dev/docmd" xmlns:w="{W}">
              <docmd:body><w:body>{bodyInner}</w:body></docmd:body>
              <docmd:styles><w:styles>{stylesInner}</w:styles></docmd:styles>
              <docmd:numbering><w:numbering>{numberingInner}</w:numbering></docmd:numbering>
              <docmd:relationships/>
              <docmd:properties/>
            </docmd:package>
            """);

        HeadingAnnotator.Annotate(composite);

        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(StylesheetLoader.Read("markdown.xslt"));
        return XDocument.Parse(await transformer.TransformAsync(composite.ToString()));
    }

    /// <summary>Transforms, then serialises — the two stages callers actually compose.</summary>
    private static async Task<string> ToMarkdownAsync(string bodyInner, string stylesInner = "", string numberingInner = "")
        => MarkdownSerializer.Serialize(await TransformAsync(bodyInner, stylesInner, numberingInner));

    [Fact]
    public async Task Paragraph_BecomesAParagraph()
        => (await ToMarkdownAsync("""<w:p><w:r><w:t>Hello.</w:t></w:r></w:p>"""))
            .Should().Be("Hello.\n");

    [Fact]
    public async Task Heading_BecomesHashesAtTheAnnotatedLevel()
        => (await ToMarkdownAsync("""
            <w:p><w:pPr><w:outlineLvl w:val="1"/></w:pPr><w:r><w:t>Findings</w:t></w:r></w:p>
            """))
            .Should().Be("## Findings\n");

    [Fact]
    public async Task Heading_DropsInlineBoldToAvoidNoise()
    {
        // "# **Title**" is valid but adds nothing -- the heading is already prominent.
        var markdown = await ToMarkdownAsync("""
            <w:p><w:pPr><w:outlineLvl w:val="0"/></w:pPr>
              <w:r><w:rPr><w:b/></w:rPr><w:t>Title</w:t></w:r>
            </w:p>
            """);

        markdown.Should().Be("# Title\n");
    }

    [Fact]
    public async Task Runs_CarryBoldAndItalic()
        => (await ToMarkdownAsync("""
            <w:p>
              <w:r><w:rPr><w:b/></w:rPr><w:t>bold</w:t></w:r>
              <w:r><w:t> and </w:t></w:r>
              <w:r><w:rPr><w:i/></w:rPr><w:t>italic</w:t></w:r>
            </w:p>
            """))
            .Should().Be("**bold** and *italic*\n");

    [Fact]
    public async Task Runs_NestBoldAndItalicTogether()
        => (await ToMarkdownAsync("""
            <w:p><w:r><w:rPr><w:b/><w:i/></w:rPr><w:t>both</w:t></w:r></w:p>
            """))
            .Should().Be("***both***\n");

    [Fact]
    public async Task Runs_SplitMidWordByWordAreRejoined()
    {
        // Word splits runs constantly -- spell-check state, rsids, formatting boundaries.
        // Emitting one md:text per run would produce "**re**suming" style artefacts.
        var markdown = await ToMarkdownAsync("""
            <w:p>
              <w:r><w:t>resum</w:t></w:r>
              <w:r><w:t>ing</w:t></w:r>
            </w:p>
            """);

        markdown.Should().Be("resuming\n");
    }

    [Fact]
    public async Task PreservedSpaces_AreHonoured()
        => (await ToMarkdownAsync("""
            <w:p>
              <w:r><w:t xml:space="preserve">before </w:t></w:r>
              <w:r><w:t>after</w:t></w:r>
            </w:p>
            """))
            .Should().Be("before after\n");

    [Fact]
    public async Task LineBreak_BecomesAHardBreak()
        => (await ToMarkdownAsync("""
            <w:p><w:r><w:t>one</w:t><w:br/><w:t>two</w:t></w:r></w:p>
            """))
            .Should().Be("one  \ntwo\n");

    [Fact]
    public async Task EmptyParagraphs_AreDropped()
    {
        // Word documents are full of empty paragraphs used as vertical spacing. Emitting
        // them produces runs of blank lines that mean nothing to a reader or an indexer.
        var markdown = await ToMarkdownAsync("""
            <w:p><w:r><w:t>One.</w:t></w:r></w:p>
            <w:p/>
            <w:p><w:r><w:t>Two.</w:t></w:r></w:p>
            """);

        markdown.Should().Be("One.\n\nTwo.\n");
    }

    [Fact]
    public async Task DeletedText_IsNotEmitted()
    {
        // w:delText, not w:t. A naive //w:t harvest silently accepts every tracked change
        // while appearing to work. Full revision handling arrives in Plan 2; for now the
        // default (accept) must at least not resurrect deleted words.
        var markdown = await ToMarkdownAsync("""
            <w:p>
              <w:r><w:t>The vent </w:t></w:r>
              <w:del><w:r><w:delText>was</w:delText></w:r></w:del>
              <w:ins><w:r><w:t>is</w:t></w:r></w:ins>
              <w:r><w:t> compliant</w:t></w:r>
            </w:p>
            """);

        markdown.Should().Be("The vent is compliant\n");
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test tests/Docmd.Word.Tests/Docmd.Word.Tests.csproj --filter "FullyQualifiedName~MarkdownStylesheet"`
Expected: FAIL — `markdown.xslt` does not exist.

- [ ] **Step 4: Write the stylesheet**

`src/Docmd.Word/Stylesheets/markdown.xslt`:
```xml
<?xml version="1.0" encoding="UTF-8"?>
<!--
  WordprocessingML -> md-XML.

  This stylesheet emits a semantic tree, never Markdown text: MarkdownSerializer owns
  every whitespace and escaping rule (spec §4.5). Heading levels and slugs are read from
  the docmd: attributes that HeadingAnnotator stamped on, never recomputed here, so the
  Markdown and the review companion cannot disagree (spec §4.4).
-->
<xsl:stylesheet version="3.0"
    xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
    xmlns:xs="http://www.w3.org/2001/XMLSchema"
    xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"
    xmlns:docmd="https://phoenixml.dev/docmd"
    xmlns:md="https://phoenixml.dev/docmd/md"
    exclude-result-prefixes="xs w docmd">

  <xsl:output method="xml" indent="no"/>
  <xsl:strip-space elements="*"/>
  <xsl:preserve-space elements="w:t w:delText"/>

  <xsl:template match="/docmd:package">
    <md:document>
      <xsl:apply-templates select="docmd:body/w:body"/>
    </md:document>
  </xsl:template>

  <xsl:template match="w:body">
    <xsl:apply-templates select="*"/>
  </xsl:template>

  <!-- Headings. Level comes from annotation and is zero-based, so +1 for Markdown. -->
  <xsl:template match="w:p[@docmd:heading-source and @docmd:heading-source ne 'None']">
    <md:heading level="{xs:integer(@docmd:outline-level) + 1}" slug="{@docmd:slug}">
      <!-- Plain text only: '# **Title**' is noise. -->
      <md:text><xsl:value-of select="docmd:visible-text(.)"/></md:text>
    </md:heading>
  </xsl:template>

  <!-- Empty paragraphs are vertical spacing in Word and mean nothing here. -->
  <xsl:template match="w:p[not(normalize-space(docmd:visible-text(.)))][not(.//w:drawing)]"/>

  <xsl:template match="w:p">
    <md:para>
      <xsl:apply-templates select="w:r | w:ins | w:hyperlink" mode="inline"/>
    </md:para>
  </xsl:template>

  <!-- Insertions are part of the accepted text; deletions are not reached at all,
       because no template selects w:del. -->
  <xsl:template match="w:ins" mode="inline">
    <xsl:apply-templates select="w:r" mode="inline"/>
  </xsl:template>

  <xsl:template match="w:r" mode="inline">
    <xsl:variable name="text" select="string-join(w:t, '')"/>

    <xsl:if test="$text ne '' or w:br">
      <xsl:variable name="innermost" as="node()*">
        <xsl:if test="$text ne ''">
          <md:text><xsl:value-of select="$text"/></md:text>
        </xsl:if>
      </xsl:variable>

      <xsl:variable name="italicised" as="node()*">
        <xsl:choose>
          <xsl:when test="w:rPr/w:i and $text ne ''">
            <md:em><xsl:sequence select="$innermost"/></md:em>
          </xsl:when>
          <xsl:otherwise><xsl:sequence select="$innermost"/></xsl:otherwise>
        </xsl:choose>
      </xsl:variable>

      <xsl:choose>
        <xsl:when test="w:rPr/w:b and $text ne ''">
          <md:strong><xsl:sequence select="$italicised"/></md:strong>
        </xsl:when>
        <xsl:otherwise><xsl:sequence select="$italicised"/></xsl:otherwise>
      </xsl:choose>

      <xsl:for-each select="w:br"><md:br/></xsl:for-each>
    </xsl:if>
  </xsl:template>

  <!--
    The text a reader would see. Selects w:t and never w:delText, so deleted words are
    absent by construction rather than by a filter someone can forget.
  -->
  <xsl:function name="docmd:visible-text" as="xs:string">
    <xsl:param name="node" as="node()"/>
    <xsl:sequence select="string-join($node//w:t[not(ancestor::w:del)], '')"/>
  </xsl:function>

</xsl:stylesheet>
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Docmd.Word.Tests/Docmd.Word.Tests.csproj --filter "FullyQualifiedName~MarkdownStylesheet"`
Expected: PASS, 10 tests.

If `Runs_SplitMidWordByWordAreRejoined` fails, `string-join(w:t, '')` is not collecting sibling `w:t` elements — check that `xsl:strip-space` has not removed them.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Transform Word bodies into md-XML, recovering semantics not text

The stylesheet emits a semantic tree and never Markdown text -- the
serialiser owns every whitespace and escaping rule. Its job here is purely
which paragraph is a heading, which run is bold, which words a reader
actually sees.

Heading levels and slugs are read from the annotation rather than
recomputed, so the Markdown and the review companion cannot drift apart.

visible-text() selects w:t and never w:delText, which makes deleted words
absent by construction rather than by a filter someone can later forget.
That distinction is the trap in tracked changes: a naive //w:t harvest
keeps insertions, drops deletions, and so silently accepts every change
while appearing to work.

Empty paragraphs are dropped. Word documents are full of them as vertical
spacing, and emitting them produces runs of blank lines that mean nothing
to a reader or an indexer."
```

---

### Task 8: The stylesheet — lists

**Files:**
- Modify: `src/Docmd.Word/Stylesheets/markdown.xslt`
- Test: `tests/Docmd.Word.Tests/ListStylesheetTests.cs`

**Interfaces:**
- Consumes: `markdown.xslt` (Task 7), `MarkdownSerializer` list output (Task 5).
- Produces: `md:list` / `md:item` output from `w:numPr` paragraphs.

**Context:** Word does **not** nest lists. Every list item is a top-level paragraph carrying `<w:pPr><w:numPr><w:ilvl w:val="1"/><w:numId w:val="3"/></w:numPr>`: `numId` says which list, `ilvl` says how deep. Reconstructing a tree from that flat run is precisely what `xsl:for-each-group` exists for, and it is the single best showcase of the engine in this product.

Whether a list is ordered lives in `numbering.xml`, two hops away: `w:num[@w:numId]` → `w:abstractNumId/@w:val` → `w:abstractNum[@w:abstractNumId]/w:lvl[@w:ilvl]/w:numFmt/@w:val`. A `w:numFmt` of `bullet` means unordered; anything else (`decimal`, `lowerLetter`, `upperRoman`) means ordered.

- [ ] **Step 1: Write the failing tests**

`tests/Docmd.Word.Tests/ListStylesheetTests.cs`:
```csharp
namespace Docmd.Word.Tests;

using System.Threading.Tasks;
using System.Xml.Linq;
using Docmd.Word;
using Docmd.Word.Assembly;
using FluentAssertions;
using Ooxml.Md.Core.Markdown;
using PhoenixmlDb.Xslt;
using Xunit;

public sealed class ListStylesheetTests
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    /// <summary>numId 1 is a bullet list; numId 2 is decimal. Both have three levels.</summary>
    private const string Numbering = """
        <w:num w:numId="1"><w:abstractNumId w:val="10"/></w:num>
        <w:num w:numId="2"><w:abstractNumId w:val="20"/></w:num>
        <w:abstractNum w:abstractNumId="10">
          <w:lvl w:ilvl="0"><w:numFmt w:val="bullet"/></w:lvl>
          <w:lvl w:ilvl="1"><w:numFmt w:val="bullet"/></w:lvl>
          <w:lvl w:ilvl="2"><w:numFmt w:val="bullet"/></w:lvl>
        </w:abstractNum>
        <w:abstractNum w:abstractNumId="20">
          <w:lvl w:ilvl="0"><w:numFmt w:val="decimal"/></w:lvl>
          <w:lvl w:ilvl="1"><w:numFmt w:val="lowerLetter"/></w:lvl>
        </w:abstractNum>
        """;

    private static string Item(string text, int numId, int ilvl) => $"""
        <w:p><w:pPr><w:numPr><w:ilvl w:val="{ilvl}"/><w:numId w:val="{numId}"/></w:numPr></w:pPr>
          <w:r><w:t>{text}</w:t></w:r></w:p>
        """;

    private static async Task<string> ToMarkdownAsync(string bodyInner)
    {
        var composite = XDocument.Parse($"""
            <docmd:package xmlns:docmd="https://phoenixml.dev/docmd" xmlns:w="{W}">
              <docmd:body><w:body>{bodyInner}</w:body></docmd:body>
              <docmd:styles><w:styles/></docmd:styles>
              <docmd:numbering><w:numbering>{Numbering}</w:numbering></docmd:numbering>
              <docmd:relationships/><docmd:properties/>
            </docmd:package>
            """);

        HeadingAnnotator.Annotate(composite);
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(StylesheetLoader.Read("markdown.xslt"));
        return MarkdownSerializer.Serialize(XDocument.Parse(await transformer.TransformAsync(composite.ToString())));
    }

    [Fact]
    public async Task BulletList_BecomesHyphens()
        => (await ToMarkdownAsync(Item("One", 1, 0) + Item("Two", 1, 0)))
            .Should().Be("- One\n- Two\n");

    [Fact]
    public async Task NumberedList_BecomesDigits()
        => (await ToMarkdownAsync(Item("First", 2, 0) + Item("Second", 2, 0)))
            .Should().Be("1. First\n2. Second\n");

    [Fact]
    public async Task NestedItems_AreIndentedUnderTheirParent()
        => (await ToMarkdownAsync(
                Item("Top", 1, 0) + Item("Child", 1, 1) + Item("Back", 1, 0)))
            .Should().Be("- Top\n  - Child\n- Back\n");

    [Fact]
    public async Task ThreeLevelsNest()
        => (await ToMarkdownAsync(
                Item("A", 1, 0) + Item("B", 1, 1) + Item("C", 1, 2)))
            .Should().Be("- A\n  - B\n    - C\n");

    [Fact]
    public async Task ListEnds_WhenAnOrdinaryParagraphFollows()
        => (await ToMarkdownAsync(
                Item("One", 1, 0) + """<w:p><w:r><w:t>After.</w:t></w:r></w:p>"""))
            .Should().Be("- One\n\nAfter.\n");

    [Fact]
    public async Task TwoAdjacentListsWithDifferentNumIds_DoNotMerge()
    {
        // Distinct numIds are distinct lists even when adjacent. Merging them would
        // renumber the second one from where the first left off.
        var markdown = await ToMarkdownAsync(Item("Bullet", 1, 0) + Item("Number", 2, 0));

        markdown.Should().Be("- Bullet\n\n1. Number\n");
    }

    [Fact]
    public async Task ListWithNoMatchingNumberingDefinition_FallsBackToBullets()
    {
        // numId 99 is not defined. A missing definition must degrade, never throw:
        // this is a batch tool and one malformed document cannot stop a corpus.
        var markdown = await ToMarkdownAsync(Item("Orphan", 99, 0));

        markdown.Should().Be("- Orphan\n");
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Docmd.Word.Tests/Docmd.Word.Tests.csproj --filter "FullyQualifiedName~ListStylesheet"`
Expected: FAIL — list paragraphs currently become plain paragraphs.

- [ ] **Step 3: Add list handling to the stylesheet**

Add to `markdown.xslt`, before the `w:p` templates:

```xml
  <xsl:variable name="numbering" select="/docmd:package/docmd:numbering/w:numbering"/>

  <xsl:function name="docmd:num-id" as="xs:string">
    <xsl:param name="p" as="element(w:p)"/>
    <xsl:sequence select="string(($p/w:pPr/w:numPr/w:numId/@w:val, '')[1])"/>
  </xsl:function>

  <xsl:function name="docmd:ilvl" as="xs:integer">
    <xsl:param name="p" as="element(w:p)"/>
    <xsl:sequence select="xs:integer(($p/w:pPr/w:numPr/w:ilvl/@w:val, '0')[1])"/>
  </xsl:function>

  <!--
    numFmt lives two hops away: w:num -> abstractNumId -> w:abstractNum -> w:lvl.
    An undefined numId degrades to a bullet rather than failing: one malformed document
    must never stop a corpus conversion.
  -->
  <xsl:function name="docmd:is-ordered" as="xs:boolean">
    <xsl:param name="numbering" as="element()?"/>
    <xsl:param name="numId" as="xs:string"/>
    <xsl:param name="ilvl" as="xs:integer"/>
    <xsl:variable name="abstractId"
        select="string($numbering/w:num[@w:numId eq $numId]/w:abstractNumId/@w:val)"/>
    <xsl:variable name="format"
        select="string($numbering/w:abstractNum[@w:abstractNumId eq $abstractId]
                                  /w:lvl[xs:integer(@w:ilvl) eq $ilvl]/w:numFmt/@w:val)"/>
    <xsl:sequence select="$format ne '' and $format ne 'bullet' and $format ne 'none'"/>
  </xsl:function>

  <!--
    Word stores no nesting: every item is a top-level paragraph with a numId and an ilvl.
    group-adjacent separates list runs from body text; the recursive template below
    rebuilds depth from ilvl.
  -->
  <xsl:template match="w:body">
    <xsl:for-each-group select="*"
        group-adjacent="if (self::w:p[w:pPr/w:numPr]) then docmd:num-id(.) else ''">
      <xsl:choose>
        <xsl:when test="current-grouping-key() ne ''">
          <xsl:call-template name="build-list">
            <xsl:with-param name="items" select="current-group()"/>
            <xsl:with-param name="level" select="docmd:ilvl(current-group()[1])"/>
          </xsl:call-template>
        </xsl:when>
        <xsl:otherwise>
          <xsl:apply-templates select="current-group()"/>
        </xsl:otherwise>
      </xsl:choose>
    </xsl:for-each-group>
  </xsl:template>

  <xsl:template name="build-list">
    <xsl:param name="items" as="element(w:p)*"/>
    <xsl:param name="level" as="xs:integer"/>

    <md:list ordered="{docmd:is-ordered($numbering, docmd:num-id($items[1]), $level)}">
      <xsl:for-each-group select="$items" group-starting-with="w:p[docmd:ilvl(.) le $level]">
        <md:item>
          <md:para>
            <xsl:apply-templates select="current-group()[1]/(w:r | w:ins | w:hyperlink)" mode="inline"/>
          </md:para>
          <xsl:variable name="deeper" select="current-group()[position() gt 1]"/>
          <xsl:if test="exists($deeper)">
            <xsl:call-template name="build-list">
              <xsl:with-param name="items" select="$deeper"/>
              <xsl:with-param name="level" select="$level + 1"/>
            </xsl:call-template>
          </xsl:if>
        </md:item>
      </xsl:for-each-group>
    </md:list>
  </xsl:template>
```

Also add `<xsl:template match="w:p[w:pPr/w:numPr]"/>` so a list paragraph reached by any other route produces nothing rather than a stray paragraph.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Docmd.Word.Tests/Docmd.Word.Tests.csproj --filter "FullyQualifiedName~ListStylesheet"`
Expected: PASS, 7 tests.

- [ ] **Step 5: Run the whole suite to check for regressions**

Run: `dotnet test docmd.slnx`
Expected: PASS. The new `w:body` template replaces Task 7's; confirm Task 7's tests still pass.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Rebuild list nesting, which Word does not store

Word keeps no list tree. Every item is a top-level paragraph carrying a
numId saying which list it belongs to and an ilvl saying how deep it sits,
and whether the list is ordered lives two hops away in numbering.xml:
w:num to abstractNumId to w:abstractNum to w:lvl to w:numFmt.

Reconstructing a tree from that flat run is what for-each-group exists
for. group-adjacent on numId separates list runs from body text and keeps
two adjacent lists with different numIds apart -- merging them would
renumber the second from where the first left off -- and a recursive
group-starting-with rebuilds depth from ilvl.

An undefined numId degrades to a bullet rather than failing. This is a
batch tool, and one malformed document must never stop a corpus."
```

---
### Task 9: The stylesheet — tables

**Files:**
- Modify: `src/Docmd.Word/Stylesheets/markdown.xslt`
- Test: `tests/Docmd.Word.Tests/TableStylesheetTests.cs`

**Interfaces:**
- Consumes: `markdown.xslt` (Tasks 7–8), `MarkdownSerializer` table output (Task 5).
- Produces: `md:table` / `md:row` / `md:cell` output from `w:tbl`.

**Context:** `w:tbl` → `w:tr` → `w:tc`, each cell containing block content. GFM pipe tables are far less expressive than Word's model, so three cases must degrade deliberately rather than produce nonsense:

- **`w:gridSpan`** (horizontal merge): GFM has no colspan. The content goes in the first cell and the spanned positions are emitted empty, which keeps every row the same width — a ragged row breaks the whole table for a parser.
- **`w:vMerge`** (vertical merge): a continuation cell (`w:vMerge` without `w:val="restart"`) is emitted empty.
- **Nested tables:** GFM cannot express them. The inner table's text is flattened into the containing cell, which preserves the words for retrieval even though the structure is lost.
- **Multi-paragraph cells:** a pipe table cell is one line, so paragraphs are joined with a space rather than a newline, which would terminate the row.

Every one of these is reported by the audit in Plan 3. Losing structure silently is the thing to avoid; losing it visibly and on the record is acceptable.

- [ ] **Step 1: Write the failing tests**

`tests/Docmd.Word.Tests/TableStylesheetTests.cs`:
```csharp
namespace Docmd.Word.Tests;

using System.Threading.Tasks;
using System.Xml.Linq;
using Docmd.Word;
using Docmd.Word.Assembly;
using FluentAssertions;
using Ooxml.Md.Core.Markdown;
using PhoenixmlDb.Xslt;
using Xunit;

public sealed class TableStylesheetTests
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static string Cell(string text, string properties = "")
        => $"""<w:tc><w:tcPr>{properties}</w:tcPr><w:p><w:r><w:t>{text}</w:t></w:r></w:p></w:tc>""";

    private static async Task<string> ToMarkdownAsync(string bodyInner)
    {
        var composite = XDocument.Parse($"""
            <docmd:package xmlns:docmd="https://phoenixml.dev/docmd" xmlns:w="{W}">
              <docmd:body><w:body>{bodyInner}</w:body></docmd:body>
              <docmd:styles><w:styles/></docmd:styles>
              <docmd:numbering><w:numbering/></docmd:numbering>
              <docmd:relationships/><docmd:properties/>
            </docmd:package>
            """);

        HeadingAnnotator.Annotate(composite);
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(StylesheetLoader.Read("markdown.xslt"));
        return MarkdownSerializer.Serialize(XDocument.Parse(await transformer.TransformAsync(composite.ToString())));
    }

    [Fact]
    public async Task Table_BecomesAPipeTableWithADelimiterRow()
        => (await ToMarkdownAsync($"""
            <w:tbl>
              <w:tr>{Cell("Item")}{Cell("Status")}</w:tr>
              <w:tr>{Cell("Vent")}{Cell("Pass")}</w:tr>
            </w:tbl>
            """))
            .Should().Be("| Item | Status |\n| --- | --- |\n| Vent | Pass |\n");

    [Fact]
    public async Task HorizontallyMergedCell_KeepsTheRowWidth()
    {
        // GFM has no colspan. A ragged row breaks the entire table for a parser, so the
        // spanned positions are emitted empty rather than omitted.
        var markdown = await ToMarkdownAsync($"""
            <w:tbl>
              <w:tr>{Cell("A")}{Cell("B")}</w:tr>
              <w:tr>{Cell("Wide", """<w:gridSpan w:val="2"/>""")}</w:tr>
            </w:tbl>
            """);

        markdown.Should().Be("| A | B |\n| --- | --- |\n| Wide |  |\n");
    }

    [Fact]
    public async Task VerticallyMergedContinuationCell_IsEmpty()
        => (await ToMarkdownAsync($"""
            <w:tbl>
              <w:tr>{Cell("A")}{Cell("B")}</w:tr>
              <w:tr>{Cell("Cont", """<w:vMerge/>""")}{Cell("C")}</w:tr>
            </w:tbl>
            """))
            .Should().Be("| A | B |\n| --- | --- |\n|  | C |\n");

    [Fact]
    public async Task MultiParagraphCell_IsJoinedOntoOneLine()
    {
        // A newline inside a cell terminates the row.
        var markdown = await ToMarkdownAsync("""
            <w:tbl><w:tr>
              <w:tc><w:p><w:r><w:t>One.</w:t></w:r></w:p><w:p><w:r><w:t>Two.</w:t></w:r></w:p></w:tc>
            </w:tr></w:tbl>
            """);

        markdown.Should().Be("| One. Two. |\n| --- |\n");
    }

    [Fact]
    public async Task NestedTable_IsFlattenedIntoItsContainingCell()
    {
        // GFM cannot express nesting. The words survive for retrieval; the structure
        // does not, and the audit reports it.
        var markdown = await ToMarkdownAsync($"""
            <w:tbl><w:tr>
              <w:tc><w:tbl><w:tr>{Cell("Inner")}</w:tr></w:tbl></w:tc>
              {Cell("Outer")}
            </w:tr></w:tbl>
            """);

        markdown.Should().Be("| Inner | Outer |\n| --- | --- |\n");
    }

    [Fact]
    public async Task CellContent_IsEscapedSoPipesDoNotSplitTheRow()
    {
        // A literal pipe in cell text would otherwise invent a column.
        var markdown = await ToMarkdownAsync($"""<w:tbl><w:tr>{Cell("a|b")}</w:tr></w:tbl>""");

        markdown.Should().Be("| a\\|b |\n| --- |\n");
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Docmd.Word.Tests/Docmd.Word.Tests.csproj --filter "FullyQualifiedName~TableStylesheet"`
Expected: FAIL — tables currently produce nothing or stray paragraphs.

- [ ] **Step 3: Add table handling to the stylesheet**

Add to `markdown.xslt`:
```xml
  <xsl:template match="w:tbl">
    <md:table>
      <xsl:for-each select="w:tr">
        <md:row header="{if (position() eq 1) then 'true' else 'false'}">
          <xsl:for-each select="w:tc">
            <md:cell>
              <!--
                A vMerge continuation carries the visual span, not content. w:val='restart'
                marks the cell that owns it.
              -->
              <xsl:if test="not(w:tcPr/w:vMerge[not(@w:val eq 'restart')])">
                <!--
                  Paragraphs are joined with a space: a newline inside a pipe cell would
                  terminate the row. Nested tables are flattened here for the same reason
                  -- GFM cannot express them, and the words matter more than the shape.
                -->
                <md:text>
                  <xsl:value-of select="normalize-space(string-join(.//w:t[not(ancestor::w:del)], ' '))"/>
                </md:text>
              </xsl:if>
            </md:cell>

            <!--
              gridSpan has no GFM equivalent. Emit the spanned positions as empty cells so
              every row keeps the same width; a ragged row breaks the table entirely.
            -->
            <xsl:for-each select="2 to xs:integer((w:tcPr/w:gridSpan/@w:val, 1)[1])">
              <md:cell/>
            </xsl:for-each>
          </xsl:for-each>
        </md:row>
      </xsl:for-each>
    </md:table>
  </xsl:template>

  <!-- A nested table is consumed by its containing cell's string-join above. -->
  <xsl:template match="w:tbl[ancestor::w:tc]"/>
```

- [ ] **Step 4: Run the tests, then the full suite**

Run: `dotnet test tests/Docmd.Word.Tests/Docmd.Word.Tests.csproj --filter "FullyQualifiedName~TableStylesheet"`
Expected: PASS, 6 tests.

Run: `dotnet test docmd.slnx`
Expected: PASS.

**If the `2 to xs:integer(...)` range expression or nested `for-each-group` misbehaves, follow the engine-defect protocol above** — minimise, reproduce with the `xslt` CLI, and file it in `phoenixmldb-xslt/BUGS.md` rather than working around it silently.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Convert tables, degrading the three cases GFM cannot express

Word's table model is far richer than a pipe table, so the merges have to
lose something. What matters is losing it deliberately.

A gridSpan emits empty cells for the spanned positions rather than
omitting them, because a ragged row does not degrade a GFM table -- it
breaks it outright. A vMerge continuation carries span, not content, so it
emits empty. Nested tables are flattened into their containing cell: the
words survive for retrieval even though the structure cannot.

Cell content is joined onto one line, since a newline inside a pipe cell
terminates the row, and escaped, since a literal pipe in the text would
invent a column.

Each of these is reported by the audit rather than left for a customer to
discover. Silent structure loss is the failure mode; visible, recorded
loss is an acceptable limit of the target format."
```

---

### Task 10: Hyperlinks, images, and the asset sink

**Files:**
- Create: `src/Ooxml.Md.Core/Assets/IAssetSink.cs`, `src/Ooxml.Md.Core/Assets/FileSystemAssetSink.cs`, `src/Ooxml.Md.Core/Assets/AssetRewriter.cs`
- Modify: `src/Docmd.Word/Stylesheets/markdown.xslt`
- Test: `tests/Ooxml.Md.Core.Tests/AssetRewriterTests.cs`, `tests/Docmd.Word.Tests/LinkAndImageStylesheetTests.cs`

**Interfaces:**
- Consumes: the composite's `docmd:relationships` (Task 3), md-XML (Task 5), `OpcPackage` (Task 2).
- Produces:
  - `IAssetSink.WriteAsync(string relativePath, Stream content, string contentType, CancellationToken ct) : Task<Uri>`
  - `FileSystemAssetSink(string outputDirectory, string? baseUrl)`
  - `AssetRewriter.RewriteAsync(XDocument mdXml, OpcPackage package, IAssetSink sink, string documentStem, CancellationToken ct) : Task<IReadOnlyList<AssetIssue>>`
  - `record AssetIssue(string PartName, string Reason)`

**Context:** Spec §10.1. **Where bytes land and what the Markdown says are separate decisions.** The stylesheet emits `md:image` carrying the *package part name*; a C# pass then writes each part through the sink and replaces `src` with the URI the sink returned. That ordering is structural — rewriting URLs in finished Markdown text with regexes fails the moment a filename contains a bracket.

`--asset-base-url` is not a special case: it is the filesystem sink returning a rewritten URI, which is why it costs almost nothing.

**EMF/WMF are passed through and reported** (spec §10.2). No Markdown renderer displays them, so a silent pass-through yields a broken image on every viewer.

- [ ] **Step 1: Write the failing asset tests**

`tests/Ooxml.Md.Core.Tests/AssetRewriterTests.cs`:
```csharp
namespace Ooxml.Md.Core.Tests;

using System.Threading.Tasks;
using System.Xml.Linq;
using FluentAssertions;
using Ooxml.Md.Core.Assets;
using Ooxml.Md.Core.Markdown;
using Ooxml.Md.Core.Opc;
using Xunit;

public sealed class AssetRewriterTests
{
    private static XDocument MdWithImage(string src) => new(
        new XElement(MdNames.Document,
            new XElement(MdNames.Para,
                new XElement(MdNames.Image, new XAttribute("src", src), new XAttribute("alt", "Fig")))));

    private static OpcPackage OpenMinimal()
        => OpcPackage.Open(FixtureZip.FromDirectory(FixtureZip.FixtureRoot("with-image")));

    [Fact]
    public async Task Rewrite_WritesTheAssetAndReplacesTheSource()
    {
        var directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var mdXml = MdWithImage("word/media/image1.png");
            using var package = OpenMinimal();
            var sink = new FileSystemAssetSink(directory, baseUrl: null);

            var issues = await AssetRewriter.RewriteAsync(mdXml, package, sink, "report", default);

            issues.Should().BeEmpty();
            File.Exists(Path.Combine(directory, "img", "report", "image1.png")).Should().BeTrue();
            mdXml.Descendants(MdNames.Image).Single()
                 .Attribute("src")!.Value.Should().Be("img/report/image1.png");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Rewrite_UsesTheBaseUrlWhenGiven()
    {
        var directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var mdXml = MdWithImage("word/media/image1.png");
            using var package = OpenMinimal();
            var sink = new FileSystemAssetSink(directory, baseUrl: "https://cdn.example.com/docs");

            await AssetRewriter.RewriteAsync(mdXml, package, sink, "report", default);

            // Bytes still land locally; only what the Markdown says changed.
            File.Exists(Path.Combine(directory, "img", "report", "image1.png")).Should().BeTrue();
            mdXml.Descendants(MdNames.Image).Single().Attribute("src")!.Value
                 .Should().Be("https://cdn.example.com/docs/img/report/image1.png");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Rewrite_ReportsMetafilesRatherThanDroppingThem()
    {
        var directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var mdXml = MdWithImage("word/media/diagram.emf");
            using var package = OpenMinimal();

            var issues = await AssetRewriter.RewriteAsync(
                mdXml, package, new FileSystemAssetSink(directory, null), "report", default);

            // Passed through, so nothing is lost -- but reported, because no Markdown
            // renderer displays EMF and the user needs to know before publishing.
            issues.Should().ContainSingle(i => i.Reason.Contains("EMF", StringComparison.Ordinal));
            File.Exists(Path.Combine(directory, "img", "report", "diagram.emf")).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Rewrite_ReportsAMissingPartAndLeavesTheDocumentUsable()
    {
        var directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var mdXml = MdWithImage("word/media/absent.png");
            using var package = OpenMinimal();

            var issues = await AssetRewriter.RewriteAsync(
                mdXml, package, new FileSystemAssetSink(directory, null), "report", default);

            issues.Should().ContainSingle(i => i.PartName == "word/media/absent.png");
            // The image element is removed rather than left pointing at nothing.
            mdXml.Descendants(MdNames.Image).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Rewrite_WritesEachPartOnceEvenWhenReferencedRepeatedly()
    {
        var directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var mdXml = new XDocument(
                new XElement(MdNames.Document,
                    new XElement(MdNames.Para, new XElement(MdNames.Image,
                        new XAttribute("src", "word/media/image1.png"), new XAttribute("alt", "A"))),
                    new XElement(MdNames.Para, new XElement(MdNames.Image,
                        new XAttribute("src", "word/media/image1.png"), new XAttribute("alt", "B")))));

            using var package = OpenMinimal();
            await AssetRewriter.RewriteAsync(mdXml, package, new FileSystemAssetSink(directory, null), "report", default);

            mdXml.Descendants(MdNames.Image)
                 .Select(e => e.Attribute("src")!.Value)
                 .Should().AllBe("img/report/image1.png");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
```

Create the fixture `tests/Ooxml.Md.Core.Tests/fixtures/with-image/` by copying `minimal/` and adding two binary-ish parts. Any bytes will do — content is never parsed:
```bash
mkdir -p tests/Ooxml.Md.Core.Tests/fixtures/with-image/word/media
cp -r tests/Ooxml.Md.Core.Tests/fixtures/minimal/. tests/Ooxml.Md.Core.Tests/fixtures/with-image/
printf 'PNG-BYTES' > tests/Ooxml.Md.Core.Tests/fixtures/with-image/word/media/image1.png
printf 'EMF-BYTES' > tests/Ooxml.Md.Core.Tests/fixtures/with-image/word/media/diagram.emf
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Ooxml.Md.Core.Tests/Ooxml.Md.Core.Tests.csproj --filter "FullyQualifiedName~AssetRewriter"`
Expected: FAIL — the asset types do not exist.

- [ ] **Step 3: Implement the sink and rewriter**

`src/Ooxml.Md.Core/Assets/IAssetSink.cs`:
```csharp
namespace Ooxml.Md.Core.Assets;

/// <summary>
/// Where extracted assets are written, and what URI the Markdown should reference.
/// </summary>
/// <remarks>
/// The seam that separates "where the bytes land" from "what the Markdown says". Cloud
/// sinks (Pixault, blob storage) implement this and ship as separate packages so their
/// SDKs never burden the base tool. See spec §10.1.
/// </remarks>
public interface IAssetSink
{
    /// <summary>Writes one asset and returns the URI to reference it by.</summary>
    Task<Uri> WriteAsync(string relativePath, Stream content, string contentType, CancellationToken ct);
}
```

`src/Ooxml.Md.Core/Assets/FileSystemAssetSink.cs`:
```csharp
namespace Ooxml.Md.Core.Assets;

/// <summary>Writes assets beside the Markdown. The default, and the whole free tier.</summary>
/// <param name="baseUrl">
/// When set, the returned URI is this prefix plus the relative path, while the bytes still
/// land locally. That is the entirety of --asset-base-url: the customer's existing sync
/// step moves the files, and docmd only has to say the right thing.
/// </param>
public sealed class FileSystemAssetSink(string outputDirectory, string? baseUrl) : IAssetSink
{
    public async Task<Uri> WriteAsync(string relativePath, Stream content, string contentType, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(relativePath);
        ArgumentNullException.ThrowIfNull(content);

        var destination = Path.Combine(outputDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        await using (var file = File.Create(destination))
        {
            await content.CopyToAsync(file, ct).ConfigureAwait(false);
        }

        return string.IsNullOrEmpty(baseUrl)
            ? new Uri(relativePath, UriKind.Relative)
            : new Uri($"{baseUrl.TrimEnd('/')}/{relativePath}", UriKind.Absolute);
    }
}
```

`src/Ooxml.Md.Core/Assets/AssetRewriter.cs`:
```csharp
namespace Ooxml.Md.Core.Assets;

using System.Xml.Linq;
using Ooxml.Md.Core.Markdown;
using Ooxml.Md.Core.Opc;

/// <summary>Something worth telling the user about an asset. Never fatal.</summary>
public sealed record AssetIssue(string PartName, string Reason);

/// <summary>
/// Emit phase 5a: writes every referenced asset through the sink, then replaces each
/// md:image/@src with the URI the sink returned.
/// </summary>
/// <remarks>
/// The ordering is structural, not stylistic. Rewriting URLs inside finished Markdown text
/// would mean pattern-matching link syntax, which breaks the first time a filename contains
/// a bracket. Doing it on the tree, before serialisation, cannot break that way.
/// </remarks>
public static class AssetRewriter
{
    private static readonly string[] UnrenderableExtensions = [".emf", ".wmf"];

    public static async Task<IReadOnlyList<AssetIssue>> RewriteAsync(
        XDocument mdXml, OpcPackage package, IAssetSink sink, string documentStem, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mdXml);
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(sink);

        var issues = new List<AssetIssue>();
        // Each part is written once however often it is referenced.
        var written = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var image in mdXml.Descendants(MdNames.Image).ToArray())
        {
            ct.ThrowIfCancellationRequested();
            var partName = (string?)image.Attribute("src") ?? "";

            if (written.TryGetValue(partName, out var existing))
            {
                image.SetAttributeValue("src", existing);
                continue;
            }

            if (!package.ContainsPart(partName))
            {
                // Remove rather than leave a link pointing at nothing: a broken image in
                // a RAG corpus is noise, and in a rendered document it is an error icon.
                issues.Add(new AssetIssue(partName, "Referenced image part is missing from the package."));
                image.Remove();
                continue;
            }

            var fileName = partName[(partName.LastIndexOf('/') + 1)..];
            var extension = Path.GetExtension(fileName).ToLowerInvariant();
            if (Array.IndexOf(UnrenderableExtensions, extension) >= 0)
            {
                // Passed through so nothing is lost, but reported: no Markdown renderer
                // displays EMF/WMF, and the user needs to know before publishing.
                issues.Add(new AssetIssue(partName,
                    $"Image is {extension.TrimStart('.').ToUpperInvariant()}, which Markdown renderers do not display."));
            }

            var relativePath = $"img/{documentStem}/{fileName}";
            await using (var content = package.OpenPartStream(partName))
            {
                var uri = await sink.WriteAsync(relativePath, content, ContentTypeFor(extension), ct).ConfigureAwait(false);
                var reference = uri.IsAbsoluteUri ? uri.AbsoluteUri : uri.OriginalString;
                written[partName] = reference;
                image.SetAttributeValue("src", reference);
            }
        }

        return issues;
    }

    private static string ContentTypeFor(string extension) => extension switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        ".bmp" => "image/bmp",
        ".tif" or ".tiff" => "image/tiff",
        ".emf" => "image/x-emf",
        ".wmf" => "image/x-wmf",
        _ => "application/octet-stream",
    };
}
```

- [ ] **Step 4: Add links and images to the stylesheet**

Add the DrawingML namespaces to the `xsl:stylesheet` element:
```
    xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"
    xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
    xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture"
    xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"
```
and add `wp a pic r` to `exclude-result-prefixes`. Then add:

```xml
  <!--
    Relationship targets were resolved during assembly, so this is a lookup, never path
    arithmetic.
  -->
  <xsl:key name="rel" match="docmd:relationship" use="@id"/>

  <xsl:function name="docmd:rel-target" as="xs:string">
    <xsl:param name="package" as="document-node()"/>
    <xsl:param name="id" as="xs:string?"/>
    <xsl:sequence select="string(key('rel', $id, $package)/@target)"/>
  </xsl:function>

  <xsl:template match="w:hyperlink" mode="inline">
    <xsl:variable name="href" select="docmd:rel-target(root(.), @r:id)"/>
    <xsl:choose>
      <xsl:when test="$href ne ''">
        <md:link href="{$href}">
          <xsl:apply-templates select="w:r" mode="inline"/>
        </md:link>
      </xsl:when>
      <!-- A dangling r:id degrades to plain text: the words matter, the link does not. -->
      <xsl:otherwise>
        <xsl:apply-templates select="w:r" mode="inline"/>
      </xsl:otherwise>
    </xsl:choose>
  </xsl:template>

  <!--
    src carries the PART NAME. AssetRewriter replaces it with the sink's URI before
    serialisation, so the stylesheet stays ignorant of where assets go.
    Alt text is the only thing an image contributes to a text index, so both descr and
    title are tried.
  -->
  <xsl:template match="w:drawing" mode="inline">
    <xsl:variable name="embed" select="(.//a:blip/@r:embed)[1]"/>
    <xsl:variable name="target" select="docmd:rel-target(root(.), $embed)"/>
    <xsl:if test="$target ne ''">
      <md:image src="{$target}"
                alt="{((.//wp:docPr/@descr)[1], (.//wp:docPr/@title)[1], '')[1]}"/>
    </xsl:if>
  </xsl:template>
```

Then make drawings reachable. Inside the `w:r` inline template from Task 7, add this as the
last statement of the `xsl:if` body, after the `w:br` loop:
```xml
      <xsl:apply-templates select="w:drawing" mode="inline"/>
```
and widen that template's guard so a run holding only a drawing is not skipped — change
`<xsl:if test="$text ne '' or w:br">` to:
```xml
    <xsl:if test="$text ne '' or w:br or w:drawing">
```

The empty-paragraph suppression from Task 7 already excludes `.//w:drawing`, so a paragraph
containing nothing but an image is retained without further change.

- [ ] **Step 5: Write and run the stylesheet link/image tests**

`tests/Docmd.Word.Tests/LinkAndImageStylesheetTests.cs` — same harness as Task 9, with `docmd:relationships` populated:
```csharp
    private const string Relationships = """
        <docmd:relationship id="rId8" type="hyperlink" target="https://example.com/spec" external="true"/>
        <docmd:relationship id="rId7" type="image" target="word/media/image1.png" external="false"/>
        """;
```

Tests to write (assertions shown; build them on the Task 9 harness with the relationships block inserted):
```csharp
    [Fact]
    public async Task Hyperlink_BecomesALink()
        => (await ToMarkdownAsync("""
            <w:p><w:hyperlink r:id="rId8"><w:r><w:t>the spec</w:t></w:r></w:hyperlink></w:p>
            """))
            .Should().Be("[the spec](https://example.com/spec)\n");

    [Fact]
    public async Task DanglingHyperlink_DegradesToPlainText()
        => (await ToMarkdownAsync("""
            <w:p><w:hyperlink r:id="rId99"><w:r><w:t>orphan</w:t></w:r></w:hyperlink></w:p>
            """))
            .Should().Be("orphan\n");

    [Fact]
    public async Task Drawing_BecomesAnImageCarryingThePartName()
    {
        // src is the part name here; AssetRewriter replaces it with the sink's URI.
        var mdXml = await TransformAsync("""
            <w:p><w:r><w:drawing><wp:inline>
              <wp:docPr id="1" name="Picture 1" descr="Vent assembly"/>
              <a:graphic><a:graphicData><pic:pic><pic:blipFill>
                <a:blip r:embed="rId7"/></pic:blipFill></pic:pic></a:graphicData></a:graphic>
            </wp:inline></w:drawing></w:r></w:p>
            """);

        var image = mdXml.Descendants(Ooxml.Md.Core.Markdown.MdNames.Image).Single();
        image.Attribute("src")!.Value.Should().Be("word/media/image1.png");
        image.Attribute("alt")!.Value.Should().Be("Vent assembly");
    }
```
The test harness must declare `wp`, `a`, `pic` and `r` namespaces on the composite root for these fixtures to parse.

Run: `dotnet test docmd.slnx`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Extract assets through a sink, and keep URLs off the text

Where bytes land and what the Markdown says are separate decisions. The
stylesheet emits an image carrying the package part name; a pass over the
tree writes each part through the sink and swaps in the URI it returned.

That ordering is structural rather than tidy. Rewriting URLs inside
finished Markdown means pattern-matching link syntax, which breaks the
first time a filename contains a bracket -- and filenames from Word
routinely do.

--asset-base-url is not a special case, just the filesystem sink
returning a rewritten URI while the bytes still land locally, so the
customer's existing sync step moves them. That is why it costs almost
nothing and stays free.

EMF and WMF are passed through and reported: no Markdown renderer
displays them, so silence would produce a broken image on every viewer. A
missing part removes the image entirely rather than leaving a link
pointing at nothing.

Each part is written once however often the document references it."
```

---
### Task 11: YAML frontmatter

**Files:**
- Create: `src/Ooxml.Md.Core/Frontmatter/DocumentProperties.cs`, `src/Ooxml.Md.Core/Frontmatter/FrontmatterWriter.cs`, `src/Docmd.Word/Assembly/WordPropertiesReader.cs`
- Test: `tests/Ooxml.Md.Core.Tests/FrontmatterWriterTests.cs`, `tests/Docmd.Word.Tests/WordPropertiesReaderTests.cs`

**Interfaces:**
- Consumes: the composite's `docmd:properties` (Task 3).
- Produces:
  - `record DocumentProperties { string? Title; string? Author; string? Created; string? Modified; string? Revision; string? Company; required string Source; required string Sha256; }`
  - `WordPropertiesReader.Read(XDocument composite, string sourceFileName, string sha256) : DocumentProperties`
  - `FrontmatterWriter.Write(DocumentProperties properties) : string` — includes the `---` fences and a trailing blank line

**Context:** Spec §6. Properties come from `docProps/core.xml` (`dc:title`, `dc:creator`, `dcterms:created`, `dcterms:modified`, `cp:revision`) and `docProps/app.xml` (`Company`). docmd adds `source` and `sha256`, so a chunk retrieved months later traces to an exact document version.

**No conversion timestamp** (spec §5). It would change every file's hash on every run, forcing a whole corpus to re-embed for no content change. Absent fields are omitted entirely rather than emitted empty — a null `author:` is noise in an index.

- [ ] **Step 1: Write the failing tests**

`tests/Ooxml.Md.Core.Tests/FrontmatterWriterTests.cs`:
```csharp
namespace Ooxml.Md.Core.Tests;

using FluentAssertions;
using Ooxml.Md.Core.Frontmatter;
using Xunit;

public sealed class FrontmatterWriterTests
{
    private static DocumentProperties Minimal => new() { Source = "report.docx", Sha256 = "9f2c1a" };

    [Fact]
    public void Write_EmitsFencesAndTheRequiredFields()
        => FrontmatterWriter.Write(Minimal)
            .Should().Be("---\nsource: report.docx\nsha256: 9f2c1a\n---\n\n");

    [Fact]
    public void Write_OmitsAbsentFieldsEntirely()
    {
        // "author:" with no value is noise in an index and a filterable field that
        // matches nothing.
        var yaml = FrontmatterWriter.Write(Minimal);

        yaml.Should().NotContain("author");
        yaml.Should().NotContain("title");
    }

    [Fact]
    public void Write_IncludesEveryFieldWhenPresent()
    {
        var yaml = FrontmatterWriter.Write(new DocumentProperties
        {
            Title = "Q3 Safety Review",
            Author = "A. Whitfield",
            Created = "2026-04-11",
            Modified = "2026-04-18",
            Revision = "7",
            Company = "Endpoint Systems",
            Source = "report.docx",
            Sha256 = "9f2c1a",
        });

        yaml.Should().Contain("title: Q3 Safety Review");
        yaml.Should().Contain("author: A. Whitfield");
        yaml.Should().Contain("created: '2026-04-11'");
        yaml.Should().Contain("company: Endpoint Systems");
    }

    [Fact]
    public void Write_QuotesValuesThatWouldOtherwiseChangeMeaning()
    {
        // A title containing a colon splits into a nested mapping unless quoted, silently
        // corrupting the document's metadata.
        var yaml = FrontmatterWriter.Write(Minimal with { Title = "Report: Phase 2" });

        yaml.Should().Contain("'Report: Phase 2'");
    }

    [Fact]
    public void Write_ContainsNoTimestamp()
    {
        // Determinism (spec §5). A converted-at field changes every file's hash on every
        // run and forces a whole corpus to re-embed for no content change.
        var yaml = FrontmatterWriter.Write(Minimal);

        yaml.Should().NotContain("converted");
        FrontmatterWriter.Write(Minimal).Should().Be(yaml);
    }
}
```

- [ ] **Step 2: Run to verify failure, then implement**

Run: `dotnet test tests/Ooxml.Md.Core.Tests/Ooxml.Md.Core.Tests.csproj --filter "FullyQualifiedName~Frontmatter"`
Expected: FAIL.

`src/Ooxml.Md.Core/Frontmatter/DocumentProperties.cs`:
```csharp
namespace Ooxml.Md.Core.Frontmatter;

/// <summary>
/// Metadata emitted as YAML frontmatter. Dates are strings already in ISO-8601 form: they
/// are passed through from the document rather than reformatted, so no CultureInfo can
/// influence the output.
/// </summary>
public sealed record DocumentProperties
{
    public string? Title { get; init; }
    public string? Author { get; init; }
    public string? Created { get; init; }
    public string? Modified { get; init; }
    public string? Revision { get; init; }
    public string? Company { get; init; }

    /// <summary>The original filename, so a retrieved chunk names its source.</summary>
    public required string Source { get; init; }

    /// <summary>SHA-256 of the source file, so it names an exact version.</summary>
    public required string Sha256 { get; init; }
}
```

`src/Ooxml.Md.Core/Frontmatter/FrontmatterWriter.cs`:
```csharp
namespace Ooxml.Md.Core.Frontmatter;

using System.Text;
using YamlDotNet.Serialization;

/// <summary>Writes the YAML frontmatter block, fences included.</summary>
/// <remarks>
/// Field order is fixed rather than reflective, because determinism requires it and
/// because a stable order makes the frontmatter diffable across releases.
/// </remarks>
public static class FrontmatterWriter
{
    public static string Write(DocumentProperties properties)
    {
        ArgumentNullException.ThrowIfNull(properties);

        var builder = new StringBuilder("---\n");
        Append(builder, "title", properties.Title);
        Append(builder, "author", properties.Author);
        Append(builder, "created", properties.Created);
        Append(builder, "modified", properties.Modified);
        Append(builder, "revision", properties.Revision);
        Append(builder, "company", properties.Company);
        Append(builder, "source", properties.Source);
        Append(builder, "sha256", properties.Sha256);
        builder.Append("---\n\n");
        return builder.ToString();
    }

    private static void Append(StringBuilder builder, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            // Omit rather than emit empty: a null field is noise in an index and matches
            // nothing when filtered on.
            return;
        }

        builder.Append(key).Append(": ").Append(Quote(value)).Append('\n');
    }

    /// <summary>
    /// Quotes a scalar when leaving it bare would change its meaning — a colon makes a
    /// nested mapping, a leading '#' a comment, a bare date a typed timestamp.
    /// </summary>
    private static string Quote(string value)
    {
        var needsQuoting =
            value.Contains(": ", StringComparison.Ordinal) ||
            value.EndsWith(':') ||
            value.StartsWith('#') || value.StartsWith('&') || value.StartsWith('*') ||
            value.StartsWith('[') || value.StartsWith('{') || value.StartsWith('-') ||
            value.StartsWith(' ') || value.EndsWith(' ') ||
            (value.Length > 0 && char.IsAsciiDigit(value[0]));

        return needsQuoting ? $"'{value.Replace("'", "''", StringComparison.Ordinal)}'" : value;
    }
}
```

Note: `YamlDotNet` is referenced for future style-map parsing (Plan 3); the writer is hand-rolled because a serialiser's field ordering and quoting rules are exactly what determinism needs pinned. Keep the package reference.

- [ ] **Step 3: Implement and test the properties reader**

`src/Docmd.Word/Assembly/WordPropertiesReader.cs`:
```csharp
namespace Docmd.Word.Assembly;

using System.Xml.Linq;
using Ooxml.Md.Core.Frontmatter;

/// <summary>Reads docProps out of the composite into the frontmatter model.</summary>
public static class WordPropertiesReader
{
    private static readonly XNamespace Cp = "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace DcTerms = "http://purl.org/dc/terms/";
    private static readonly XNamespace Ep = "http://schemas.openxmlformats.org/officeDocument/2006/extended-properties";

    public static DocumentProperties Read(XDocument composite, string sourceFileName, string sha256)
    {
        ArgumentNullException.ThrowIfNull(composite);

        var properties = composite.Root?.Element(WordNames.Docmd + "properties");
        var core = properties?.Element(WordNames.Docmd + "core")?.Element(Cp + "coreProperties");
        var app = properties?.Element(WordNames.Docmd + "app")?.Element(Ep + "Properties");

        return new DocumentProperties
        {
            Title = Trim(core?.Element(Dc + "title")?.Value),
            Author = Trim(core?.Element(Dc + "creator")?.Value),
            // Passed through verbatim: they are already ISO-8601 in the XML, and
            // reformatting would introduce a culture dependency for no gain.
            Created = Trim(core?.Element(DcTerms + "created")?.Value),
            Modified = Trim(core?.Element(DcTerms + "modified")?.Value),
            Revision = Trim(core?.Element(Cp + "revision")?.Value),
            Company = Trim(app?.Element(Ep + "Company")?.Value),
            Source = sourceFileName,
            Sha256 = sha256,
        };
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
```

`tests/Docmd.Word.Tests/WordPropertiesReaderTests.cs` — assert that the `composite-basic` fixture yields `Title == "Q3 Safety Review"`, `Author == "A. Whitfield"`, `Created == "2026-04-11T09:14:00Z"`, and that a composite with an empty `docmd:properties` yields all-null optional fields with `Source` and `Sha256` still set.

- [ ] **Step 4: Run all tests, then commit**

Run: `dotnet test docmd.slnx`
Expected: PASS.

```bash
git add -A
git commit -m "Emit YAML frontmatter, with provenance and without a clock

Properties come from docProps; docmd adds the source filename and a
SHA-256 of the source file, so a chunk retrieved months later names not
just a document but an exact version of it.

There is deliberately no converted-at timestamp. It would change every
file's hash on every run and force a whole corpus to re-embed for no
content change -- a real bill on a few thousand documents, and a pointless
one.

The writer is hand-rolled rather than reflective. Field order and quoting
rules are precisely what determinism needs pinned, and a serialiser's
defaults are free to change under us. Dates pass through verbatim because
they are already ISO-8601, so no culture can reach them.

Absent fields are omitted rather than emitted empty: a null author is
noise in an index and a filter that matches nothing."
```

---

### Task 12: The conversion pipeline, end to end

**Files:**
- Create: `src/Docmd.Word/ConversionOptions.cs`, `src/Docmd.Word/ConversionResult.cs`, `src/Docmd.Word/DocumentConverter.cs`
- Test: `tests/Docmd.Word.Tests/DocumentConverterTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 2–11.
- Produces:
  - `record ConversionOptions { string OutputDirectory; string? AssetBaseUrl; bool IncludeImages = true; bool IncludeFrontmatter = true; MarkdownFlavour Flavour; string ImageDirectoryName = "img"; }`
  - `record ConversionResult(string Markdown, DocumentProperties Properties, IReadOnlyList<AssetIssue> AssetIssues)`
  - `DocumentConverter.ConvertAsync(string inputPath, ConversionOptions options, CancellationToken ct) : Task<ConversionResult>`
  - `DocumentConverter.WriteAsync(string inputPath, ConversionOptions options, CancellationToken ct) : Task<ConversionResult>` — also writes the `.md`

**Context:** This wires stages 1–5 together and is the API the CLI calls. It is also where the SHA-256 is computed and where the document stem is derived.

- [ ] **Step 1: Write the failing tests**

`tests/Docmd.Word.Tests/DocumentConverterTests.cs`:
```csharp
namespace Docmd.Word.Tests;

using System.Threading.Tasks;
using Docmd.Word;
using FluentAssertions;
using Ooxml.Md.Core.Opc;
using Xunit;

public sealed class DocumentConverterTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory().FullName;

    /// <summary>Materialises a fixture directory as a real .docx on disk.</summary>
    private string StageDocx(string fixture, string name)
    {
        var path = Path.Combine(_workspace, name);
        using var zip = FixtureZip.FromDirectory(FixtureZip.FixtureRoot(fixture));
        using var file = File.Create(path);
        zip.CopyTo(file);
        return path;
    }

    private ConversionOptions Options => new() { OutputDirectory = _workspace };

    [Fact]
    public async Task Convert_ProducesFrontmatterFollowedByBody()
    {
        var result = await DocumentConverter.ConvertAsync(StageDocx("composite-basic", "report.docx"), Options, default);

        result.Markdown.Should().StartWith("---\n");
        result.Markdown.Should().Contain("title: Q3 Safety Review");
        result.Markdown.Should().Contain("Hello");
    }

    [Fact]
    public async Task Convert_ComputesTheSourceHash()
    {
        var result = await DocumentConverter.ConvertAsync(StageDocx("composite-basic", "report.docx"), Options, default);

        result.Properties.Sha256.Should().MatchRegex("^[0-9a-f]{64}$");
        result.Properties.Source.Should().Be("report.docx");
    }

    [Fact]
    public async Task Convert_OmitsFrontmatterWhenAsked()
    {
        var options = Options with { IncludeFrontmatter = false };

        var result = await DocumentConverter.ConvertAsync(StageDocx("composite-basic", "report.docx"), options, default);

        result.Markdown.Should().NotStartWith("---");
    }

    [Fact]
    public async Task Write_PutsTheMarkdownBesideTheOutputDirectory()
    {
        await DocumentConverter.WriteAsync(StageDocx("composite-basic", "report.docx"), Options, default);

        File.Exists(Path.Combine(_workspace, "report.md")).Should().BeTrue();
    }

    [Fact]
    public async Task Convert_RejectsSomethingThatIsNotAnOoxmlPackage()
    {
        var path = Path.Combine(_workspace, "legacy.doc");
        await File.WriteAllTextAsync(path, "not a zip");

        var act = async () => await DocumentConverter.ConvertAsync(path, Options, default);

        await act.Should().ThrowAsync<OpcFormatException>();
    }

    public void Dispose() => Directory.Delete(_workspace, recursive: true);
}
```

- [ ] **Step 2: Run to verify failure, then implement**

Run: `dotnet test tests/Docmd.Word.Tests/Docmd.Word.Tests.csproj --filter "FullyQualifiedName~DocumentConverter"`
Expected: FAIL.

`src/Docmd.Word/ConversionOptions.cs`:
```csharp
namespace Docmd.Word;

using Ooxml.Md.Core.Markdown;

public sealed record ConversionOptions
{
    public required string OutputDirectory { get; init; }
    public string? AssetBaseUrl { get; init; }
    public bool IncludeImages { get; init; } = true;
    public bool IncludeFrontmatter { get; init; } = true;
    public MarkdownFlavour Flavour { get; init; } = MarkdownFlavour.Gfm;
    public string ImageDirectoryName { get; init; } = "img";
}
```

`src/Docmd.Word/ConversionResult.cs`:
```csharp
namespace Docmd.Word;

using Ooxml.Md.Core.Assets;
using Ooxml.Md.Core.Frontmatter;

/// <param name="AssetIssues">
/// Non-fatal problems worth telling the user about. Plan 3's audit aggregates these.
/// </param>
public sealed record ConversionResult(
    string Markdown,
    DocumentProperties Properties,
    IReadOnlyList<AssetIssue> AssetIssues);
```

`src/Docmd.Word/DocumentConverter.cs`:
```csharp
namespace Docmd.Word;

using System.Security.Cryptography;
using System.Xml.Linq;
using Docmd.Word.Assembly;
using Ooxml.Md.Core.Assets;
using Ooxml.Md.Core.Frontmatter;
using Ooxml.Md.Core.Markdown;
using Ooxml.Md.Core.Opc;
using PhoenixmlDb.Xslt;

/// <summary>Runs the five pipeline stages over one document.</summary>
public static class DocumentConverter
{
    public static async Task<ConversionResult> ConvertAsync(
        string inputPath, ConversionOptions options, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(inputPath);
        ArgumentNullException.ThrowIfNull(options);

        var sha256 = await ComputeSha256Async(inputPath, ct).ConfigureAwait(false);
        var stem = Path.GetFileNameWithoutExtension(inputPath);

        // 1 + 2: open and compose.
        using var package = OpcPackage.OpenFile(inputPath);
        var composite = WordCompositeBuilder.Build(package);

        // 3: annotate.
        HeadingAnnotator.Annotate(composite);

        // 4: transform.
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(StylesheetLoader.Read("markdown.xslt")).ConfigureAwait(false);
        var mdXml = XDocument.Parse(await transformer.TransformAsync(composite.ToString(), ct).ConfigureAwait(false));

        // 5a: assets first, so 5b can reference what the sink returned.
        IReadOnlyList<AssetIssue> issues = [];
        if (options.IncludeImages)
        {
            var sink = new FileSystemAssetSink(options.OutputDirectory, options.AssetBaseUrl);
            issues = await AssetRewriter.RewriteAsync(mdXml, package, sink, stem, ct).ConfigureAwait(false);
        }
        else
        {
            foreach (var image in mdXml.Descendants(MdNames.Image).ToArray())
            {
                image.Remove();
            }
        }

        // 5b: serialise.
        var body = MarkdownSerializer.Serialize(mdXml, new MarkdownOptions { Flavour = options.Flavour });
        var properties = WordPropertiesReader.Read(composite, Path.GetFileName(inputPath), sha256);
        var markdown = options.IncludeFrontmatter
            ? FrontmatterWriter.Write(properties) + body
            : body;

        return new ConversionResult(markdown, properties, issues);
    }

    public static async Task<ConversionResult> WriteAsync(
        string inputPath, ConversionOptions options, CancellationToken ct)
    {
        var result = await ConvertAsync(inputPath, options, ct).ConfigureAwait(false);

        Directory.CreateDirectory(options.OutputDirectory);
        var destination = Path.Combine(
            options.OutputDirectory,
            Path.GetFileNameWithoutExtension(inputPath) + ".md");

        // UTF-8 without a BOM: a BOM is invisible, breaks byte-comparison expectations,
        // and confuses some downstream tooling.
        await File.WriteAllTextAsync(destination, result.Markdown, new System.Text.UTF8Encoding(false), ct)
                  .ConfigureAwait(false);
        return result;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }
}
```

- [ ] **Step 3: Run all tests and commit**

Run: `dotnet test docmd.slnx`
Expected: PASS.

```bash
git add -A
git commit -m "Wire the five stages into one conversion

Assets are written before the Markdown is serialised, not after, because
the serialiser needs the URIs the sink returned. That is the ordering the
asset seam exists to make natural.

Output is UTF-8 with no BOM. A BOM is invisible, defeats byte-comparison,
and confuses downstream tooling that reads the first characters of a file.

The source hash is over the .docx itself rather than the Markdown, so
frontmatter identifies which document version produced a chunk even after
the converter's own output has changed between releases."
```

---

### Task 13: The command-line tool

**Files:**
- Create: `src/Ooxml.Md.Core/Licensing/ILicenseGate.cs`, `src/Ooxml.Md.Core/Licensing/PermissiveLicenseGate.cs`, `src/Docmd.Cli/CommandLine.cs`
- Modify: `src/Docmd.Cli/Program.cs`
- Test: `tests/Docmd.Word.Tests/CommandLineTests.cs`

**Interfaces:**
- Consumes: `DocumentConverter` (Task 12).
- Produces:
  - `interface ILicenseGate { LicenseStatus Check(); }`, `record LicenseStatus(bool Allowed, string? Notice)`
  - `CommandLine.Parse(string[] args) : ParseResult`
  - `record ParseResult(CommandKind Command, string? Input, ConversionOptions? Options, bool Review, string? Error)`
  - `enum CommandKind { Convert, Audit, Register, License, Help, Version }`

**Context:** Spec §12. Exit codes: `0` success, `1` internal error, `2` bad input, `3` not registered, `4` degradations under `--strict`, `5` paid feature without entitlement.

**Argument parsing is hand-rolled.** `System.CommandLine` is still shifting its API surface across previews, and docmd is a commercial product where a churning dependency is a liability. Fifteen flags do not justify it.

**The licence gate is a seam with a permissive implementation.** Registration enforcement is Plan 4's, blocked on the licensing workstream. What matters now is that `Program` calls the gate and honours its answer, so replacing the implementation is a one-line change and never touches the converter.

- [ ] **Step 1: Write the licence seam**

`src/Ooxml.Md.Core/Licensing/ILicenseGate.cs`:
```csharp
namespace Ooxml.Md.Core.Licensing;

/// <param name="Allowed">False stops the run with exit code 3.</param>
/// <param name="Notice">Printed to stderr whether or not the run proceeds.</param>
public sealed record LicenseStatus(bool Allowed, string? Notice);

/// <summary>
/// The single point at which licensing is consulted. Called once, before any conversion.
/// </summary>
/// <remarks>
/// Deliberately one method on one interface. The real verifier belongs to a standalone
/// licensing project owned by another workstream (spec §11); isolating it here means
/// adopting that project is a registration change rather than a rewrite, and no converter
/// code ever references licensing.
/// </remarks>
public interface ILicenseGate
{
    LicenseStatus Check();
}
```

`src/Ooxml.Md.Core/Licensing/PermissiveLicenseGate.cs`:
```csharp
namespace Ooxml.Md.Core.Licensing;

/// <summary>
/// Allows every run. TEMPORARY — replaced in Plan 4 by the real verifier.
/// </summary>
/// <remarks>
/// This exists so the CLI's gate call site is real and tested before the verifier exists.
/// It must not survive into a released build: spec §3 decision 4 requires registration to
/// run. The release checklist gates on this type being gone.
/// </remarks>
public sealed class PermissiveLicenseGate : ILicenseGate
{
    public LicenseStatus Check() => new(Allowed: true, Notice: null);
}
```

- [ ] **Step 2: Write the failing parser tests**

`tests/Docmd.Word.Tests/CommandLineTests.cs`:
```csharp
namespace Docmd.Word.Tests;

using Docmd.Cli;
using FluentAssertions;
using Ooxml.Md.Core.Markdown;
using Xunit;

public sealed class CommandLineTests
{
    [Fact]
    public void Parse_DefaultsToConvertWithTheInputPath()
    {
        var result = CommandLine.Parse(["report.docx"]);

        result.Error.Should().BeNull();
        result.Command.Should().Be(CommandKind.Convert);
        result.Input.Should().Be("report.docx");
        result.Options!.IncludeImages.Should().BeTrue();
        result.Options.Flavour.Should().Be(MarkdownFlavour.Gfm);
    }

    [Fact]
    public void Parse_ReadsOutputAndAssetFlags()
    {
        var result = CommandLine.Parse(
            ["specs/", "-o", "out/", "--asset-base-url", "https://cdn.example.com/docs", "--no-images"]);

        result.Options!.OutputDirectory.Should().Be("out/");
        result.Options.AssetBaseUrl.Should().Be("https://cdn.example.com/docs");
        result.Options.IncludeImages.Should().BeFalse();
    }

    [Fact]
    public void Parse_RecognisesSubcommands()
    {
        CommandLine.Parse(["audit", "specs/"]).Command.Should().Be(CommandKind.Audit);
        CommandLine.Parse(["register", "--email", "a@b.com"]).Command.Should().Be(CommandKind.Register);
        CommandLine.Parse(["license"]).Command.Should().Be(CommandKind.License);
        CommandLine.Parse(["--version"]).Command.Should().Be(CommandKind.Version);
        CommandLine.Parse([]).Command.Should().Be(CommandKind.Help);
    }

    [Fact]
    public void Parse_RejectsAnUnknownFlag()
        => CommandLine.Parse(["report.docx", "--nope"]).Error.Should().Contain("--nope");

    [Fact]
    public void Parse_RejectsAFlagMissingItsValue()
        => CommandLine.Parse(["report.docx", "-o"]).Error.Should().Contain("-o");

    [Fact]
    public void Parse_RejectsAnUnknownFlavour()
        => CommandLine.Parse(["report.docx", "--flavour", "textile"]).Error.Should().Contain("textile");
}
```

- [ ] **Step 3: Run to verify failure, then implement the parser**

Run: `dotnet test tests/Docmd.Word.Tests/Docmd.Word.Tests.csproj --filter "FullyQualifiedName~CommandLine"`
Expected: FAIL. Add a `ProjectReference` to `Docmd.Cli` from `Docmd.Word.Tests` and set `<InternalsVisibleTo>` — or simply make `CommandLine` and `ParseResult` public; public is fine here, the CLI is not a library API anyone else consumes.

`src/Docmd.Cli/CommandLine.cs`:
```csharp
namespace Docmd.Cli;

using Docmd.Word;
using Ooxml.Md.Core.Markdown;

public enum CommandKind { Convert, Audit, Register, License, Help, Version }

public sealed record ParseResult(
    CommandKind Command,
    string? Input,
    ConversionOptions? Options,
    bool Review,
    string? Error);

/// <summary>
/// Hand-rolled argument parsing.
/// </summary>
/// <remarks>
/// System.CommandLine's API has moved repeatedly across previews, and a churning
/// dependency is a liability in a commercial product. Fifteen flags do not justify it.
/// </remarks>
public static class CommandLine
{
    public static ParseResult Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0)
        {
            return new ParseResult(CommandKind.Help, null, null, false, null);
        }

        switch (args[0])
        {
            case "--version" or "-V":
                return new ParseResult(CommandKind.Version, null, null, false, null);
            case "--help" or "-h":
                return new ParseResult(CommandKind.Help, null, null, false, null);
            case "audit":
                return new ParseResult(CommandKind.Audit, args.ElementAtOrDefault(1), null, false, null);
            case "register":
                return new ParseResult(CommandKind.Register, null, null, false, null);
            case "license":
                return new ParseResult(CommandKind.License, null, null, false, null);
            default:
                break;
        }

        string? input = null;
        var output = ".";
        string? assetBaseUrl = null;
        var includeImages = true;
        var includeFrontmatter = true;
        var flavour = MarkdownFlavour.Gfm;
        var imageDirectory = "img";
        var review = false;

        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];

            if (!argument.StartsWith('-'))
            {
                input ??= argument;
                continue;
            }

            switch (argument)
            {
                case "--no-images":
                    includeImages = false;
                    break;
                case "--review":
                    review = true;
                    break;
                case "-o" or "--output":
                    if (!TryTake(args, ref i, out output!))
                    {
                        return Fail($"{argument} requires a value.");
                    }

                    break;
                case "--asset-base-url":
                    if (!TryTake(args, ref i, out assetBaseUrl!))
                    {
                        return Fail($"{argument} requires a value.");
                    }

                    break;
                case "--img-dir":
                    if (!TryTake(args, ref i, out imageDirectory!))
                    {
                        return Fail($"{argument} requires a value.");
                    }

                    break;
                case "--front-matter":
                    if (!TryTake(args, ref i, out var frontMatter))
                    {
                        return Fail($"{argument} requires a value.");
                    }

                    switch (frontMatter)
                    {
                        case "yaml":
                            includeFrontmatter = true;
                            break;
                        case "none":
                            includeFrontmatter = false;
                            break;
                        default:
                            // A bad argument is a diagnostic, never an exception: the CLI
                            // must exit 2 with a message, not a stack trace.
                            return Fail($"Unknown front-matter mode '{frontMatter}'. Use yaml or none.");
                    }

                    break;
                case "--flavour":
                    if (!TryTake(args, ref i, out var flavourText))
                    {
                        return Fail($"{argument} requires a value.");
                    }

                    if (!Enum.TryParse(flavourText, ignoreCase: true, out flavour))
                    {
                        return Fail($"Unknown flavour '{flavourText}'. Use gfm or commonmark.");
                    }

                    break;
                default:
                    return Fail($"Unknown option '{argument}'.");
            }
        }

        var options = new ConversionOptions
        {
            OutputDirectory = output,
            AssetBaseUrl = assetBaseUrl,
            IncludeImages = includeImages,
            IncludeFrontmatter = includeFrontmatter,
            Flavour = flavour,
            ImageDirectoryName = imageDirectory,
        };

        return new ParseResult(CommandKind.Convert, input, options, review, null);
    }

    private static bool TryTake(string[] args, ref int index, out string value)
    {
        if (index + 1 >= args.Length || args[index + 1].StartsWith('-'))
        {
            value = "";
            return false;
        }

        value = args[++index];
        return true;
    }

    private static ParseResult Fail(string message)
        => new(CommandKind.Help, null, null, false, message);
}
```

- [ ] **Step 4: Implement `Program`**

`src/Docmd.Cli/Program.cs`:
```csharp
namespace Docmd.Cli;

using Docmd.Word;
using Ooxml.Md.Core.Licensing;
using Ooxml.Md.Core.Opc;

internal static class Program
{
    private const int ExitSuccess = 0;
    private const int ExitInternalError = 1;
    private const int ExitBadInput = 2;
    private const int ExitNotRegistered = 3;

    internal static async Task<int> Main(string[] args)
    {
        var parsed = CommandLine.Parse(args);

        if (parsed.Error is not null)
        {
            Console.Error.WriteLine($"docmd: {parsed.Error}");
            return ExitBadInput;
        }

        switch (parsed.Command)
        {
            case CommandKind.Help:
                PrintUsage();
                return ExitSuccess;
            case CommandKind.Version:
                Console.WriteLine(typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0");
                return ExitSuccess;
            case CommandKind.Audit or CommandKind.Register or CommandKind.License:
                Console.Error.WriteLine($"docmd: '{parsed.Command.ToString().ToLowerInvariant()}' is not available yet.");
                return ExitInternalError;
            default:
                break;
        }

        // The gate is consulted once, before any work. Its implementation is a seam;
        // what matters here is that the answer is honoured.
        ILicenseGate gate = new PermissiveLicenseGate();
        var status = gate.Check();
        if (status.Notice is not null)
        {
            Console.Error.WriteLine(status.Notice);
        }

        if (!status.Allowed)
        {
            return ExitNotRegistered;
        }

        if (parsed.Input is null)
        {
            Console.Error.WriteLine("docmd: no input file given.");
            return ExitBadInput;
        }

        try
        {
            var result = await DocumentConverter.WriteAsync(parsed.Input, parsed.Options!, CancellationToken.None);

            foreach (var issue in result.AssetIssues)
            {
                Console.Error.WriteLine($"! {issue.PartName}: {issue.Reason}");
            }

            return ExitSuccess;
        }
        catch (OpcFormatException ex)
        {
            Console.Error.WriteLine($"docmd: {ex.Message}");
            return ExitBadInput;
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"docmd: {ex.Message}");
            return ExitBadInput;
        }
    }

    private static void PrintUsage() => Console.WriteLine("""
        docmd - convert Microsoft Word documents to Markdown

        Usage:
          docmd <input.docx> [options]
          docmd audit <path> [-r]
          docmd register --email <address> | --key <key>
          docmd license

        Options:
          -o, --output <path>        output directory (default: .)
              --review               also emit <name>.review.html
              --asset-base-url <url> emit remote URLs for local assets
              --img-dir <name>       image folder name (default: img)
              --no-images            omit images entirely
              --flavour <name>       gfm | commonmark (default: gfm)
              --front-matter <mode>  yaml | none (default: yaml)
          -h, --help                 show this help
          -V, --version              show the version

        Only .docx, .docm, .dotx and .dotm are supported. Word 97-2003 (.doc) is a
        different, binary format - re-save it as .docx first.
        """);
}
```

- [ ] **Step 5: Verify the tool actually runs**

```bash
dotnet test docmd.slnx
dotnet run --project src/Docmd.Cli -- --help
```
Expected: tests PASS; help text printed.

Then convert a real document end to end and read the output yourself:
```bash
dotnet run --project src/Docmd.Cli -- /path/to/a/real.docx -o /tmp/docmd-out
cat /tmp/docmd-out/real.md
```

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Ship the command line, with licensing behind a seam

Argument parsing is hand-rolled. System.CommandLine has moved its API
repeatedly across previews and a churning dependency is a liability in a
commercial product; fifteen flags do not justify one.

The licence gate is one method on one interface, consulted once before any
work. Its implementation here allows every run and is explicitly
temporary -- enforcement belongs to the standalone licensing project. What
this commit buys is that the call site is real and tested, so adopting
that project is a registration change that never touches the converter.

PermissiveLicenseGate must not survive into a release: registration to run
is the agreed model, and the release checklist gates on this type being
gone."
```

---

### Task 14: Determinism, culture, and a real Word document

**Files:**
- Test: `tests/Docmd.Word.Tests/DeterminismTests.cs`, `tests/Docmd.Word.Tests/RealDocumentTests.cs`
- Create: `tests/Docmd.Word.Tests/fixtures/real/` (a genuine `.docx`, committed as a binary)

**Interfaces:**
- Consumes: `DocumentConverter` (Task 12).
- Produces: no new API — this task pins the guarantees.

**Context:** Spec §5 and §13. Determinism is a product guarantee, so it gets a test rather than a comment. Culture is where it silently breaks: Turkish lowercases `I` to dotless `ı`, German formats numbers with a comma.

**And a real document is required.** Hand-written fixtures test intent; Word's actual output tests reality — `w:proofErr` scattered through the flow, runs split mid-word by spell-check, `rsid` attributes on everything, `w:bookmarkStart`/`End` between runs. This is the one place a committed binary is correct, because the point is to test markup we did not author.

- [ ] **Step 1: Create the real fixture**

Open Word (or LibreOffice) and produce a document containing: a Heading 1, a Heading 2, two body paragraphs with some bold and italic, a three-item bullet list with one nested item, a numbered list, a 2×3 table with a header row, a hyperlink, and an inserted image. Save as `tests/Docmd.Word.Tests/fixtures/real/sample.docx`.

Commit it with an explicit note that this fixture is binary **on purpose** — see the commit message below.

Add to `Docmd.Word.Tests.csproj`:
```xml
<ItemGroup>
  <None Include="fixtures/real/*.docx" CopyToOutputDirectory="PreserveNewest" LinkBase="fixtures/real" />
</ItemGroup>
```

- [ ] **Step 2: Write the determinism tests**

`tests/Docmd.Word.Tests/DeterminismTests.cs`:
```csharp
namespace Docmd.Word.Tests;

using System.Globalization;
using System.Threading.Tasks;
using Docmd.Word;
using FluentAssertions;
using Xunit;

public sealed class DeterminismTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory().FullName;

    private string Sample => Path.Combine(AppContext.BaseDirectory, "fixtures", "real", "sample.docx");

    private ConversionOptions Options => new() { OutputDirectory = _workspace };

    [Fact]
    public void Fixture_IsStaged()
        // Without this the tests below would pass vacuously if the copy wiring broke.
        => File.Exists(Sample).Should().BeTrue("the real Word fixture must reach the output directory");

    [Fact]
    public async Task Converting_Twice_ProducesIdenticalBytes()
    {
        var first = await DocumentConverter.ConvertAsync(Sample, Options, default);
        var second = await DocumentConverter.ConvertAsync(Sample, Options, default);

        second.Markdown.Should().Be(first.Markdown);
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("tr-TR")]
    public async Task Converting_UnderAnotherCulture_ProducesIdenticalBytes(string culture)
    {
        // tr-TR lowercases 'I' to dotless 'i', which changes slugs; de-DE uses a comma
        // decimal separator. Either silently breaks the byte-identical guarantee.
        var baseline = await DocumentConverter.ConvertAsync(Sample, Options, default);

        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            var underCulture = await DocumentConverter.ConvertAsync(Sample, Options, default);

            underCulture.Markdown.Should().Be(baseline.Markdown);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public async Task Output_ContainsNoTimestamp()
    {
        var result = await DocumentConverter.ConvertAsync(Sample, Options, default);

        result.Markdown.Should().NotContain(DateTime.UtcNow.Year.ToString(CultureInfo.InvariantCulture) + "-" +
                                            DateTime.UtcNow.Month.ToString("D2", CultureInfo.InvariantCulture) + "-" +
                                            DateTime.UtcNow.Day.ToString("D2", CultureInfo.InvariantCulture),
                                            "no conversion timestamp may appear in the output");
    }

    public void Dispose() => Directory.Delete(_workspace, recursive: true);
}
```

Note: the timestamp test can false-positive if the document's own `created` date happens to be today. If that occurs, change the real fixture's created date rather than weakening the assertion.

- [ ] **Step 3: Write the real-document test**

`tests/Docmd.Word.Tests/RealDocumentTests.cs`:
```csharp
namespace Docmd.Word.Tests;

using System.Threading.Tasks;
using Docmd.Word;
using FluentAssertions;
using Xunit;

/// <summary>
/// Word's own output is far messier than anything hand-written: w:proofErr through the
/// flow, runs split mid-word by spell-check, rsid attributes everywhere, bookmarks
/// between runs. Hand-written fixtures test intent; this tests reality.
/// </summary>
public sealed class RealDocumentTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory().FullName;

    private string Sample => Path.Combine(AppContext.BaseDirectory, "fixtures", "real", "sample.docx");

    [Fact]
    public async Task RealDocument_ProducesTheExpectedStructure()
    {
        var result = await DocumentConverter.ConvertAsync(
            Sample, new ConversionOptions { OutputDirectory = _workspace }, default);

        var markdown = result.Markdown;

        markdown.Should().Contain("\n# ", "the Heading 1 must survive");
        markdown.Should().Contain("\n## ", "the Heading 2 must survive");
        markdown.Should().Contain("\n- ", "the bullet list must survive");
        markdown.Should().Contain("\n1. ", "the numbered list must survive");
        markdown.Should().Contain("| --- |", "the table must survive with a delimiter row");
        markdown.Should().Contain("](", "the hyperlink and image must survive");

        // Spell-check and rsid boundaries split runs mid-word. If they are not rejoined,
        // words come out fragmented -- the single most visible real-document failure.
        markdown.Should().NotMatchRegex(@"\*\*\w+\*\*\w", "runs split mid-word must be rejoined");
    }

    [Fact]
    public async Task RealDocument_ReportsNoUnexpectedAssetIssues()
    {
        var result = await DocumentConverter.ConvertAsync(
            Sample, new ConversionOptions { OutputDirectory = _workspace }, default);

        result.AssetIssues.Should().BeEmpty("the sample uses a PNG, which renders everywhere");
    }

    public void Dispose() => Directory.Delete(_workspace, recursive: true);
}
```

- [ ] **Step 4: Run everything**

Run: `dotnet test docmd.slnx`
Expected: PASS.

Real documents will very likely surface failures here that hand-written fixtures did not. **That is the point of this task.** Fix them in the stylesheet or the annotator — and if the cause turns out to be the engine, follow the engine-defect protocol at the top of this plan rather than working around it silently.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Pin determinism, and test against markup we did not author

Byte-identical output is a product guarantee, so it gets a test rather
than a comment -- including under de-DE and tr-TR, which is where it
silently breaks. Turkish lowercases 'I' to a dotless letter, which changes
every slug; German formats numbers with a comma. Neither fails loudly.

The sample .docx is committed as a binary on purpose, and it is the only
fixture that should be. Every other fixture is an unzipped directory
precisely so it can be reviewed and diffed -- but the value of this one is
that we did NOT author its markup. Word's real output carries w:proofErr
through the flow, rsid attributes on everything, bookmarks between runs,
and runs split mid-word by spell-check. Hand-written fixtures test intent;
this tests reality, and the two find different bugs."
```

---

## Plan Self-Review

Run through this before starting Task 1.

**Spec coverage:**

| Spec section | Task |
|---|---|
| §3.1 OOXML family only | 2 (OpcFormatException message), 13 (help text) |
| §3.2 Markdown + YAML frontmatter | 11 |
| §4.1 project layout | 1 |
| §4.2 pipeline | 12 |
| §4.3 composite | 3 |
| §4.4 annotation, heading rules, slugs | 4 |
| §4.5 md-XML + serialiser | 5, 6 |
| §5 determinism | 5 (LF), 11 (no timestamp), 14 (tests) |
| §6 frontmatter | 11 |
| §7.1 revisions — accepted text default | 7 (`visible-text` excludes `w:delText`) |
| §10.1 asset sink | 10 |
| §10.2 images, EMF/WMF | 10 |
| §11 licensing seam | 13 |
| §12 CLI + exit codes | 13 |
| §13.1 unzipped fixtures | 2, 14 |
| §13.2 Markdig oracle | 6 |
| §13.3 no vacuous passes | 2 (`Fixture_IsStaged`), 14 |
| §15 dependency licences | 1 |

**Deferred to later plans, by design:** §7.2 the review companion and full revision modes (Plan 2); §8 style map and §9 corpus audit (Plan 3); §10.3 paid sinks and §11 the real licence gate (Plan 4). `--review`, `--style-map`, `--strict`, `--report` and `--revisions` are parsed or documented but not yet functional; Plan 2 and 3 wire them.

**Known gaps deliberately left open:**
- `-r` / recursive directory conversion is in the CLI surface but not implemented in Task 13. Add it as the first task of Plan 3, where the audit needs batch traversal anyway.
- `--flavour commonmark` is parsed and plumbed, but `MarkdownSerializer` currently emits GFM tables regardless. Spec §12 lists it; wire the CommonMark HTML-table fallback in Plan 3.
- `ConversionOptions.ImageDirectoryName` is carried but `AssetRewriter` hardcodes `img/`. Thread it through in Plan 3 when `--img-dir` becomes testable end to end.

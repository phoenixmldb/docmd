using System.Runtime.CompilerServices;

// Grants the test assembly access to internal CLI parsing types (CommandLine, ParseResult,
// CommandKind) without widening this Exe/PackAsTool project's public surface -- nobody
// references an app project as a library (CA1515). Mirrors Ooxml.Md.Core/AssemblyInfo.cs.
[assembly: InternalsVisibleTo("Docmd.Word.Tests")]

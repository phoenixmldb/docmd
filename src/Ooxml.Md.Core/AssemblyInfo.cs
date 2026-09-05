using System.Runtime.CompilerServices;

// Grants the test assembly access to internal placeholder/throwaway types (e.g.
// Markdown.MarkdownEscaper) without widening their public surface just to be testable.
[assembly: InternalsVisibleTo("Ooxml.Md.Core.Tests")]

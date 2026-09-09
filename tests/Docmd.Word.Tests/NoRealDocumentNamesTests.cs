namespace Docmd.Word.Tests;

using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

/// <summary>
/// Guards against naming a real document anywhere in the repository.
/// </summary>
/// <remarks>
/// <para>
/// docmd is developed against corpora of real business documents, which are not in this
/// repository and must not be identifiable from it. A corpus filename typically carries the
/// client name, the project and a revision, which together say more about who we work with than
/// any of our documentation intends to.
/// </para>
/// <para>
/// This is not hypothetical. A defect report cited a customer document by filename and reached
/// the default branch, and removing it afterwards meant rewriting published history — a thing
/// that is cheap only while a repository is private and impossible to do meaningfully once it
/// is not. A rule that depends on remembering is the rule that fails; this one fails the build.
/// </para>
/// <para>
/// Adding a name to the allow-list is deliberately a code change, reviewed like any other.
/// </para>
/// </remarks>
public sealed class NoRealDocumentNamesTests
{
    /// <summary>
    /// Names that may appear, read from <c>.allowed-document-names</c> at the repository root.
    /// </summary>
    /// <remarks>
    /// Kept in a file rather than in this test because CI applies the same list to commit
    /// messages, and two copies of a rule drift until one of them is wrong. Adding a name is a
    /// reviewed change either way.
    /// </remarks>
    private static HashSet<string> Allowed(DirectoryInfo root) =>
        new(File.ReadLines(Path.Combine(root.FullName, ".allowed-document-names"))
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('#')),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A filename token: no spaces, so prose like "a valid .docx" is not a match, while
    /// "March 10.docx" still trips on its "10.docx" tail rather than slipping through.
    /// </summary>
    private static readonly Regex DocumentName =
        new(@"\b[A-Za-z0-9][A-Za-z0-9._-]*\.(?:docx|docm|dotx|dotm|pptx|pptm|potx|ppt|xlsx|xlsm|doc)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly string[] Scanned = [".md", ".cs", ".xslt", ".yml", ".yaml", ".csproj"];

    private static DirectoryInfo RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "docmd.slnx")))
        {
            directory = directory.Parent;
        }

        return directory ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    [Fact]
    public void NoFileNamesARealDocument()
    {
        var root = RepositoryRoot();
        var allowed = Allowed(root);
        var offences = new List<string>();

        foreach (var file in root.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            if (!Scanned.Contains(file.Extension, StringComparer.OrdinalIgnoreCase)
                || file.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.FullName.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            // This file names documents by construction; excluding it keeps the allow-list from
            // having to allow itself.
            if (string.Equals(file.Name, "NoRealDocumentNamesTests.cs", StringComparison.Ordinal))
            {
                continue;
            }

            var line = 0;
            foreach (var text in File.ReadLines(file.FullName))
            {
                line++;
                foreach (Match match in DocumentName.Matches(text))
                {
                    if (!allowed.Contains(match.Value))
                    {
                        offences.Add(
                            $"{Path.GetRelativePath(root.FullName, file.FullName)}:{line}: '{match.Value}'");
                    }
                }
            }
        }

        offences.Should().BeEmpty(
            "no real document may be named in this repository. Corpus documents belong to "
            + "customers and their filenames identify them. If the name is a fixture this "
            + "repository owns, or a placeholder, add it to .allowed-document-names; "
            + "otherwise describe the document instead, as in "
            + "'a 1,000-paragraph design document'");
    }
}

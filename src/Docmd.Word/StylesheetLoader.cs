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

namespace Ooxml.Md.Core.Opc;

/// <summary>A single entry from a <c>.rels</c> part.</summary>
/// <param name="Id">The relationship id, e.g. <c>rId7</c>, as referenced from the source part.</param>
/// <param name="Type">The relationship type URI, e.g. an image or hyperlink relationship type.</param>
/// <param name="Target">
/// Verbatim from the XML. Relative targets are resolved against the source part's
/// directory by <see cref="OpcPackage.ResolveTarget"/>; external ones are absolute URIs.
/// </param>
/// <param name="IsExternal">True when the relationship's <c>TargetMode</c> is <c>External</c>.</param>
public sealed record OpcRelationship(string Id, string Type, string Target, bool IsExternal);

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

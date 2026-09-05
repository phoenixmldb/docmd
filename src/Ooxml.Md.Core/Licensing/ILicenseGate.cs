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

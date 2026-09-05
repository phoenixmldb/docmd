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

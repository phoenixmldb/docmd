namespace Docmd.Word.Tests;

using System.Threading.Tasks;
using Docmd.Cli;
using FluentAssertions;
using Ooxml.Md.Core.Licensing;
using Xunit;

/// <summary>
/// Exercises the exit-code contract (spec §12) through the program's real entry point.
/// </summary>
/// <remarks>
/// There were no licence-gate tests at all: the gate was constructed inside Main, and the
/// only implementation that exists allows every run, so exit code 3 was documented and
/// unreachable. Program.Run takes the gate as a parameter for exactly this reason.
/// </remarks>
public sealed class ProgramTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory().FullName;

    private sealed class DenyingLicenseGate : ILicenseGate
    {
        public LicenseStatus Check() => new(Allowed: false, Notice: "docmd: not registered.");
    }

    private static Task<int> RunAsync(params string[] args)
        => Program.Run(args, new PermissiveLicenseGate());

    [Fact]
    public async Task Run_ReturnsNotRegisteredWhenTheGateRefuses()
    {
        // Exit 3 is part of the published contract and, until the gate became injectable,
        // could not be reached from a test at all.
        var exitCode = await Program.Run(["report.docx"], new DenyingLicenseGate());

        exitCode.Should().Be(3);
    }

    [Fact]
    public async Task Run_ConsultsTheGateBeforeTouchingTheInputFile()
    {
        // The same arguments exit 2 under a permissive gate, because the file does not
        // exist. Under a refusing one the refusal must win: the gate is consulted before
        // any work, not as a late guard a missing file can pre-empt.
        var missing = Path.Combine(_workspace, "absent.docx");

        (await RunAsync(missing, "-o", _workspace)).Should().Be(2);
        (await Program.Run([missing, "-o", _workspace], new DenyingLicenseGate())).Should().Be(3);
    }

    [Theory]
    [InlineData("audit")]
    [InlineData("register")]
    [InlineData("license")]
    public async Task Run_ReturnsUsageErrorForASubcommandThatIsNotImplemented(string subcommand)
        // Spec §12 reserves 1 for an unexpected internal error. Returning it for a
        // deliberately unimplemented subcommand leaves a CI script unable to tell a bug
        // from a command it should not have run.
        => (await RunAsync(subcommand)).Should().Be(2);

    [Fact]
    public async Task Run_ReturnsUsageErrorForAnEmptyInputPath()
        // This reached DocumentConverter, whose ArgumentException landed in the top-level
        // catch-all and exited 1.
        => (await RunAsync("")).Should().Be(2);

    [Fact]
    public async Task Run_ReturnsUsageErrorForAFileThatIsNotAnOoxmlPackage()
    {
        var path = Path.Combine(_workspace, "legacy.doc");
        await File.WriteAllTextAsync(path, "not a zip", TestContext.Current.CancellationToken);

        (await RunAsync(path, "-o", _workspace)).Should().Be(2);
    }

    [Fact]
    public async Task Run_ReturnsSuccessForHelpAndVersion()
    {
        (await RunAsync("--help")).Should().Be(0);
        (await RunAsync("--version")).Should().Be(0);
    }

    public void Dispose() => Directory.Delete(_workspace, recursive: true);
}

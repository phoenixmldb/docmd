namespace Docmd.Word.Tests;

using System.Threading.Tasks;
using Docmd.Cli;
using FluentAssertions;
using Xunit;

/// <summary>
/// Exercises the exit-code contract (spec §12) through the program's real entry point.
/// </summary>
public sealed class ProgramTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory().FullName;

    private static Task<int> RunAsync(params string[] args) => Program.Run(args);

    [Fact]
    public async Task Run_ReturnsUsageErrorForAnInputFileThatDoesNotExist()
    {
        var missing = Path.Combine(_workspace, "absent.docx");

        (await RunAsync(missing, "-o", _workspace)).Should().Be(2);
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

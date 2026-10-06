namespace DotnetPackageSkills.Tests;

public class PipelineResultsTests
{
    [Fact]
    public async Task Both_frameworks_require_nonempty_successful_results()
    {
        using var temporary = new TempDirectory();
        WriteResults(temporary, "net8.0");
        WriteResults(temporary, "net10.0");

        var result = await Check(temporary.Path);

        Assert.True(result.ExitCode == 0, result.Diagnostics);
        Assert.Contains("net8.0", result.Output);
        Assert.Contains("net10.0", result.Output);
    }

    [Theory]
    [InlineData("net8.0")]
    [InlineData("net10.0")]
    public async Task Missing_framework_results_fail_closed(string missing)
    {
        using var temporary = new TempDirectory();
        WriteResults(temporary, missing == "net8.0" ? "net10.0" : "net8.0");

        var result = await Check(temporary.Path);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("missing", result.Diagnostics, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(missing, result.Diagnostics);
    }

    [Theory]
    [InlineData("0", "0", "0", "0")]
    [InlineData("2", "1", "1", "0")]
    [InlineData("2", "2", "1", "1")]
    [InlineData("garbage", "2", "2", "0")]
    public async Task Empty_failed_incomplete_or_malformed_counters_fail_closed(
        string total, string executed, string passed, string failed)
    {
        using var temporary = new TempDirectory();
        WriteResults(temporary, "net8.0", total, executed, passed, failed);
        WriteResults(temporary, "net10.0");

        var result = await Check(temporary.Path);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("results", result.Diagnostics, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Passed_counters_cannot_hide_a_failed_individual_result()
    {
        using var temporary = new TempDirectory();
        var file = WriteResults(temporary, "net8.0");
        File.WriteAllText(file, File.ReadAllText(file).Replace("outcome=\"Passed\"", "outcome=\"Failed\""));
        WriteResults(temporary, "net10.0");

        var result = await Check(temporary.Path);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("results", result.Diagnostics, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_previous_run_cannot_supply_missing_current_results()
    {
        using var temporary = new TempDirectory();
        File.SetLastWriteTimeUtc(WriteResults(temporary, "net8.0"), DateTime.UtcNow.AddHours(-1));
        WriteResults(temporary, "net10.0");

        var result = await Check(temporary.Path, DateTime.UtcNow.AddMinutes(-1));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("stale", result.Diagnostics, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Build_configuration_tests_cannot_replace_the_functional_suite()
    {
        using var temporary = new TempDirectory();
        WriteResults(temporary, "net8.0");
        WriteResults(temporary, "net10.0");
        var result = await PipelineTestContext.Run("pwsh",
        [
            "-NoLogo", "-NoProfile", "-NonInteractive", "-File", PipelineTestContext.PipelinePath("Test-Results.ps1"),
            "-ResultsDirectory", temporary.Path, "-MinimumFunctionalTests", "792",
        ]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("functional", result.Diagnostics, StringComparison.OrdinalIgnoreCase);
    }

    private static string WriteResults(TempDirectory directory, string framework,
        string total = "2", string executed = "2", string passed = "2", string failed = "0") =>
        directory.CreateFile($"DotnetPackageSkills.Tests_{framework}_x64.trx",
            "<TestRun><Results><UnitTestResult outcome=\"Passed\" /><UnitTestResult outcome=\"Passed\" /></Results>" +
            $"<ResultSummary outcome=\"Completed\"><Counters total=\"{total}\" executed=\"{executed}\" " +
            $"passed=\"{passed}\" failed=\"{failed}\" /></ResultSummary></TestRun>");

    private static Task<PipelineProcessResult> Check(string directory, DateTime? notBefore = null)
    {
        var arguments = new List<string>
        {
            "-NoLogo", "-NoProfile", "-NonInteractive", "-File", PipelineTestContext.PipelinePath("Test-Results.ps1"),
            "-ResultsDirectory", directory,
        };
        if (notBefore is not null)
        {
            arguments.AddRange(["-NotBeforeUtc", notBefore.Value.ToString("O")]);
        }
        return PipelineTestContext.Run("pwsh", arguments);
    }
}

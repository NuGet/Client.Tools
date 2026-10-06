namespace DotnetPackageSkills.Tests;

public class PipelineRetirementTests
{
    [Fact]
    public async Task Isolated_retirement_keeps_the_Arcade_foundation_and_another_tool()
    {
        var result = await PipelineTestContext.Run("pwsh",
        [
            "-NoLogo", "-NoProfile", "-NonInteractive", "-File", PipelineTestContext.PipelinePath("Test-Retirement.ps1"),
        ]);

        Assert.True(result.ExitCode == 0, result.Diagnostics);
        Assert.Contains("Arcade infrastructure validation succeeded", result.Output);
        Assert.Contains("Future tool foundation validation succeeded", result.Output);
        Assert.Contains("Isolated retirement validation succeeded", result.Output);
    }
}

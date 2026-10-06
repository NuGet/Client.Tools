namespace DotnetPackageSkills.Tests;

public class PipelineSourceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Official_source_guard_accepts_only_an_explicit_trusted_team(bool release)
    {
        var result = await Check(release: release);

        Assert.True(result.ExitCode == 0, result.Diagnostics);
    }

    [Theory]
    [InlineData("BUILD_REPOSITORY_PROVIDER", "GitHub")]
    [InlineData("BUILD_REPOSITORY_NAME", "NuGet/Client.Tools")]
    [InlineData("SYSTEM_TEAMPROJECT", "public")]
    [InlineData("SYSTEM_COLLECTIONURI", "https://dev.azure.com/dnceng-public/")]
    [InlineData("BUILD_SOURCEBRANCH", "refs/heads/feature")]
    [InlineData("BUILD_REASON", "PullRequest")]
    [InlineData("BUILD_REASON", "")]
    public async Task Official_source_guard_rejects_untrusted_or_missing_metadata(string name, string value)
    {
        var result = await Check(name, value);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("trusted", result.Diagnostics, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("$(DotnetPackageSkillsSigningTeamName)")]
    [InlineData("unreviewed;property")]
    public async Task Official_source_guard_requires_a_configured_signing_team(string value)
    {
        var result = await Check("DOTNET_PACKAGE_SKILLS_SIGNING_TEAM", value);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("team", result.Diagnostics, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_automatic_official_run_cannot_request_a_stable_release()
    {
        var result = await Check("BUILD_REASON", "IndividualCI", release: true);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("manual", result.Diagnostics, StringComparison.OrdinalIgnoreCase);
    }

    private static Task<PipelineProcessResult> Check(string? name = null, string? value = null, bool release = false)
    {
        var environment = PipelineTestContext.OfficialInputs();
        environment["DOTNET_PACKAGE_SKILLS_SIGNING_TEAM"] = "FixtureApprovedTeam";
        if (name is not null) { environment[name] = value!; }
        var arguments = new List<string>
        {
            "-NoLogo", "-NoProfile", "-NonInteractive", "-File", PipelineTestContext.PipelinePath("Validate-OfficialSource.ps1"),
        };
        if (release) { arguments.Add("-ReleaseBuild"); }
        return PipelineTestContext.Run("pwsh", arguments, environment);
    }
}

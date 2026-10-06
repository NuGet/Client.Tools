namespace DotnetPackageSkills.Tests;

public class PipelineVersionTests
{
    [Theory]
    [InlineData("net8.0", false, "0.1.0-dev")]
    [InlineData("net10.0", false, "0.1.0-dev")]
    [InlineData("net8.0", true, "0.1.0-ci")]
    [InlineData("net10.0", true, "0.1.0-ci")]
    public async Task Native_versions_match_local_and_public_builds(string framework, bool ci, string expected)
    {
        var result = await PipelineTestContext.Evaluate(framework, new Dictionary<string, string>
        {
            ["ContinuousIntegrationBuild"] = ci ? "true" : "false",
        });
        var properties = PipelineTestContext.Properties(result);

        Assert.Equal(expected, properties["Version"]);
        Assert.Equal(expected, properties["PackageVersion"]);
        Assert.Equal("false", properties["DotnetPackageSkillsReleaseBuild"]);
        Assert.Empty(properties["DotNetFinalVersionKind"]);
    }

    [Theory]
    [InlineData("net8.0", false)]
    [InlineData("net10.0", false)]
    [InlineData("net8.0", true)]
    [InlineData("net10.0", true)]
    public async Task Native_official_versions_use_the_same_inputs_for_assembly_and_package(string framework, bool release)
    {
        var properties = PipelineTestContext.Properties(
            await PipelineTestContext.Evaluate(framework, PipelineTestContext.OfficialInputs(release)));
        var expected = release
            ? "0.1.0"
            : $"0.1.0-beta.{properties["VersionSuffixDateStamp"]}.{properties["VersionSuffixBuildOfTheDay"]}";

        Assert.Equal(expected, properties["Version"]);
        Assert.Equal(expected, properties["PackageVersion"]);
        Assert.Equal("26505", properties["VersionSuffixDateStamp"]);
        Assert.Equal("2", properties["VersionSuffixBuildOfTheDay"]);
        Assert.Equal(release ? "release" : "", properties["DotNetFinalVersionKind"]);
    }

    [Theory]
    [InlineData("net8.0")]
    [InlineData("net10.0")]
    public async Task Native_official_revision_changes_the_prerelease(string framework)
    {
        var inputs = PipelineTestContext.OfficialInputs();
        inputs["OfficialBuildId"] = "20261005.3";
        var properties = PipelineTestContext.Properties(await PipelineTestContext.Evaluate(framework, inputs));

        Assert.Equal("0.1.0-beta.26505.3", properties["Version"]);
        Assert.Equal(properties["Version"], properties["PackageVersion"]);
    }

    [Theory]
    [InlineData("ContinuousIntegrationBuild", "false")]
    [InlineData("OfficialBuildId", "")]
    [InlineData("BUILD_REASON", "IndividualCI")]
    [InlineData("BUILD_REASON", "PullRequest")]
    [InlineData("BUILD_REASON", "")]
    [InlineData("BUILD_SOURCEBRANCH", "refs/heads/feature")]
    [InlineData("BUILD_SOURCEBRANCH", "")]
    [InlineData("BUILD_REPOSITORY_PROVIDER", "GitHub")]
    [InlineData("BUILD_REPOSITORY_NAME", "NuGet/Client.Tools")]
    [InlineData("SYSTEM_TEAMPROJECT", "public")]
    [InlineData("SYSTEM_COLLECTIONURI", "https://dev.azure.com/dnceng-public/")]
    public async Task Stable_requests_reject_untrusted_or_missing_inputs(string property, string value)
    {
        var inputs = PipelineTestContext.OfficialInputs(release: true);
        inputs[property] = value;

        var result = await PipelineTestContext.Evaluate("net8.0", inputs);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("dotnet-package-skills", result.Diagnostics);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("$(Build.BuildId)")]
    [InlineData("20261301.1")]
    [InlineData("20260230.1")]
    [InlineData("20261005.0")]
    [InlineData("20261005.100")]
    [InlineData("20261005.01")]
    [InlineData("20261005.x")]
    public async Task Stable_requests_reject_malformed_official_build_numbers(string buildNumber)
    {
        var inputs = PipelineTestContext.OfficialInputs(release: true);
        inputs["OfficialBuildId"] = buildNumber;

        var result = await PipelineTestContext.Evaluate("net10.0", inputs);

        Assert.NotEqual(0, result.ExitCode);
    }

    [Theory]
    [InlineData("release")]
    [InlineData("prerelease")]
    public async Task Final_version_cannot_bypass_the_default_false_release_opt_in(string finalKind)
    {
        var inputs = PipelineTestContext.OfficialInputs();
        inputs["DotNetFinalVersionKind"] = finalKind;

        var result = await PipelineTestContext.Evaluate("net8.0", inputs);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("DotnetPackageSkillsReleaseBuild", result.Diagnostics);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("$(ReleaseBuild)")]
    public async Task Release_opt_in_requires_a_boolean(string value)
    {
        var inputs = PipelineTestContext.OfficialInputs();
        inputs["DotnetPackageSkillsReleaseBuild"] = value;

        var result = await PipelineTestContext.Evaluate("net8.0", inputs);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("DotnetPackageSkillsReleaseBuild", result.Diagnostics);
    }

    [Theory]
    [InlineData("RuntimeIdentifier", "win-x64")]
    [InlineData("Version", "0.1.0")]
    [InlineData("PackageVersion", "0.2.0")]
    public async Task Packaging_cannot_override_portability_or_native_version_inputs(string property, string value)
    {
        var result = await PipelineTestContext.Evaluate("net8.0", new Dictionary<string, string>
        {
            [property] = value,
        });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("dotnet-package-skills", result.Diagnostics);
    }

    [Theory]
    [InlineData("net8.0")]
    [InlineData("net10.0")]
    public async Task Tool_configuration_preserves_identity_and_native_artifact_paths(string framework)
    {
        var properties = PipelineTestContext.Properties(await PipelineTestContext.Evaluate(framework));

        Assert.Equal("false", properties["SignAssembly"]);
        Assert.Equal("true", properties["IsPackable"]);
        Assert.Equal("true", properties["IsShipping"]);
        Assert.Equal("false", properties["PackageRequireLicenseAcceptance"]);
        Assert.Empty(properties["RuntimeIdentifier"]);
        Assert.Equal(Path.GetFullPath(PipelineTestContext.RepoRoot) + Path.DirectorySeparatorChar, properties["RepoRoot"]);
        Assert.Equal(Path.Combine(PipelineTestContext.RepoRoot, "artifacts", "bin", "DotnetPackageSkills") +
            Path.DirectorySeparatorChar, properties["BaseOutputPath"]);
        Assert.Equal(Path.Combine(PipelineTestContext.RepoRoot, "artifacts", "obj", "DotnetPackageSkills") +
            Path.DirectorySeparatorChar, properties["BaseIntermediateOutputPath"]);
    }

    [Theory]
    [InlineData("net8.0")]
    [InlineData("net10.0")]
    public async Task Tests_remain_unsigned_nonpackable_and_nonshipping(string framework)
    {
        var properties = PipelineTestContext.Properties(await PipelineTestContext.Evaluate(
            framework, project: @"dotnet-package-skills\tests\DotnetPackageSkills.Tests.csproj"));

        Assert.Equal("false", properties["SignAssembly"]);
        Assert.Equal("false", properties["IsPackable"]);
        Assert.Equal("false", properties["IsShipping"]);
    }
}

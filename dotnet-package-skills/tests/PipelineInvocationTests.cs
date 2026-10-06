using System.Text.Json;

namespace DotnetPackageSkills.Tests;

public class PipelineInvocationTests
{
    [Fact]
    public async Task Metadata_uses_Arcades_actual_sdk_without_requiring_an_optional_sdk_cache_file()
    {
        var result = await PipelineTestContext.Run("pwsh",
        [
            "-NoLogo", "-NoProfile", "-NonInteractive", "-File",
            PipelineTestContext.PipelinePath("Invoke-Build.ps1"), "-Action", "Metadata",
        ]);

        Assert.True(result.ExitCode == 0, result.Diagnostics);
        using var document = JsonDocument.Parse(result.Output);
        var metadata = document.RootElement;
        Assert.Equal("0.1.0-dev", metadata.GetProperty("PackageVersion").GetString());
        Assert.Equal(metadata.GetProperty("Version").GetString(), metadata.GetProperty("PackageVersion").GetString());
        Assert.True(File.Exists(metadata.GetProperty("DotnetPath").GetString()), result.Output);
        Assert.True(File.Exists(metadata.GetProperty("DotnetPackageSkillsStrongNameToolPath").GetString()), result.Output);
        Assert.EndsWith("dotnet-package-skills.0.1.0-dev.nupkg",
            metadata.GetProperty("DotnetPackageSkillsPackagePath").GetString());
    }
}

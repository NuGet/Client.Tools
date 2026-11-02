using DotnetPackageSkills.Infrastructure;

namespace DotnetPackageSkills.Tests;

public class ProcessRunnerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("target folder")]
    public void Sdk_processes_do_not_inherit_the_terminal_and_preserve_argument_boundaries(string? workingDirectory)
    {
        string[] arguments = ["list", "project with spaces.csproj", "package", "--no-restore", "--format", "json"];

        var startInfo = ProcessRunner.CreateStartInfo("dotnet", arguments, workingDirectory);

        Assert.True(startInfo.RedirectStandardInput);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.Equal("dotnet", startInfo.FileName);
        Assert.Equal(workingDirectory ?? string.Empty, startInfo.WorkingDirectory);
        Assert.Equal(arguments, startInfo.ArgumentList);
    }
}

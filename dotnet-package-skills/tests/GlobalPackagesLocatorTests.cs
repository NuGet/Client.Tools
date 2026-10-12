using DotnetPackageSkills.Infrastructure;
using DotnetPackageSkills.NuGet;

namespace DotnetPackageSkills.Tests;

public class GlobalPackagesLocatorTests
{
    private static string SomeAbsolutePath => Path.Combine(Path.GetTempPath(), "nuget-packages");

    [Fact]
    public void A_missing_override_uses_the_global_packages_directory_terminology()
    {
        using var temp = new TempDirectory();
        var locator = new GlobalPackagesLocator(new DotnetCli(new UnusedRunner()));

        var error = Assert.Throws<PackageSkillsException>(() => locator.Locate(temp.Combine("missing"), temp.Path));

        Assert.Contains("NuGet global-packages directory", error.Message);
        Assert.Contains("--global-packages", error.Message);
        Assert.DoesNotContain("cache", error.Message);
    }

    [Fact]
    public void ParseListOutput_reads_the_path_from_current_SDK_output()
    {
        var output = $"global-packages: {SomeAbsolutePath}";

        Assert.Equal(Path.GetFullPath(SomeAbsolutePath), GlobalPackagesLocator.ParseListOutput(output));
    }

    [Fact]
    public void ParseListOutput_reads_the_path_from_older_prefixed_output()
    {
        // Older SDKs prefix the line, which is why parsing keys off the label.
        var output = $"info : global-packages: {SomeAbsolutePath}";

        Assert.Equal(Path.GetFullPath(SomeAbsolutePath), GlobalPackagesLocator.ParseListOutput(output));
    }

    [Fact]
    public void ParseListOutput_ignores_surrounding_lines()
    {
        var output = $"""
            Welcome to .NET!
            ----------------
            global-packages: {SomeAbsolutePath}

            """;

        Assert.Equal(Path.GetFullPath(SomeAbsolutePath), GlobalPackagesLocator.ParseListOutput(output));
    }

    [Fact]
    public void ParseListOutput_tolerates_windows_line_endings()
    {
        var output = $"info : something\r\nglobal-packages: {SomeAbsolutePath}\r\n";

        Assert.Equal(Path.GetFullPath(SomeAbsolutePath), GlobalPackagesLocator.ParseListOutput(output));
    }

    [Fact]
    public void ParseListOutput_returns_null_when_the_label_is_absent() =>
        Assert.Null(GlobalPackagesLocator.ParseListOutput("http-cache: /somewhere\ntemp: /elsewhere"));

    [Fact]
    public void ParseListOutput_returns_null_when_the_label_has_no_value() =>
        Assert.Null(GlobalPackagesLocator.ParseListOutput("global-packages:   "));

    private sealed class UnusedRunner : IProcessRunner
    {
        public ProcessResult Run(string fileName, IReadOnlyList<string> arguments, string? workingDirectory = null) =>
            throw new InvalidOperationException("An explicit directory override must not invoke dotnet.");
    }
}

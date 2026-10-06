using System.Diagnostics;
using System.Text.Json;

namespace DotnetPackageSkills.Tests;

internal static class PipelineTestContext
{
    internal static string RepoRoot { get; } = FindRepoRoot();

    internal static string DotnetPath { get; } =
        File.Exists(Path.Combine(RepoRoot, ".dotnet", "dotnet.exe"))
            ? Path.Combine(RepoRoot, ".dotnet", "dotnet.exe")
            : Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ??
                Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "..", "..", "..", "dotnet.exe"));

    internal static string PipelinePath(string name) =>
        Path.Combine(RepoRoot, "eng", "pipelines", "dotnet-package-skills", name);

    internal static async Task<PipelineProcessResult> Evaluate(
        string framework, IReadOnlyDictionary<string, string>? inputs = null,
        string project = @"dotnet-package-skills\src\DotnetPackageSkills.csproj")
    {
        var properties = new Dictionary<string, string>
        {
            ["Configuration"] = "Release",
            ["TargetFramework"] = framework,
            ["ContinuousIntegrationBuild"] = "false",
            ["OfficialBuildId"] = "",
            ["BUILD_REASON"] = "",
            ["BUILD_SOURCEBRANCH"] = "",
            ["BUILD_REPOSITORY_PROVIDER"] = "",
            ["BUILD_REPOSITORY_NAME"] = "",
            ["SYSTEM_TEAMPROJECT"] = "",
            ["SYSTEM_COLLECTIONURI"] = "",
        };
        if (inputs is not null)
        {
            foreach (var (key, value) in inputs) { properties[key] = value; }
        }
        var arguments = new List<string>
        {
            "msbuild", Path.Combine(RepoRoot, project), "-nologo", "-verbosity:quiet",
            "-target:GetAssemblyVersion",
            "-getProperty:Version,PackageVersion,VersionPrefix,VersionSuffixDateStamp,VersionSuffixBuildOfTheDay," +
                "DotnetPackageSkillsReleaseBuild,DotNetFinalVersionKind,SignAssembly,IsPackable,IsShipping," +
                "RuntimeIdentifier,RepoRoot,BaseOutputPath,BaseIntermediateOutputPath,PackageRequireLicenseAcceptance," +
                "NuGetPackageRoot,SNVersion",
        };
        arguments.AddRange(properties.Select(property => $"-property:{property.Key}={property.Value}"));
        return await Run(DotnetPath, arguments);
    }

    internal static Dictionary<string, string> OfficialInputs(bool release = false) => new()
    {
        ["ContinuousIntegrationBuild"] = "true",
        ["OfficialBuildId"] = "20261005.2",
        ["DotnetPackageSkillsReleaseBuild"] = release ? "true" : "false",
        ["BUILD_REASON"] = "Manual",
        ["BUILD_SOURCEBRANCH"] = "refs/heads/main",
        ["BUILD_REPOSITORY_PROVIDER"] = "TfsGit",
        ["BUILD_REPOSITORY_NAME"] = "NuGet-Client.Tools",
        ["SYSTEM_TEAMPROJECT"] = "internal",
        ["SYSTEM_COLLECTIONURI"] = "https://dev.azure.com/dnceng/",
    };

    internal static Dictionary<string, string> Properties(PipelineProcessResult result)
    {
        Assert.True(result.ExitCode == 0, result.Diagnostics);
        using var document = JsonDocument.Parse(result.Output);
        return document.RootElement.GetProperty("Properties").EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetString() ?? "");
    }

    internal static async Task<PipelineProcessResult> Run(string command, IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(command)
        {
            WorkingDirectory = RepoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) { start.ArgumentList.Add(argument); }
        foreach (var name in new[] { "DotnetPackageSkillsReleaseBuild", "DotNetFinalVersionKind", "OfficialBuild" })
        {
            start.Environment.Remove(name);
        }
        if (environment is not null)
        {
            foreach (var (name, value) in environment) { start.Environment[name] = value; }
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {command}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException($"{command} did not finish.");
        }
        return new PipelineProcessResult(process.ExitCode, await output, await error);
    }

    private static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "eng", "common", "build.cmd")))
            {
                return directory.FullName;
            }
        }
        throw new InvalidOperationException("Could not find the repository's Arcade entry point.");
    }
}

internal sealed record PipelineProcessResult(int ExitCode, string Output, string Error)
{
    internal string Diagnostics => Output + Environment.NewLine + Error;
}

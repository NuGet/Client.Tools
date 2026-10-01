using System.Diagnostics;

namespace DotnetPackageSkills.Tests;

public class PipelineVersionTests
{
    [Theory]
    [InlineData(false, false, "Manual", "refs/heads/main", "", "12345", "0.1.0-ci.12345")]
    [InlineData(false, false, "PullRequest", "refs/pull/42/merge", "42", "12345", "0.1.0-pr.42.12345")]
    [InlineData(true, false, "IndividualCI", "refs/heads/main", "", "12345", "0.1.0-preview.12345")]
    [InlineData(true, false, "Manual", "refs/heads/main", "", "12345", "0.1.0-preview.12345")]
    [InlineData(true, true, "Manual", "refs/heads/main", "", "12345", "0.1.0")]
    [InlineData(true, false, "IndividualCI", "refs/heads/main", "", "12346", "0.1.0-preview.12346")]
    [InlineData(true, false, "IndividualCI", "refs/heads/main", "", "10000000", "0.1.0-preview.10000000")]
    public async Task Pipeline_versions_distinguish_build_kinds(
        bool official, bool release, string reason, string branch, string pullRequest,
        string buildId, string expected)
    {
        var result = await Calculate("0.1.0", buildId, reason, branch, pullRequest, official, release);

        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Equal(expected, result.Output.Trim());
    }

    [Theory]
    [InlineData("0.1.0", "12345", "Manual", "refs/heads/main", "", false, true, "manual official build")]
    [InlineData("0.1.0", "12345", "IndividualCI", "refs/heads/main", "", true, true, "manual official build")]
    [InlineData("0.1.0", "12345", "Manual", "refs/heads/feature", "", true, true, "manual official build")]
    [InlineData("0.1.0", "12345", "PullRequest", "refs/pull/42/merge", "42", true, false, "cannot use the official signing path")]
    [InlineData("0.1.0", "$(Build.BuildId)", "Manual", "refs/heads/main", "", false, false, "BuildId must be a positive integer")]
    [InlineData("0.1.0", "12345", "PullRequest", "refs/pull/42/merge", "", false, false, "PullRequestNumber must be a positive integer")]
    [InlineData("0.1.0", "12345", "PullRequest", "refs/pull/42/merge", "042", false, false, "PullRequestNumber must be a positive integer")]
    [InlineData("01.0.0", "12345", "Manual", "refs/heads/main", "", false, false, "BaseVersion must be a three-part semantic version")]
    public async Task Invalid_pipeline_versions_fail_explicitly(
        string baseVersion, string buildId, string reason, string branch, string pullRequest,
        bool official, bool release, string diagnostic)
    {
        var result = await Calculate(baseVersion, buildId, reason, branch, pullRequest, official, release);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(diagnostic, result.Error);
        Assert.Empty(result.Output.Trim());
    }

    private static async Task<(int ExitCode, string Output, string Error)> Calculate(
        string baseVersion, string buildId, string reason, string branch, string pullRequest,
        bool official, bool release)
    {
        var start = new ProcessStartInfo("pwsh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
        {
            "-NoLogo", "-NoProfile", "-NonInteractive", "-File",
            Path.Combine(AppContext.BaseDirectory, "Pipeline", "Get-PackageVersion.ps1"),
            "-BaseVersion", baseVersion, "-BuildId", buildId,
            "-BuildReason", reason, "-SourceBranch", branch,
        })
        {
            start.ArgumentList.Add(argument);
        }
        if (pullRequest.Length > 0)
        {
            start.ArgumentList.Add("-PullRequestNumber");
            start.ArgumentList.Add(pullRequest);
        }
        if (official) { start.ArgumentList.Add("-Official"); }
        if (release) { start.ArgumentList.Add("-ReleaseBuild"); }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start PowerShell.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException("Package version calculation did not finish.");
        }
        return (process.ExitCode, await output, await error);
    }
}

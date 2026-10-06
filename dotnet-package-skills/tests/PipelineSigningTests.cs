namespace DotnetPackageSkills.Tests;

public class PipelineSigningTests
{
    [Theory]
    [InlineData("package", false)]
    [InlineData("package", true)]
    [InlineData("net8.0", false)]
    [InlineData("net10.0", true)]
    public async Task Explicit_signing_inputs_reject_missing_or_empty_package_and_loose_Dlls(string input, bool empty)
    {
        using var temporary = new TempDirectory();
        using var package = new PipelinePackageFixture();
        var metadata = PipelineTestContext.Properties(await PipelineTestContext.Evaluate("net8.0"));
        var artifacts = temporary.CreateDirectory("artifacts");
        var inputs = new Dictionary<string, string>
        {
            ["package"] = Path.Combine(temporary.CreateDirectory("artifacts", "packages", "Release", "Shipping"),
                $"dotnet-package-skills.{metadata["PackageVersion"]}.nupkg"),
        };
        File.Copy(package.PackagePath, inputs["package"]);
        foreach (var framework in new[] { "net8.0", "net10.0" })
        {
            inputs[framework] = Path.Combine(temporary.CreateDirectory("artifacts", "bin", "DotnetPackageSkills", "Release", framework),
                "dotnet-package-skills.dll");
            File.Copy(package.OwnedAssembly(framework), inputs[framework]);
        }
        if (empty) { File.WriteAllBytes(inputs[input], []); }
        else { File.Delete(inputs[input]); }

        var result = await PipelineTestContext.Run(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
        [
            "/c", Path.Combine(PipelineTestContext.RepoRoot, "eng", "common", "build.cmd"),
            "-sign", "-configuration", "Release", "/p:OfficialBuildId=",
            $"/p:ArtifactsDir={artifacts}{Path.DirectorySeparatorChar}", "/p:NETCORE_ENGINEERING_TELEMETRY=false",
        ]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(empty ? "empty signing input" : "missing signing input", result.Diagnostics);
        Assert.DoesNotContain("SignToolTask starting", result.Diagnostics);
    }
}

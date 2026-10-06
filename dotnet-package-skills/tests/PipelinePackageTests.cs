using System.IO.Compression;
using System.Reflection;
using System.Security;
using System.Text;

namespace DotnetPackageSkills.Tests;

public class PipelinePackageTests
{
    [Fact]
    public async Task Exact_package_installs_and_runs_on_both_runtimes()
    {
        using var fixture = new PipelinePackageFixture();

        var result = await fixture.Verify();

        Assert.True(result.ExitCode == 0, result.Diagnostics);
        Assert.Contains("on .NET 8 and .NET 10", result.Output);
    }

    [Theory]
    [InlineData("tools/net8.0/any/Unmapped.dll")]
    [InlineData("tools/net10.0/any/Unmapped.exe")]
    [InlineData("tools/net8.0/win-x64/Unmapped.dll")]
    [InlineData("tools/net9.0/any/Unmapped.dll")]
    public async Task Undeclared_or_nonportable_payloads_fail_before_install(string entry)
    {
        using var fixture = new PipelinePackageFixture();
        fixture.SetEntry(entry, File.ReadAllBytes(fixture.OwnedAssembly("net8.0")));

        var result = await fixture.Verify();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("payload", result.Diagnostics, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("tools/net8.0/any/SharpYaml.dll")]
    [InlineData("tools/net8.0/any/System.Collections.Immutable.dll")]
    [InlineData("tools/net10.0/any/System.CommandLine.dll")]
    [InlineData("tools/net10.0/any/fr/System.CommandLine.resources.dll")]
    public async Task Missing_declared_dependencies_fail_before_install(string entry)
    {
        using var fixture = new PipelinePackageFixture();
        fixture.RemoveEntry(entry);

        var result = await fixture.Verify();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("missing", result.Diagnostics, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(entry, result.Diagnostics);
    }

    [Theory]
    [InlineData("tools/net8.0/any/SharpYaml.dll")]
    [InlineData("tools/net10.0/any/dotnet-package-skills.dll")]
    public async Task Empty_signable_payloads_are_not_silently_skipped(string entry)
    {
        using var fixture = new PipelinePackageFixture();
        fixture.SetEntry(entry, []);

        var result = await fixture.Verify();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("empty", result.Diagnostics, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("tools/net8.0/any/SharpYaml.dll")]
    [InlineData("tools/net10.0/any/fr/System.CommandLine.resources.dll")]
    public async Task Redistributed_assembly_identity_cannot_change(string entry)
    {
        using var fixture = new PipelinePackageFixture();
        fixture.SetEntry(entry, File.ReadAllBytes(fixture.OwnedAssembly("net8.0")));

        var result = await fixture.Verify();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("identity", result.Diagnostics, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("net8.0")]
    [InlineData("net10.0")]
    public async Task Owned_package_payload_must_match_the_tested_loose_assembly(string framework)
    {
        using var fixture = new PipelinePackageFixture();
        var bytes = File.ReadAllBytes(fixture.OwnedAssembly(framework));
        bytes[^1] ^= 1;
        fixture.SetEntry($"tools/{framework}/any/dotnet-package-skills.dll", bytes);

        var result = await fixture.Verify();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("does not match", result.Diagnostics);
    }

    [Theory]
    [InlineData("<id>dotnet-package-skills</id>", "<id>renamed-tool</id>", "ID")]
    [InlineData("<authors>dotnet-package-skills contributors</authors>", "<authors>Changed</authors>", "authors")]
    [InlineData("<license type=\"expression\">MIT</license>", "<license type=\"expression\">Apache-2.0</license>", "MIT")]
    [InlineData("url=\"https://github.com/NuGet/Client.Tools\"", "url=\"https://example.invalid/tool\"", "origin")]
    [InlineData("<requireLicenseAcceptance>false</requireLicenseAcceptance>", "<requireLicenseAcceptance>true</requireLicenseAcceptance>", "license acceptance")]
    public async Task Package_identity_license_and_origin_metadata_are_preserved(
        string original, string replacement, string diagnostic)
    {
        using var fixture = new PipelinePackageFixture();
        fixture.SetEntry("dotnet-package-skills.nuspec", Encoding.UTF8.GetBytes(fixture.Nuspec.Replace(original, replacement)));

        var result = await fixture.Verify();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(diagnostic, result.Diagnostics, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Version_drift_is_rejected()
    {
        using var fixture = new PipelinePackageFixture();

        var result = await fixture.Verify(expectedVersion: "0.9.0");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("does not match", result.Diagnostics);
    }

    [Fact]
    public async Task Missing_readme_is_rejected()
    {
        using var fixture = new PipelinePackageFixture();
        fixture.RemoveEntry("README.md");

        var result = await fixture.Verify();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("README.md", result.Diagnostics);
    }

    [Fact]
    public async Task Duplicate_archive_entries_are_rejected()
    {
        using var fixture = new PipelinePackageFixture();
        using (var archive = ZipFile.Open(fixture.PackagePath, ZipArchiveMode.Update))
        {
            using var writer = new StreamWriter(archive.CreateEntry("README.md").Open());
            writer.Write("Duplicate README.");
        }

        var result = await fixture.Verify();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("duplicate", result.Diagnostics, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Archive_paths_cannot_escape_verification_scratch_space()
    {
        using var fixture = new PipelinePackageFixture();
        fixture.SetEntry("../escape.dll", File.ReadAllBytes(fixture.OwnedAssembly("net8.0")));

        var result = await fixture.Verify();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("path", result.Diagnostics, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unsigned_package_cannot_be_an_official_output()
    {
        using var fixture = new PipelinePackageFixture();

        var result = await fixture.Verify(requireSigned: true);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("NuGet signature", result.Diagnostics);
    }

    [Fact]
    public async Task Missing_package_is_rejected()
    {
        using var fixture = new PipelinePackageFixture();
        File.Delete(fixture.PackagePath);

        var result = await fixture.Verify();

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task A_corrupt_package_does_not_leave_extracted_payload_scratch_space()
    {
        using var fixture = new PipelinePackageFixture();
        File.WriteAllText(fixture.PackagePath, "Not a ZIP file.");
        var before = Directory.GetDirectories(Path.GetTempPath(), "dotnet-package-skills-payload-*").ToHashSet();

        var result = await fixture.Verify();

        Assert.NotEqual(0, result.ExitCode);
        var leaked = Directory.GetDirectories(Path.GetTempPath(), "dotnet-package-skills-payload-*")
            .Where(directory => !before.Contains(directory)).ToArray();
        try { Assert.Empty(leaked); }
        finally
        {
            foreach (var directory in leaked) { Directory.Delete(directory, recursive: true); }
        }
    }
}

internal sealed class PipelinePackageFixture : IDisposable
{
    private readonly TempDirectory temporary = new();
    private static readonly Lazy<Task<string>> StrongNameTool = new(async () =>
    {
        var properties = PipelineTestContext.Properties(await PipelineTestContext.Evaluate("net8.0"));
        return Path.Combine(properties["NuGetPackageRoot"], "sn", properties["SNVersion"], "sn.exe");
    });

    internal PipelinePackageFixture()
    {
        Version = typeof(PackageSkillsException).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion.Split('+', 2)[0];
        BuildOutputPath = temporary.CreateDirectory("build");
        PackagePath = temporary.Combine($"dotnet-package-skills.{Version}.nupkg");
        foreach (var framework in new[] { "net8.0", "net10.0" })
        {
            var source = Path.Combine(PipelineTestContext.RepoRoot, "artifacts", "bin", "DotnetPackageSkills", "Release", framework);
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, file);
                if (relative.StartsWith("publish" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) { continue; }
                if (Path.GetExtension(file) is not (".dll" or ".json")) { continue; }
                var copy = Path.Combine(BuildOutputPath, framework, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(file, copy);
            }
        }
        using (var archive = ZipFile.Open(PackagePath, ZipArchiveMode.Create))
        {
            foreach (var framework in new[] { "net8.0", "net10.0" })
            {
                var directory = Path.Combine(BuildOutputPath, framework);
                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                {
                    archive.CreateEntryFromFile(file, $"tools/{framework}/any/{Path.GetRelativePath(directory, file).Replace('\\', '/')}");
                }
                WriteEntry(archive, $"tools/{framework}/any/DotnetToolSettings.xml",
                    "<DotNetCliTool Version=\"1\"><Commands><Command Name=\"dotnet-package-skills\" " +
                    "EntryPoint=\"dotnet-package-skills.dll\" Runner=\"dotnet\" /></Commands></DotNetCliTool>");
            }
            WriteEntry(archive, "dotnet-package-skills.nuspec", Nuspec);
            WriteEntry(archive, "README.md", "Package verification fixture.");
        }
    }

    internal string Version { get; }
    internal string BuildOutputPath { get; }
    internal string PackagePath { get; }
    internal string Nuspec =>
        "<package><metadata><id>dotnet-package-skills</id>" +
        $"<version>{SecurityElement.Escape(Version)}</version>" +
        "<authors>dotnet-package-skills contributors</authors>" +
        "<description>Package verification fixture.</description>" +
        "<requireLicenseAcceptance>false</requireLicenseAcceptance>" +
        "<license type=\"expression\">MIT</license><readme>README.md</readme>" +
        "<repository type=\"git\" url=\"https://github.com/NuGet/Client.Tools\" commit=\"" + new string('a', 40) + "\" />" +
        "<packageTypes><packageType name=\"DotnetTool\" /></packageTypes></metadata></package>";

    internal string OwnedAssembly(string framework) => Path.Combine(BuildOutputPath, framework, "dotnet-package-skills.dll");

    internal async Task<PipelineProcessResult> Verify(bool requireSigned = false, string? expectedVersion = null)
    {
        var arguments = new List<string>
        {
            "-NoLogo", "-NoProfile", "-NonInteractive", "-File", PipelineTestContext.PipelinePath("Verify-Package.ps1"),
            "-PackagePath", PackagePath, "-ExpectedVersion", expectedVersion ?? Version,
            "-BuildOutputPath", BuildOutputPath,
            "-DotnetPath", PipelineTestContext.DotnetPath,
            "-StrongNameToolPath", await StrongNameTool.Value,
        };
        if (requireSigned) { arguments.Add("-RequireSigned"); }
        return await PipelineTestContext.Run("pwsh", arguments);
    }

    internal void SetEntry(string name, byte[] bytes)
    {
        using var archive = ZipFile.Open(PackagePath, ZipArchiveMode.Update);
        archive.GetEntry(name)?.Delete();
        using var stream = archive.CreateEntry(name).Open();
        stream.Write(bytes);
    }

    internal void RemoveEntry(string name)
    {
        using var archive = ZipFile.Open(PackagePath, ZipArchiveMode.Update);
        archive.GetEntry(name)!.Delete();
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    public void Dispose() => temporary.Dispose();
}

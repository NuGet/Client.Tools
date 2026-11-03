using System.Text.RegularExpressions;
using DotnetPackageSkills.Cli;
using DotnetPackageSkills.Infrastructure;
using DotnetPackageSkills.Skills;

namespace DotnetPackageSkills.Tests;

[Collection("Console output")]
public class DestinationCommandLineTests
{
    private sealed class FakeDotnet(string? packageOutput = null) : IProcessRunner
    {
        public List<(IReadOnlyList<string> Arguments, string? WorkingDirectory)> Invocations { get; } = [];

        public ProcessResult Run(string fileName, IReadOnlyList<string> arguments, string? workingDirectory = null)
        {
            Invocations.Add((arguments, workingDirectory));
            Assert.Contains("list", arguments);
            return new ProcessResult(0, packageOutput ??
                """
                {"projects":[{"frameworks":[{"framework":"net8.0","topLevelPackages":[
                  {"id":"Mockly","resolvedVersion":"1.0.0"}
                ]}]}]}
                """, string.Empty);
        }
    }

    private static void Invoke(string workingDirectory, IProcessRunner runner, params string[] args) =>
        Invoke(workingDirectory, runner, args, destinationAncestors: null);

    private static void Invoke(
        string workingDirectory, IProcessRunner runner, string[] args, IEnumerable<DirectoryInfo>? destinationAncestors)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, InvokeCapturingConsole(workingDirectory, runner, output, error, args, destinationAncestors));
        Assert.Empty(error.ToString());
    }

    private static int InvokeCapturingConsole(
        string workingDirectory, IProcessRunner runner, TextWriter output, TextWriter error, string[] args,
        IEnumerable<DirectoryInfo>? destinationAncestors = null)
    {
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            return CommandLineBuilder.Invoke(args, runner, output, error, workingDirectory, destinationAncestors);
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
        }
    }

    [Theory]
    [InlineData(".git")]
    [InlineData("Repo.sln")]
    [InlineData("Repo.slnx")]
    public void All_commands_share_the_ancestor_destination_and_manifest(string marker)
    {
        using var temp = new TempDirectory();
        temp.CreateFile(marker);
        var invocation = temp.CreateDirectory("src", "App");
        var package = temp.CreatePackageWithSkill("Mockly", "1.0.0", "usage");
        var cache = Path.GetRelativePath(invocation, temp.Combine("packages"));
        var destination = temp.Combine(".agents", "skills");
        var skill = Path.Combine(destination, "usage", "SKILL.md");
        var runner = new FakeDotnet();

        Invoke(invocation, runner, "install", "-p", "Mockly@1.0.0", "--global-packages", cache);
        Assert.True(File.Exists(skill));
        var manifest = File.ReadAllBytes(Path.Combine(destination, InstallManifest.FileName));
        File.WriteAllText(Path.Combine(package, "skills", "usage", "SKILL.md"), "updated");

        // A bare ID must find the ancestor manifest, even with no project in the invocation directory.
        Invoke(invocation, runner, "list", "-p", "Mockly", "--global-packages", cache);
        Assert.NotEqual("updated", File.ReadAllText(skill));
        Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(destination, InstallManifest.FileName)));
        Invoke(invocation, runner, "install", "-p", "Mockly", "--global-packages", cache, "--dry-run");
        Assert.NotEqual("updated", File.ReadAllText(skill));
        Invoke(invocation, runner, "install", "-p", "Mockly", "--global-packages", cache, "--interactive");
        Assert.NotEqual("updated", File.ReadAllText(skill));
        Invoke(invocation, runner, "install", "-p", "Mockly", "--global-packages", cache);
        Assert.Equal("updated", File.ReadAllText(skill));

        Invoke(invocation, runner, "uninstall", "--dry-run");
        Assert.True(File.Exists(skill));
        Invoke(invocation, runner, "uninstall");
        Assert.False(File.Exists(skill));
        Invoke(invocation, runner, "uninstall", "--interactive");

        Assert.False(Directory.Exists(Path.Combine(invocation, ".agents")));
        Assert.Empty(runner.Invocations);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("install")]
    public void Discovery_and_dry_run_do_not_create_default_destination_folders(string command)
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory(".git");
        var invocation = temp.CreateDirectory("src", "App");
        temp.CreatePackageWithSkill("Mockly", "1.0.0", "usage");
        var args = new List<string>
        {
            command, "-p", "Mockly@1.0.0", "--global-packages", temp.Combine("packages"),
        };
        if (command == "install") { args.Add("--dry-run"); }

        Invoke(invocation, new FakeDotnet(), [.. args]);

        Assert.False(Directory.Exists(temp.Combine(".agents")));
        Assert.False(Directory.Exists(Path.Combine(invocation, ".agents")));
    }

    [Theory]
    [InlineData(".agents/skills")]
    [InlineData(".claude/skills")]
    [InlineData("absolute")]
    public void Explicit_destinations_override_discovery_for_all_commands(string destination)
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory(".git");
        var invocation = temp.CreateDirectory("src", "App");
        temp.CreatePackageWithSkill("Mockly", "1.0.0", "usage");
        if (destination == "absolute") { destination = temp.Combine("custom-skills"); }
        var resolved = Path.GetFullPath(destination, invocation);
        var runner = new FakeDotnet();

        Invoke(invocation, runner, "install", "-p", "Mockly@1.0.0", "-d", destination,
            "--global-packages", temp.Combine("packages"));
        Assert.True(File.Exists(Path.Combine(resolved, "usage", "SKILL.md")));
        Invoke(invocation, runner, "list", "-p", "Mockly", "-d", destination,
            "--global-packages", temp.Combine("packages"));
        Invoke(invocation, runner, "uninstall", "-d", destination);

        Assert.False(File.Exists(Path.Combine(resolved, "usage", "SKILL.md")));
        Assert.False(Directory.Exists(temp.Combine(".agents")));
        Assert.Empty(runner.Invocations);
    }

    [Theory]
    [InlineData("list", false, false)]
    [InlineData("list", true, false)]
    [InlineData("install", true, false)]
    [InlineData("install", false, true)]
    public void Source_and_cache_paths_keep_using_the_invocation_directory(
        string command, bool explicitSource, bool explicitDestination)
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory(".git");
        temp.CreateFile("Repo.sln");
        var invocation = temp.CreateDirectory("src", "App");
        var target = temp.CreateFile("src/App/App.csproj");
        temp.CreatePackageWithSkill("Mockly", "1.0.0", "usage");
        var runner = new FakeDotnet();
        var args = new List<string>
        {
            command, "--global-packages", Path.GetRelativePath(invocation, temp.Combine("packages")),
        };
        if (explicitSource) { args.AddRange(["--source", "App.csproj"]); }
        if (explicitDestination) { args.AddRange(["--destination", temp.Combine(".agents", "skills")]); }

        Invoke(invocation, runner, [.. args]);

        Assert.Equal(command == "install", File.Exists(temp.Combine(".agents", "skills", "usage", "SKILL.md")));
        var call = Assert.Single(runner.Invocations);
        Assert.Contains(target, call.Arguments);
        Assert.Equal(invocation, call.WorkingDirectory);
    }

    [Theory]
    [InlineData("install", ".git")]
    [InlineData("install", "App.sln")]
    [InlineData("install", "App.slnx")]
    [InlineData("install", null)]
    [InlineData("uninstall", ".git")]
    [InlineData("uninstall", "App.sln")]
    [InlineData("uninstall", "App.slnx")]
    [InlineData("uninstall", null)]
    public void Implicit_invocation_local_scope_allows_full_install_and_stale_cleanup(string command, string? marker)
    {
        using var temp = new TempDirectory();
        DirectoryInfo[] destinationAncestors = [new(temp.Path)];
        var project = temp.CreateFile("App.csproj");
        if (marker == ".git") { temp.CreateDirectory(marker); }
        else if (marker is not null) { temp.CreateFile(marker); }

        temp.CreatePackageWithSkill("Mockly", "1.0.0", "usage");
        var destination = temp.Combine(".agents", "skills");
        var runner = new FakeDotnet();
        if (command == "uninstall")
        {
            temp.CreatePackageWithSkill("Old.Package", "1.0.0", "old-skill");
            Invoke(temp.Path, runner,
                ["install", "--package", "Mockly@1.0.0", "Old.Package@1.0.0", "--global-packages", temp.Combine("packages")],
                destinationAncestors);
            Assert.Empty(runner.Invocations);
            Assert.True(File.Exists(Path.Combine(destination, "old-skill", "SKILL.md")));
            Invoke(temp.Path, runner, ["uninstall", "--stale"], destinationAncestors);
        }
        else
        {
            Invoke(temp.Path, runner, ["install", "--global-packages", temp.Combine("packages")], destinationAncestors);
        }

        var target = marker is "App.sln" or "App.slnx" ? temp.Combine(marker) : project;
        var call = Assert.Single(runner.Invocations);
        Assert.Equal(["list", target, "package", "--no-restore", "--format", "json"], call.Arguments);
        Assert.Equal(temp.Path, call.WorkingDirectory);
        Assert.True(File.Exists(Path.Combine(destination, "usage", "SKILL.md")));
        Assert.False(Directory.Exists(Path.Combine(destination, "old-skill")));
        Assert.Equal(new TrackedSkill("mockly", "1.0.0", "usage"),
            Assert.Single(InstallManifest.Load(destination).EnumerateSkills()));
    }

    [Theory]
    [InlineData("install")]
    [InlineData("uninstall")]
    public void Fallback_command_tests_ignore_markers_outside_the_injected_discovery_boundary(string command)
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory(".git");
        var invocation = temp.CreateDirectory("src", "App");
        var target = temp.CreateFile("src/App/App.csproj");
        temp.CreatePackageWithSkill("Mockly", "1.0.0", "usage");
        temp.CreatePackageWithSkill("Old.Package", "1.0.0", "old-skill");
        DirectoryInfo[] destinationAncestors = [new(invocation)];
        var runner = new FakeDotnet();
        var destination = Path.Combine(invocation, ".agents", "skills");
        if (command == "uninstall")
        {
            Invoke(invocation, runner,
                ["install", "-p", "Mockly@1.0.0", "Old.Package@1.0.0", "--global-packages", temp.Combine("packages")],
                destinationAncestors);
            Assert.Empty(runner.Invocations);
            Invoke(invocation, runner, ["uninstall", "--stale"], destinationAncestors);
        }
        else
        {
            Invoke(invocation, runner, ["install", "--global-packages", temp.Combine("packages")], destinationAncestors);
        }

        var call = Assert.Single(runner.Invocations);
        Assert.Contains(target, call.Arguments);
        Assert.Equal(invocation, call.WorkingDirectory);
        Assert.True(File.Exists(Path.Combine(destination, "usage", "SKILL.md")));
        Assert.False(Directory.Exists(Path.Combine(destination, "old-skill")));
        Assert.False(Directory.Exists(temp.Combine(".agents")));
        Assert.Equal(new TrackedSkill("mockly", "1.0.0", "usage"),
            Assert.Single(InstallManifest.Load(destination).EnumerateSkills()));
    }

    [Theory]
    [InlineData("install", "")]
    [InlineData("install", "--dry-run")]
    [InlineData("install", "--interactive")]
    [InlineData("uninstall", "")]
    [InlineData("uninstall", "--dry-run")]
    [InlineData("uninstall", "--interactive")]
    public void Implicit_project_scope_preserves_sibling_packages_in_a_shared_ancestor_manifest(string command, string option)
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory(".git");
        temp.CreateFile("Repo.sln");
        temp.CreateFile("src/App1/App1.csproj");
        temp.CreateFile("src/App2/App2.csproj");
        temp.CreatePackageWithSkill("Mockly", "1.0.0", "usage");
        temp.CreatePackageWithSkill("Sibling.Package", "1.0.0", "sibling");
        var runner = new FakeDotnet();
        Invoke(temp.Path, runner, "install", "--package", "Mockly@1.0.0", "Sibling.Package@1.0.0",
            "--global-packages", temp.Combine("packages"));
        var destination = temp.Combine(".agents", "skills");
        var manifest = File.ReadAllBytes(Path.Combine(destination, InstallManifest.FileName));
        var sibling = File.ReadAllBytes(Path.Combine(destination, "sibling", "SKILL.md"));
        var args = new List<string> { command };
        if (command == "uninstall") { args.Add("--stale"); }
        else { args.AddRange(["--global-packages", temp.Combine("packages")]); }
        if (option.Length > 0) { args.Add(option); }

        Invoke(temp.Combine("src", "App1"), runner, [.. args]);

        Assert.Single(runner.Invocations);
        Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(destination, InstallManifest.FileName)));
        Assert.Equal(sibling, File.ReadAllBytes(Path.Combine(destination, "sibling", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(destination, "usage", "SKILL.md")));
    }

    [Theory]
    [InlineData("install", false)]
    [InlineData("install", true)]
    [InlineData("uninstall", false)]
    [InlineData("uninstall", true)]
    public void Implicit_project_scope_compares_versions_only_for_referenced_packages(string command, bool dryRun)
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory(".git");
        var target = temp.CreateFile("src/App1/App1.csproj");
        temp.CreateFile("src/App2/App2.csproj");
        temp.CreatePackageWithSkill("Mockly", "0.5.0", "usage", "dropped");
        temp.CreatePackageWithSkill("Mockly", "1.0.0", "usage", "new-skill");
        temp.CreatePackageWithSkill("Sibling.Package", "1.0.0", "sibling");
        var runner = new FakeDotnet();
        Invoke(temp.Path, runner, "install", "--package", "Mockly@0.5.0", "Sibling.Package@1.0.0",
            "--global-packages", temp.Combine("packages"));
        var destination = temp.Combine(".agents", "skills");
        var manifest = File.ReadAllBytes(Path.Combine(destination, InstallManifest.FileName));
        var sibling = File.ReadAllBytes(Path.Combine(destination, "sibling", "SKILL.md"));
        var args = new List<string> { command };
        if (command == "uninstall") { args.Add("--stale"); }
        else { args.AddRange(["--global-packages", temp.Combine("packages")]); }
        if (dryRun) { args.Add("--dry-run"); }

        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, InvokeCapturingConsole(Path.GetDirectoryName(target)!, runner, output, error, [.. args]));

        Assert.Empty(error.ToString());
        Assert.DoesNotContain("sibling", output.ToString(), StringComparison.OrdinalIgnoreCase);
        var call = Assert.Single(runner.Invocations);
        Assert.Contains(target, call.Arguments);
        Assert.Equal(Path.GetDirectoryName(target), call.WorkingDirectory);
        Assert.Equal(sibling, File.ReadAllBytes(Path.Combine(destination, "sibling", "SKILL.md")));
        Assert.Contains(new TrackedSkill("sibling.package", "1.0.0", "sibling"),
            InstallManifest.Load(destination).EnumerateSkills());
        Assert.Equal(dryRun, Directory.Exists(Path.Combine(destination, "dropped")));
        Assert.Equal(command == "install" || dryRun, Directory.Exists(Path.Combine(destination, "usage")));
        Assert.Equal(command == "install" && !dryRun, Directory.Exists(Path.Combine(destination, "new-skill")));
        if (dryRun) { Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(destination, InstallManifest.FileName))); }
    }

    [Theory]
    [InlineData("install", "")]
    [InlineData("install", "--dry-run")]
    [InlineData("install", "--interactive")]
    [InlineData("uninstall", "")]
    [InlineData("uninstall", "--dry-run")]
    [InlineData("uninstall", "--interactive")]
    public void An_empty_automatic_project_scope_preserves_every_shared_package(string command, string option)
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory(".git");
        var target = temp.CreateFile("src/App/App.csproj");
        temp.CreatePackageWithSkill("Mockly", "1.0.0", "usage");
        var runner = new FakeDotnet(
            """{"projects":[{"frameworks":[{"framework":"net8.0","topLevelPackages":[]}]}]}""");
        Invoke(temp.Path, runner, "install", "-p", "Mockly@1.0.0",
            "--global-packages", temp.Combine("packages"));
        var destination = temp.Combine(".agents", "skills");
        var manifest = File.ReadAllBytes(Path.Combine(destination, InstallManifest.FileName));
        var skill = File.ReadAllBytes(Path.Combine(destination, "usage", "SKILL.md"));
        var args = new List<string> { command };
        if (command == "uninstall") { args.Add("--stale"); }
        else { args.AddRange(["--global-packages", temp.Combine("packages")]); }
        if (option.Length > 0) { args.Add(option); }

        Invoke(Path.GetDirectoryName(target)!, runner, [.. args]);

        Assert.Single(runner.Invocations);
        Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(destination, InstallManifest.FileName)));
        Assert.Equal(skill, File.ReadAllBytes(Path.Combine(destination, "usage", "SKILL.md")));
    }

    [Fact]
    public void Interactive_project_install_checks_stale_versions_only_for_referenced_packages()
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory(".git");
        var target = temp.CreateFile("src/App/App.csproj");
        temp.CreatePackageWithSkill("Mockly", "0.5.0", "usage");
        temp.CreatePackageWithSkill("Mockly", "1.0.0", "usage");
        temp.CreatePackageWithSkill("Sibling.Package", "1.0.0", "sibling");
        var runner = new FakeDotnet();
        Invoke(temp.Path, runner, "install", "-p", "Mockly@0.5.0", "Sibling.Package@1.0.0",
            "--global-packages", temp.Combine("packages"));
        var destination = temp.Combine(".agents", "skills");
        var manifest = File.ReadAllBytes(Path.Combine(destination, InstallManifest.FileName));
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(1, InvokeCapturingConsole(Path.GetDirectoryName(target)!, runner, output, error,
            ["install", "--interactive", "--global-packages", temp.Combine("packages")]));

        Assert.Contains("usage", error.ToString());
        Assert.DoesNotContain("sibling", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Which skills", output.ToString());
        Assert.Single(runner.Invocations);
        Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(destination, InstallManifest.FileName)));
    }

    [Theory]
    [InlineData("--source")]
    [InlineData("--destination")]
    public void Explicit_scope_allows_stale_comparison_against_the_shared_manifest(string option)
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory(".git");
        var solution = temp.CreateFile("Repo.sln");
        var invocation = temp.CreateDirectory("src", "App1");
        temp.CreateFile("src/App1/App1.csproj");
        temp.CreatePackageWithSkill("Mockly", "1.0.0", "usage");
        temp.CreatePackageWithSkill("Sibling.Package", "1.0.0", "sibling");
        var runner = new FakeDotnet();
        Invoke(temp.Path, runner, "install", "--package", "Mockly@1.0.0", "Sibling.Package@1.0.0",
            "--global-packages", temp.Combine("packages"));
        var destination = temp.Combine(".agents", "skills");
        var value = option == "--source" ? Path.GetRelativePath(invocation, solution) : destination;

        Invoke(invocation, runner, "uninstall", "--stale", option, value);

        Assert.Single(runner.Invocations);
        Assert.True(File.Exists(Path.Combine(destination, "usage", "SKILL.md")));
        Assert.False(File.Exists(Path.Combine(destination, "sibling", "SKILL.md")));
    }

    [Theory]
    [InlineData("install")]
    [InlineData("list")]
    [InlineData("uninstall")]
    public void Destination_help_describes_default_discovery_and_explicit_paths(string command)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(0, CommandLineBuilder.Invoke([command, "--help"], output, error));

        Assert.Empty(error.ToString());
        Assert.Contains("nearest", output.ToString());
        Assert.Contains(".slnx", output.ToString());
        Assert.Contains("Explicit relative paths", output.ToString());
    }

    [Fact]
    public void Stale_help_explains_automatic_preservation_and_explicit_authoritative_scope()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(0, CommandLineBuilder.Invoke(["uninstall", "--help"], output, error));

        Assert.Empty(error.ToString());
        var help = Regex.Replace(output.ToString(), @"\s+", " ");
        Assert.Contains("When source and destination are automatic and the destination is above the current directory", help);
        Assert.Contains("packages absent from the source are preserved", help);
        Assert.Contains("An explicit --source or --destination also includes unreferenced packages", help);
    }
}

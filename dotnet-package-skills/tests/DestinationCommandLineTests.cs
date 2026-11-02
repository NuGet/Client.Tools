using DotnetPackageSkills.Cli;
using DotnetPackageSkills.Infrastructure;
using DotnetPackageSkills.Skills;

namespace DotnetPackageSkills.Tests;

public class DestinationCommandLineTests
{
    private sealed class FakeDotnet : IProcessRunner
    {
        public List<(IReadOnlyList<string> Arguments, string? WorkingDirectory)> Invocations { get; } = [];

        public ProcessResult Run(string fileName, IReadOnlyList<string> arguments, string? workingDirectory = null)
        {
            Invocations.Add((arguments, workingDirectory));
            Assert.Contains("list", arguments);
            return new ProcessResult(0,
                """
                {"projects":[{"frameworks":[{"framework":"net8.0","topLevelPackages":[
                  {"id":"Mockly","resolvedVersion":"1.0.0"}
                ]}]}]}
                """, string.Empty);
        }
    }

    private static void Invoke(string workingDirectory, IProcessRunner runner, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, CommandLineBuilder.Invoke(args, runner, output, error, workingDirectory));
        Assert.Empty(error.ToString());
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
    [InlineData(false)]
    [InlineData(true)]
    public void Source_and_cache_paths_keep_using_the_invocation_directory(bool explicitSource)
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
            "install", "--global-packages", Path.GetRelativePath(invocation, temp.Combine("packages")),
        };
        if (explicitSource) { args.AddRange(["--source", "App.csproj"]); }

        Invoke(invocation, runner, [.. args]);

        Assert.True(File.Exists(temp.Combine(".agents", "skills", "usage", "SKILL.md")));
        var call = Assert.Single(runner.Invocations);
        Assert.Contains(target, call.Arguments);
        Assert.Equal(invocation, call.WorkingDirectory);
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
}

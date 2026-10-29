using DotnetPackageSkills.Cli;
using DotnetPackageSkills.Skills;

namespace DotnetPackageSkills.Tests;

[Collection("Console output")]
public class MultiVersionCommandLineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Install_copies_valid_skills_and_returns_one_for_collisions(bool dryRun)
    {
        using var temp = new TempDirectory();
        temp.CreatePackageWithSkill("A", "1.0.0", "shared", "valid-a");
        temp.CreatePackageWithSkill("B", "2.0.0", "shared", "valid-b");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var args = Args(temp, "install", "A@1.0.0", "B@2.0.0");
        if (dryRun) { args.Add("--dry-run"); }

        var code = Invoke([.. args], output, error);

        Assert.Equal(1, code);
        Assert.Contains(dryRun ? "Would copy 2 skills:" : "Copied 2 skills:", output.ToString());
        Assert.Contains("Skipped 2 colliding skills:", output.ToString());
        Assert.Contains("error:", error.ToString());
        Assert.Contains("shared", error.ToString());
        Assert.Contains("A 1.0.0", error.ToString());
        Assert.Contains("B 2.0.0", error.ToString());
        Assert.DoesNotContain("No skills were changed", output.ToString() + error);
        Assert.False(Directory.Exists(temp.Combine("dest", "shared")));
        Assert.Equal(!dryRun, File.Exists(temp.Combine("dest", "valid-a", "SKILL.md")));
        Assert.Equal(!dryRun, File.Exists(temp.Combine("dest", "valid-b", "SKILL.md")));
        Assert.Equal(dryRun ? 0 : 2, InstallManifest.Load(temp.Combine("dest")).EnumerateSkills().Count());
    }

    [Fact]
    public void Multi_version_warning_without_collisions_returns_zero()
    {
        using var temp = new TempDirectory();
        temp.CreatePackageWithSkill("A", "1.0.0", "skill-x");
        temp.CreatePackageWithSkill("A", "2.0.0", "skill-y");
        temp.CreateDirectory("packages", "a", "3.0.0");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var code = Invoke(
            [.. Args(temp, "install", "A@1.0.0", "A@2.0.0", "A@3.0.0")], output, error);

        Assert.Equal(0, code);
        Assert.Empty(error.ToString());
        Assert.Contains("Copied 2 skills:", output.ToString());
        Assert.Contains("Warning:", output.ToString());
        Assert.Contains("multiple versions (1.0.0, 2.0.0)", output.ToString());
        Assert.Contains("Ignored 1 package", output.ToString());
        Assert.Contains("A 3.0.0", output.ToString());
        Assert.Equal(2, InstallManifest.Load(temp.Combine("dest")).EnumerateSkills().Count());
    }

    [Fact]
    public void List_reports_collisions_without_writes_or_install_failure()
    {
        using var temp = new TempDirectory();
        temp.CreatePackageWithSkill("A", "1.0.0", "shared", "valid");
        temp.CreatePackageWithSkill("A", "2.0.0", "shared");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var code = Invoke(
            [.. Args(temp, "list", "A@1.0.0", "A@2.0.0")], output, error);

        Assert.Equal(0, code);
        Assert.Contains("Found 1 skill:", output.ToString());
        Assert.Contains("Skipped 2 colliding skills:", output.ToString());
        Assert.Contains("shared", error.ToString());
        Assert.DoesNotContain("Would copy", output.ToString());
        Assert.False(Directory.Exists(temp.Combine("dest")));
    }

    [Fact]
    public void All_colliding_interactive_input_reports_errors_without_a_picker()
    {
        using var temp = new TempDirectory();
        temp.CreatePackageWithSkill("A", "1.0.0", "shared");
        temp.CreatePackageWithSkill("B", "1.0.0", "shared");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var code = Invoke(
            [.. Args(temp, "install", "A@1.0.0", "B@1.0.0"), "--interactive"], output, error);

        Assert.Equal(1, code);
        Assert.Contains("Nothing new to install.", output.ToString());
        Assert.Contains("shared", error.ToString());
        Assert.DoesNotContain("interactive terminal", error.ToString());
        Assert.False(Directory.Exists(temp.Combine("dest")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void All_ignored_input_succeeds_without_opening_a_picker(bool interactive)
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory("packages", "a", "1.0.0");
        temp.CreateDirectory("packages", "a", "2.0.0");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var args = Args(temp, "install", "A@1.0.0", "A@2.0.0");
        if (interactive) { args.Add("--interactive"); }

        var code = Invoke([.. args], output, error);

        Assert.Equal(0, code);
        Assert.Empty(error.ToString());
        Assert.Contains("Ignored 2 packages", output.ToString());
        Assert.DoesNotContain("Warning:", output.ToString());
        Assert.False(Directory.Exists(temp.Combine("dest")));
    }

    private static List<string> Args(TempDirectory temp, string command, params string[] coordinates) =>
    [
        command, "--package", .. coordinates, "--destination", temp.Combine("dest"),
        "--global-packages", temp.Combine("packages"),
    ];

    private static int Invoke(string[] args, TextWriter output, TextWriter error)
    {
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            return CommandLineBuilder.Invoke(args, output, error);
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
        }
    }
}

[CollectionDefinition("Console output", DisableParallelization = true)]
public sealed class ConsoleOutputCollection;

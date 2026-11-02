using DotnetPackageSkills.Infrastructure;

namespace DotnetPackageSkills.Tests;

public class DestinationLocatorTests
{
    [Theory]
    [InlineData(".git", true)]
    [InlineData(".git", false)]
    [InlineData("App.sln", false)]
    [InlineData("App.slnx", false)]
    [InlineData("App.SLN", false)]
    [InlineData("App.SLNX", false)]
    public void Finds_markers_in_the_current_directory_or_an_ancestor(string marker, bool directory)
    {
        using var temp = new TempDirectory();
        if (directory) { temp.CreateDirectory(marker); }
        else { temp.CreateFile(marker); }
        var nested = temp.CreateDirectory("src", "App");

        Assert.Equal(temp.Combine(".agents", "skills"), DestinationLocator.Resolve(null, temp.Path));
        Assert.Equal(temp.Combine(".agents", "skills"), DestinationLocator.Resolve(null, nested));
        Assert.False(Directory.Exists(temp.Combine(".agents")));
    }

    [Theory]
    [InlineData(".git", true)]
    [InlineData(".git", false)]
    [InlineData("App.sln", false)]
    [InlineData("App.slnx", false)]
    public void The_nearest_marker_wins_even_over_an_outer_git_directory(string marker, bool directory)
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory(".git");
        var inner = temp.CreateDirectory("inner");
        if (directory) { temp.CreateDirectory("inner", marker); }
        else { temp.CreateFile($"inner/{marker}"); }
        var nested = temp.CreateDirectory("inner", "src", "App");

        Assert.Equal(Path.Combine(inner, ".agents", "skills"), DestinationLocator.Resolve(null, nested));
    }

    [Theory]
    [InlineData("App.csproj")]
    [InlineData("App.sln.backup")]
    [InlineData(".agents/skills/.dotnet-package-skills.json")]
    [InlineData("child/App.sln")]
    public void Nonmarkers_do_not_replace_the_invocation_directory_fallback(string nonmarker)
    {
        using var temp = new TempDirectory();
        temp.CreateFile(nonmarker);
        var invocation = temp.CreateDirectory("src", "App");

        Assert.Equal(Path.Combine(invocation, ".agents", "skills"),
            DestinationLocator.ResolveDefault(invocation, [new(invocation), new(temp.Path)]));
    }

    [Theory]
    [InlineData(".agents/skills")]
    [InlineData(".claude/skills")]
    [InlineData("../custom-skills")]
    public void Explicit_relative_destinations_use_the_invocation_directory(string destination)
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory(".git");
        var invocation = temp.CreateDirectory("src", "App");

        Assert.Equal(Path.GetFullPath(destination, invocation), DestinationLocator.Resolve(destination, invocation));
    }

    [Fact]
    public void An_explicit_absolute_destination_bypasses_discovery()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("custom-skills");

        Assert.Equal(destination, DestinationLocator.Resolve(destination, temp.Combine("nonexistent")));
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public void A_solution_named_directory_is_not_a_marker()
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory("App.sln");
        var invocation = temp.CreateDirectory("src");

        Assert.Equal(Path.Combine(invocation, ".agents", "skills"),
            DestinationLocator.ResolveDefault(invocation, [new(invocation), new(temp.Path)]));
    }

    [Fact]
    public void An_unreadable_directory_does_not_silently_fall_back()
    {
        using var temp = new TempDirectory();

        Assert.Throws<DirectoryNotFoundException>(() =>
            DestinationLocator.Resolve(null, temp.Combine("nonexistent")));
    }
}

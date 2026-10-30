using DotnetPackageSkills.Infrastructure;
using DotnetPackageSkills.NuGet;

namespace DotnetPackageSkills.Tests;

public class SkillCandidateClassifierTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void All_owners_of_a_colliding_destination_are_skipped(bool samePackage, bool reverse)
    {
        using var temp = new TempDirectory();
        temp.CreatePackageWithSkill("A", "1.0.0", "shared", "only-a");
        var other = samePackage ? "A" : "B";
        temp.CreatePackageWithSkill(other, "2.0.0", "SHARED", "only-other");
        temp.CreatePackageWithSkill("C", "3.0.0", "Shared");
        var coordinates = new[] { "A@1.0.0", $"{other}@2.0.0", "C@3.0.0" };
        if (reverse) { Array.Reverse(coordinates); }

        var result = Discover(temp, coordinates);

        Assert.Equal(["only-a", "only-other"], result.Skills.Select(skill => skill.RelativePath).Order());
        Assert.Equal(3, result.Skipped.Count);
        Assert.Equal(new[] { "A", other, "C" }.Order(), result.Skipped.Select(skill => skill.PackageId).Order());
        Assert.DoesNotContain(result.Skipped, skill => skill.Reason.Contains("selected first", StringComparison.Ordinal));
        Assert.Equal(3, Assert.Single(result.Errors).Candidates.Count);
    }

    [Fact]
    public void Equivalent_coordinates_do_not_self_collide()
    {
        using var temp = new TempDirectory();
        temp.CreatePackageWithSkill("A", "1.0.0-beta.1", "skill-x");

        var result = Discover(temp, "A@1.0.0-BETA.1+Build.A", "a@01.0.0.0-beta.1+Build.B");

        Assert.Single(result.Skills);
        Assert.Single(result.ResolvedPackages);
        Assert.Empty(result.Skipped);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Disjoint_skill_versions_warn_and_remain_installable()
    {
        using var temp = new TempDirectory();
        temp.CreatePackageWithSkill("A", "1.0.0", "skill-x");
        temp.CreatePackageWithSkill("A", "2.0.0", "skill-y");

        var result = Discover(temp, "A@2.0.0", "a@1.0.0");

        Assert.Equal(["skill-x", "skill-y"], result.Skills.Select(skill => skill.SkillName).Order());
        Assert.Empty(result.Skipped);
        Assert.Equal(["1.0.0", "2.0.0"], Assert.Single(result.Warnings).Versions);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Only_present_skill_free_versions_are_ignored_and_do_not_warn(bool hasSkill)
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory("packages", "a", "1.0.0");
        temp.CreatePackageWithSkill("A", "2.0.0", hasSkill ? ["skill-y"] : []);

        var result = Discover(temp, "A@1.0.0", "A@2.0.0", "A@3.0.0");

        Assert.Equal(hasSkill ? 1 : 2, result.Ignored.Count);
        Assert.Equal("1.0.0", result.Ignored[0].PackageVersion);
        Assert.DoesNotContain(result.Ignored, package => package.PackageVersion == "3.0.0");
        Assert.Empty(result.Warnings);
        Assert.Empty(result.Errors);
    }

    private static InstallResult Discover(TempDirectory temp, params string[] coordinates) =>
        new SkillInstallService(new NoDotnet()).Discover(new InstallRequest
        {
            Packages = coordinates.Select(PackageCoordinate.Parse).ToArray(),
            Destination = "dest",
            WorkingDirectory = temp.Path,
            GlobalPackagesOverride = temp.Combine("packages"),
        });

    private sealed class NoDotnet : IProcessRunner
    {
        public ProcessResult Run(string fileName, IReadOnlyList<string> arguments, string? workingDirectory = null) =>
            throw new InvalidOperationException("Exact coordinates must not invoke dotnet.");
    }
}

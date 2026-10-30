using DotnetPackageSkills.Skills;

namespace DotnetPackageSkills.Tests;

public class MultiVersionManifestTests
{
    [Fact]
    public void Round_trips_disjoint_skills_from_two_versions_of_one_package()
    {
        using var temp = new TempDirectory();
        var manifest = new InstallManifest();
        manifest.SetSkills(
        [
            new TrackedSkill("A", "1.0", "skill-x"),
            new TrackedSkill("a", "2.0", "skill-y"),
        ]);
        manifest.Save(temp.Path);

        Assert.Equal(
            [new TrackedSkill("a", "1.0.0", "skill-x"), new TrackedSkill("a", "2.0.0", "skill-y")],
            InstallManifest.Load(temp.Path).EnumerateSkills());
        var text = File.ReadAllText(temp.Combine(InstallManifest.FileName));
        Assert.Contains("\"versions\"", text);
        Assert.DoesNotContain('\r', text);
    }

    [Fact]
    public void Rejects_old_single_version_layout_without_changes()
    {
        using var temp = new TempDirectory();
        const string text = """{"version":1,"packages":{"a":{"version":"1.0.0","skills":["x"]}}}""";
        var path = temp.CreateFile(InstallManifest.FileName, text);

        var error = Assert.Throws<PackageSkillsException>(() => InstallManifest.Load(temp.Path));

        Assert.Contains("pre-release", error.Message);
        Assert.Equal(text, File.ReadAllText(path));
    }

    [Fact]
    public void Rejects_duplicate_skill_ownership_across_versions()
    {
        var manifest = new InstallManifest();

        Assert.Throws<PackageSkillsException>(() => manifest.SetSkills(
        [
            new TrackedSkill("A", "1.0.0", "skill-x"),
            new TrackedSkill("a", "2.0.0", "SKILL-X"),
        ]));
        Assert.Empty(manifest.EnumerateSkills());
    }

    [Fact]
    public void Rejects_duplicate_skill_ownership_across_packages_before_saving()
    {
        var manifest = new InstallManifest();

        Assert.Throws<PackageSkillsException>(() => manifest.SetSkills(
        [
            new TrackedSkill("A", "1.0.0", "skill-x"),
            new TrackedSkill("B", "1.0.0", "SKILL-X"),
        ]));
        Assert.Empty(manifest.EnumerateSkills());
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData(InstallManifest.FileName)]
    public void Rejects_unsafe_skill_ownership_before_saving(string skill)
    {
        var manifest = new InstallManifest();

        Assert.Throws<PackageSkillsException>(() =>
            manifest.SetSkills([new TrackedSkill("A", "1.0.0", skill)]));
        Assert.Empty(manifest.EnumerateSkills());
    }

    [Theory]
    [InlineData("""{"versions":{"1.0.0":{"skills":["x"]},"1.0":{"skills":["y"]}}}""")]
    [InlineData("""{"versions":{"1.0.0":{"skills":["x"]},"2.0.0":{"skills":["X"]}}}""")]
    [InlineData("""{"versions":{"1.*":{"skills":["x"]}}}""")]
    [InlineData("""{"versions":{"1.0.0":{"skills":["../outside"]}}}""")]
    [InlineData("""{"versions":[]}""")]
    public void Rejects_invalid_version_groups_without_changing_the_manifest(string package)
    {
        using var temp = new TempDirectory();
        var text = "{\"version\":1,\"packages\":{\"a\":" + package + "}}";
        var path = temp.CreateFile(InstallManifest.FileName, text);

        Assert.Throws<PackageSkillsException>(() => InstallManifest.Load(temp.Path));
        Assert.Equal(text, File.ReadAllText(path));
    }
}

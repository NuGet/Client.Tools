using DotnetPackageSkills.Infrastructure;
using DotnetPackageSkills.NuGet;
using DotnetPackageSkills.Skills;

namespace DotnetPackageSkills.Tests;

public class MultiVersionInstallTests
{
    [Fact]
    public void All_skill_free_versions_are_ignored_without_creating_a_destination()
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory("packages", "a", "1.0.0");
        temp.CreateDirectory("packages", "a", "2.0.0");

        var result = Service().Install(Request(temp, "A@1.0.0", "A@2.0.0"));

        Assert.Empty(result.Skills);
        Assert.Equal(2, result.Ignored.Count);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
        Assert.False(Directory.Exists(result.Destination));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Skill_free_versions_do_not_block_other_packages(bool dryRun)
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory("packages", "a", "1.0.0");
        temp.CreateDirectory("packages", "a", "2.0.0");
        temp.CreatePackageWithSkill("B", "1.0.0", "valid");
        var request = Request(temp, "A@1.0.0", "A@2.0.0", "B@1.0.0") with { DryRun = dryRun };

        var result = Service().Install(request);

        Assert.Equal("valid", Assert.Single(result.Skills).SkillName);
        Assert.Equal(2, result.Ignored.Count);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
        Assert.Equal(!dryRun, File.Exists(temp.Combine("dest", "valid", "SKILL.md")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Disjoint_versions_install_and_warn(bool dryRun)
    {
        using var temp = new TempDirectory();
        temp.CreatePackageWithSkill("A", "1.0.0", "skill-x");
        temp.CreatePackageWithSkill("A", "2.0.0", "skill-y");

        var result = Service().Install(Request(temp, "A@1.0.0", "A@2.0.0") with { DryRun = dryRun });

        Assert.Equal(2, result.Skills.Count);
        Assert.Equal(["1.0.0", "2.0.0"], Assert.Single(result.Warnings).Versions);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Skipped);
        Assert.Equal(dryRun ? [] :
            new[] { new TrackedSkill("a", "1.0.0", "skill-x"), new TrackedSkill("a", "2.0.0", "skill-y") },
            InstallManifest.Load(result.Destination).EnumerateSkills());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Collisions_skip_every_candidate_but_install_other_valid_skills(bool samePackage, bool dryRun)
    {
        using var temp = new TempDirectory();
        temp.CreatePackageWithSkill("A", "1.0.0", "shared", "only-a");
        var other = samePackage ? "A" : "B";
        temp.CreatePackageWithSkill(other, "2.0.0", "SHARED", "only-other");

        var result = Service().Install(Request(temp, "A@1.0.0", $"{other}@2.0.0") with { DryRun = dryRun });

        Assert.Equal(["only-a", "only-other"], result.Skills.Select(skill => skill.SkillName).Order());
        Assert.Equal(2, result.Skipped.Count);
        Assert.Equal(2, Assert.Single(result.Errors).Candidates.Count);
        Assert.Equal(samePackage ? 1 : 0, result.Warnings.Count);
        Assert.False(Directory.Exists(temp.Combine("dest", "shared")));
        Assert.Equal(dryRun ? 0 : 2, InstallManifest.Load(result.Destination).EnumerateSkills().Count());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_collision_preserves_existing_bytes_and_exact_version_while_other_skills_upgrade(bool dryRun)
    {
        using var temp = new TempDirectory();
        Seed(temp, new TrackedSkill("a", "0.5.0", "shared"), new TrackedSkill("a", "0.5.0", "dropped"));
        temp.CreatePackageWithSkill("A", "1.0.0", "shared", "only-a");
        temp.CreatePackageWithSkill("A", "2.0.0", "shared", "only-other");
        var original = File.ReadAllBytes(temp.Combine("dest", "shared", "SKILL.md"));

        var result = Service().Install(Request(temp, "A@1.0.0", "A@2.0.0") with { DryRun = dryRun });

        Assert.Equal(2, result.Skills.Count);
        Assert.Equal("dropped", Assert.Single(result.Removed).Skill);
        Assert.Equal(2, result.Skipped.Count);
        Assert.Single(result.Errors);
        Assert.Equal(original, File.ReadAllBytes(temp.Combine("dest", "shared", "SKILL.md")));
        Assert.Contains(new TrackedSkill("a", "0.5.0", "shared"), InstallManifest.Load(result.Destination).EnumerateSkills());
        Assert.Equal(dryRun, File.Exists(temp.Combine("dest", "dropped", "SKILL.md")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_existing_different_owner_is_an_error_not_an_abort(bool dryRun)
    {
        using var temp = new TempDirectory();
        Seed(temp, new TrackedSkill("a", "1.0.0", "shared"));
        temp.CreatePackageWithSkill("B", "2.0.0", "shared", "valid");
        temp.CreateDirectory("packages", "a", "2.0.0");

        var result = Service().Install(Request(temp, "A@2.0.0", "B@2.0.0") with { DryRun = dryRun });

        Assert.Equal("valid", Assert.Single(result.Skills).SkillName);
        Assert.Equal("shared", Assert.Single(result.Skipped).SkillName);
        Assert.Single(result.Errors);
        Assert.Empty(result.Removed);
        Assert.Contains(new TrackedSkill("a", "1.0.0", "shared"), InstallManifest.Load(result.Destination).EnumerateSkills());
        Assert.Equal("original:shared", File.ReadAllText(temp.Combine("dest", "shared", "SKILL.md")));
    }

    [Fact]
    public void A_still_offered_version_retains_its_missing_skill_without_relabeling()
    {
        using var temp = new TempDirectory();
        Seed(temp, new TrackedSkill("a", "1.0.0", "skill-x"));
        temp.CreateDirectory("packages", "a", "1.0.0");
        temp.CreatePackageWithSkill("A", "2.0.0", "skill-y");

        var result = Service().Install(Request(temp, "A@1.0.0", "A@2.0.0"));

        Assert.Empty(result.Removed);
        Assert.Equal(
            [new TrackedSkill("a", "1.0.0", "skill-x"), new TrackedSkill("a", "2.0.0", "skill-y")],
            InstallManifest.Load(result.Destination).EnumerateSkills());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void A_missing_selected_version_cannot_remove_installed_skills_from_a_bare_or_exact_multi_version_install(
        bool bare, bool dryRun)
    {
        using var temp = new TempDirectory();
        Seed(temp, new TrackedSkill("a", "1.0.0", "skill-x"), new TrackedSkill("a", "2.0.0", "skill-y"));
        temp.CreatePackageWithSkill("A", "2.0.0", "skill-y");
        temp.CreateFile("dest/hand-authored/SKILL.md", "ours");
        var manifestBefore = File.ReadAllBytes(temp.Combine("dest", InstallManifest.FileName));
        var request = Request(temp, bare ? ["A"] : ["A@1.0.0", "A@2.0.0"]) with
        {
            Target = "missing.sln",
            DryRun = dryRun,
        };

        var result = Service().Install(request);

        Assert.Equal(2, result.PackagesScanned);
        Assert.Equal("skill-y", Assert.Single(result.Skills).SkillName);
        Assert.Empty(result.Removed);
        Assert.Empty(result.Ignored);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
        Assert.Equal("original:skill-x", File.ReadAllText(temp.Combine("dest", "skill-x", "SKILL.md")));
        Assert.Equal(dryRun ? "original:skill-y" : File.ReadAllText(
            temp.Combine("packages", "a", "2.0.0", "skills", "skill-y", "SKILL.md")),
            File.ReadAllText(temp.Combine("dest", "skill-y", "SKILL.md")));
        Assert.Equal(manifestBefore, File.ReadAllBytes(temp.Combine("dest", InstallManifest.FileName)));
        Assert.Equal(
            [new TrackedSkill("a", "1.0.0", "skill-x"), new TrackedSkill("a", "2.0.0", "skill-y")],
            InstallManifest.Load(result.Destination).EnumerateSkills());
        Assert.Equal("ours", File.ReadAllText(temp.Combine("dest", "hand-authored", "SKILL.md")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Dropped_skill_cleanup_waits_until_every_selected_version_of_the_package_can_be_inspected(bool dryRun)
    {
        using var temp = new TempDirectory();
        Seed(temp, new TrackedSkill("a", "0.5.0", "old-skill"));
        temp.CreatePackageWithSkill("A", "2.0.0", "new-skill");
        var request = Request(temp, "A@1.0.0", "A@2.0.0");
        var service = Service();

        var partial = service.Install(request with { DryRun = dryRun });

        Assert.Empty(partial.Removed);
        Assert.Equal("original:old-skill", File.ReadAllText(temp.Combine("dest", "old-skill", "SKILL.md")));
        Assert.Contains(new TrackedSkill("a", "0.5.0", "old-skill"),
            InstallManifest.Load(partial.Destination).EnumerateSkills());
        Assert.Equal(!dryRun, File.Exists(temp.Combine("dest", "new-skill", "SKILL.md")));

        temp.CreateDirectory("packages", "a", "1.0.0");
        var complete = service.Install(request);

        Assert.Equal(new TrackedSkill("a", "0.5.0", "old-skill"), Assert.Single(complete.Removed));
        Assert.False(Directory.Exists(temp.Combine("dest", "old-skill")));
        Assert.Equal(new TrackedSkill("a", "2.0.0", "new-skill"),
            Assert.Single(InstallManifest.Load(complete.Destination).EnumerateSkills()));
    }

    [Fact]
    public void A_bare_id_expands_all_manifest_versions_and_deduplicates_an_exact_selector()
    {
        using var temp = new TempDirectory();
        Seed(temp, new TrackedSkill("a", "1.0.0", "skill-x"), new TrackedSkill("a", "2.0.0", "skill-y"));
        temp.CreatePackageWithSkill("A", "1.0.0", "skill-x");
        temp.CreatePackageWithSkill("A", "2.0.0", "skill-y");
        var request = Request(temp, "A", "A@1.0.0") with { Target = "missing.sln" };

        var result = Service().Install(request);

        Assert.Equal(2, result.PackagesScanned);
        Assert.Equal(2, result.Skills.Count);
        Assert.Single(result.Warnings);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public void A_bare_id_expands_all_target_versions()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("App.sln");
        temp.CreatePackageWithSkill("A", "1.0.0", "skill-x");
        temp.CreatePackageWithSkill("A", "2.0.0", "skill-y");
        var service = new SkillInstallService(new TargetDotnet());

        var result = service.Install(Request(temp, "A"));

        Assert.Equal(2, result.PackagesScanned);
        Assert.Equal(2, result.Skills.Count);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void An_interactive_named_install_can_add_a_disjoint_version_without_changing_existing_skills()
    {
        using var temp = new TempDirectory();
        Seed(temp, new TrackedSkill("a", "1.0.0", "skill-x"));
        temp.CreatePackageWithSkill("A", "2.0.0", "skill-y");
        var service = Service();
        var request = Request(temp, "A@2.0.0");
        var installed = SkillInstallService.InstalledSkills(temp.Combine("dest"), temp.Path);

        var prepared = service.PrepareInteractiveInstall(request, service.Discover(request), installed);
        var result = service.Install(request, prepared, new SkillChoice(prepared.Skills) { ExpectedInstalled = installed });

        Assert.Single(result.Skills);
        Assert.Equal(
            [new TrackedSkill("a", "1.0.0", "skill-x"), new TrackedSkill("a", "2.0.0", "skill-y")],
            InstallManifest.Load(result.Destination).EnumerateSkills());
        Assert.Equal("original:skill-x", File.ReadAllText(temp.Combine("dest", "skill-x", "SKILL.md")));
    }

    [Fact]
    public void Interactive_partial_install_keeps_diagnostics_without_offering_collisions()
    {
        using var temp = new TempDirectory();
        temp.CreatePackageWithSkill("A", "1.0.0", "shared", "skill-x");
        temp.CreatePackageWithSkill("A", "2.0.0", "shared", "skill-y");
        var service = Service();
        var request = Request(temp, "A@1.0.0", "A@2.0.0");

        var prepared = service.PrepareInteractiveInstall(request, service.Discover(request), []);
        Assert.Equal(["skill-x", "skill-y"], prepared.Skills.Select(skill => skill.SkillName).Order());
        var result = service.Install(request, prepared, new SkillChoice([prepared.Skills[0]]) { ExpectedInstalled = [] });

        Assert.Single(result.Skills);
        Assert.Single(result.Errors);
        Assert.Single(result.Warnings);
        Assert.Equal(2, result.Skipped.Count);
        Assert.False(Directory.Exists(temp.Combine("dest", "shared")));
    }

    [Fact]
    public void A_caller_choice_cannot_resurrect_a_rejected_collision()
    {
        using var temp = new TempDirectory();
        temp.CreatePackageWithSkill("A", "1.0.0", "shared", "skill-x");
        temp.CreatePackageWithSkill("A", "2.0.0", "shared", "skill-y");
        var service = Service();
        var request = Request(temp, "A@1.0.0", "A@2.0.0");
        var discovered = service.Discover(request);
        var rejected = discovered.AllCandidates!.First(skill => skill.SkillName == "shared");

        var result = service.Install(request, discovered, new SkillChoice([rejected]));

        Assert.Empty(result.Skills);
        Assert.Single(result.Errors);
        Assert.Equal(2, result.Skipped.Count);
        Assert.False(Directory.Exists(result.Destination));
    }

    [Fact]
    public void Repeating_a_partial_install_keeps_exact_ownership_and_reports_each_collision_once()
    {
        using var temp = new TempDirectory();
        Seed(temp, new TrackedSkill("a", "0.5.0", "shared"));
        temp.CreatePackageWithSkill("A", "1.0.0", "shared", "skill-x");
        temp.CreatePackageWithSkill("A", "2.0.0", "shared", "skill-y");
        var service = Service();
        var request = Request(temp, "A@1.0.0", "A@2.0.0");
        var first = service.Install(request);
        var bytes = File.ReadAllBytes(temp.Combine("dest", InstallManifest.FileName));

        var second = service.Install(request);

        Assert.Equal(2, second.Skills.Count);
        Assert.Equal(2, second.Skipped.Count);
        Assert.Single(second.Errors);
        Assert.Single(second.Warnings);
        Assert.Equal(bytes, File.ReadAllBytes(temp.Combine("dest", InstallManifest.FileName)));
        Assert.Equal("original:shared", File.ReadAllText(temp.Combine("dest", "shared", "SKILL.md")));
        Assert.Equal(3, service.Uninstall("dest", temp.Path, "A", null, dryRun: false).Count);
        Assert.False(Directory.Exists(first.Destination));
    }

    [Fact]
    public void Exact_uninstall_and_stale_cleanup_remove_only_the_matching_version()
    {
        using var temp = new TempDirectory();
        Seed(temp, new TrackedSkill("a", "1.0.0", "skill-x"), new TrackedSkill("a", "2.0.0", "skill-y"));
        temp.CreateFile("dest/hand-authored/SKILL.md", "ours");
        var service = Service();

        var stale = service.Uninstall("dest", temp.Path, null, null, dryRun: true,
            staleAgainst: [new PackageReferenceInfo("A", "2.0.0")]);
        Assert.Equal(new TrackedSkill("a", "1.0.0", "skill-x"), Assert.Single(stale));
        var removed = service.Uninstall("dest", temp.Path, "A", "1.0", dryRun: false);

        Assert.Equal(new TrackedSkill("a", "1.0.0", "skill-x"), Assert.Single(removed));
        Assert.Equal(new TrackedSkill("a", "2.0.0", "skill-y"),
            Assert.Single(InstallManifest.Load(temp.Combine("dest")).EnumerateSkills()));
        Assert.Equal("ours", File.ReadAllText(temp.Combine("dest", "hand-authored", "SKILL.md")));
    }

    private static InstallRequest Request(TempDirectory temp, params string[] coordinates) => new()
    {
        Packages = coordinates.Select(PackageCoordinate.Parse).ToArray(),
        Destination = "dest",
        WorkingDirectory = temp.Path,
        GlobalPackagesOverride = temp.Combine("packages"),
    };

    private static SkillInstallService Service() => new(new TargetDotnet(exactOnly: true));

    private static void Seed(TempDirectory temp, params TrackedSkill[] skills)
    {
        var manifest = new InstallManifest();
        manifest.SetSkills(skills);
        manifest.Save(temp.Combine("dest"));
        foreach (var skill in skills) { temp.CreateFile($"dest/{skill.Skill}/SKILL.md", $"original:{skill.Skill}"); }
    }

    private sealed class TargetDotnet(bool exactOnly = false) : IProcessRunner
    {
        public ProcessResult Run(string fileName, IReadOnlyList<string> arguments, string? workingDirectory = null)
        {
            if (exactOnly) { throw new InvalidOperationException("Manifest/exact selectors must not invoke dotnet."); }
            Assert.Contains("list", arguments);
            return new ProcessResult(0,
                """
                {"projects":[{"frameworks":[{"topLevelPackages":[
                  {"id":"A","resolvedVersion":"1.0.0"},{"id":"A","resolvedVersion":"2.0.0"}
                ]}]}]}
                """, "");
        }
    }
}

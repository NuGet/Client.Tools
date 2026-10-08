using DotnetPackageSkills.NuGet;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DotnetPackageSkills.Tests;

public class TargetLocatorTests
{
    [Fact]
    public void Detect_prefers_a_solution_over_a_project()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("MyApp.sln");
        temp.CreateFile("MyApp.csproj");

        Assert.EndsWith("MyApp.sln", TargetLocator.Detect(temp.Path));
    }

    [Fact]
    public void Detect_prefers_slnx_over_sln()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("MyApp.sln");
        temp.CreateFile("MyApp.slnx");

        Assert.EndsWith("MyApp.slnx", TargetLocator.Detect(temp.Path));
    }

    [Fact]
    public void Detect_prefers_the_top_level_over_a_nested_solution()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("Root.sln");
        temp.CreateFile("nested/Inner.sln");

        Assert.EndsWith("Root.sln", TargetLocator.Detect(temp.Path));
    }

    [Fact]
    public void Detect_descends_when_nothing_is_at_the_top_level()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("src/MyApp/MyApp.csproj");

        Assert.EndsWith("MyApp.csproj", TargetLocator.Detect(temp.Path));
    }

    [Fact]
    public void Detect_ignores_build_output_directories()
    {
        using var temp = new TempDirectory();

        // Project files copied into obj/ during restore would otherwise win by sort order.
        temp.CreateFile("obj/Aaa.csproj");
        temp.CreateFile("src/Real.csproj");

        Assert.EndsWith("Real.csproj", TargetLocator.Detect(temp.Path));
    }

    [Fact]
    public void Detect_finds_fsproj_and_vbproj_too()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("src/MyApp.fsproj");

        Assert.EndsWith("MyApp.fsproj", TargetLocator.Detect(temp.Path));
    }

    [Fact]
    public void Detect_explains_what_to_do_when_there_is_no_target()
    {
        using var temp = new TempDirectory();

        var exception = Assert.Throws<PackageSkillsException>(() => TargetLocator.Detect(temp.Path));

        Assert.Contains("--target", exception.Message);
    }

    [Fact]
    public void Resolve_accepts_a_path_relative_to_the_working_directory()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("src/MyApp/MyApp.csproj");

        var resolved = TargetLocator.Resolve("src/MyApp/MyApp.csproj", temp.Path);

        Assert.True(Path.IsPathRooted(resolved));
        Assert.True(File.Exists(resolved));
    }

    [Fact]
    public void Resolve_searches_within_a_directory_that_was_passed_as_the_target()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("src/MyApp/MyApp.csproj");

        Assert.EndsWith("MyApp.csproj", TargetLocator.Resolve("src", temp.Path));
    }

    [Fact]
    public void Resolve_rejects_a_file_that_is_not_a_project_or_solution()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("notes.txt");

        var exception = Assert.Throws<PackageSkillsException>(() => TargetLocator.Resolve("notes.txt", temp.Path));

        Assert.Contains(".csproj", exception.Message);
    }

    [Fact]
    public void Resolve_reports_a_missing_target_by_full_path()
    {
        using var temp = new TempDirectory();

        var exception = Assert.Throws<PackageSkillsException>(() => TargetLocator.Resolve("Ghost.sln", temp.Path));

        Assert.Contains("Ghost.sln", exception.Message);
    }

    [WindowsTheory]
    [InlineData("bin")]
    [InlineData("OBJ")]
    [InlineData(".git")]
    [InlineData("NODE_MODULES")]
    [InlineData("artifacts")]
    public void Ignored_subtrees_are_pruned_without_opening_them(string ignored)
    {
        using var temp = new TempDirectory();
        var blocked = temp.CreateDirectory("nested", ignored);
        temp.CreateFile($"nested/{ignored}/Hidden.slnx");
        var expected = temp.CreateFile("src/Real.csproj");

        WithBlockedEnumeration(blocked, () => Assert.Equal(expected, TargetLocator.Detect(temp.Path)));
    }

    [WindowsTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Inaccessible_children_do_not_hide_a_readable_sibling_but_explicit_roots_report_errors(bool explicitRoot)
    {
        using var temp = new TempDirectory();
        var blocked = temp.CreateDirectory("inaccessible");
        temp.CreateFile("inaccessible/Hidden.slnx");
        var expected = temp.CreateFile("src/Real.csproj");

        WithBlockedEnumeration(blocked, () =>
        {
            if (explicitRoot)
            {
                var error = Assert.Throws<PackageSkillsException>(() => TargetLocator.Resolve("inaccessible", temp.Path));
                Assert.Contains("read", error.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("inaccessible", error.Message);
            }
            else
            {
                Assert.Equal(expected, TargetLocator.Detect(temp.Path));
            }
        });
    }

    [Theory]
    [InlineData("bin")]
    [InlineData("OBJ")]
    [InlineData(".GIT")]
    [InlineData("node_modules")]
    [InlineData("ARTIFACTS")]
    public void Every_ignored_name_is_excluded_at_any_depth(string ignored)
    {
        using var temp = new TempDirectory();
        temp.CreateFile($"nested/{ignored}/Hidden.slnx");
        var expected = temp.CreateFile("src/Real.vbproj");

        Assert.Equal(expected, TargetLocator.Detect(temp.Path));
        Assert.EndsWith("Hidden.slnx", TargetLocator.Resolve(Path.Combine("nested", ignored), temp.Path));
    }

    [Theory]
    [InlineData("src/A.sln", "src/Z.slnx", "src/Z.slnx")]
    [InlineData("src/A.fsproj", "src/Z.csproj", "src/Z.csproj")]
    [InlineData("src/A.vbproj", "src/Z.fsproj", "src/Z.fsproj")]
    [InlineData("src/Z/A.sln", "src/A/Z.sln", "src/A/Z.sln")]
    [InlineData("Root.csproj", "src/A.slnx", "Root.csproj")]
    public void Target_ranking_keeps_extension_stage_and_ordinal_path_precedence(
        string first, string second, string expected)
    {
        using var temp = new TempDirectory();
        temp.CreateFile(first);
        temp.CreateFile(second);

        Assert.Equal(temp.Combine(expected.Split('/')), TargetLocator.Detect(temp.Path));
    }

    [SymbolicLinkTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Automatic_search_skips_directory_links_but_explicit_targets_accept_them(bool explicitTarget)
    {
        using var temp = new TempDirectory();
        var root = temp.CreateDirectory("repo");
        var outside = temp.CreateDirectory("outside");
        temp.CreateFile("outside/App.slnx");
        var link = Path.Combine(root, "linked");
        Directory.CreateSymbolicLink(link, outside);
        try
        {
            if (explicitTarget) { Assert.EndsWith("App.slnx", TargetLocator.Resolve("linked", root)); }
            else { Assert.Throws<PackageSkillsException>(() => TargetLocator.Detect(root)); }
        }
        finally { Directory.Delete(link); }
    }

    [SymbolicLinkTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Automatic_directory_link_cycles_do_not_recurse(bool parent)
    {
        using var temp = new TempDirectory();
        var root = temp.CreateDirectory("repo");
        var child = temp.CreateDirectory("repo", "nested");
        var link = Path.Combine(child, "cycle");
        Directory.CreateSymbolicLink(link, parent ? root : child);
        try
        {
            Assert.Throws<PackageSkillsException>(() => TargetLocator.Detect(root));
        }
        finally { Directory.Delete(link); }
    }

    private static void WithBlockedEnumeration(string path, Action check)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("This fixture uses Windows directory access rules.");
        }

        var directory = new DirectoryInfo(path);
        var original = directory.GetAccessControl(AccessControlSections.Access)
            .GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        var denied = directory.GetAccessControl(AccessControlSections.Access);
        denied.AddAccessRule(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny));
        directory.SetAccessControl(denied);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => Directory.EnumerateFileSystemEntries(path).ToArray());
            check();
        }
        finally
        {
            var restored = new DirectorySecurity();
            restored.SetSecurityDescriptorSddlForm(original, AccessControlSections.Access);
            directory.SetAccessControl(restored);
        }
    }
}

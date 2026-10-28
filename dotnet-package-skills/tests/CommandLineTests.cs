using System.CommandLine;
using DotnetPackageSkills.Cli;
using DotnetPackageSkills.Infrastructure;
using DotnetPackageSkills.Skills;

namespace DotnetPackageSkills.Tests;

public class CommandLineTests
{
    private sealed class FakeDotnet(string listPackageJson) : IProcessRunner
    {
        public List<string> Invocations { get; } = [];

        public ProcessResult Run(string fileName, IReadOnlyList<string> arguments, string? workingDirectory = null)
        {
            Invocations.Add(string.Join(' ', arguments));
            return arguments.Contains("list")
                ? new ProcessResult(0, listPackageJson, string.Empty)
                : throw new InvalidOperationException($"Unexpected invocation: {string.Join(' ', arguments)}");
        }
    }

    [Theory]
    [InlineData("install", "--source")]
    [InlineData("install", "-s")]
    [InlineData("list", "--source")]
    [InlineData("list", "-s")]
    [InlineData("uninstall", "--source")]
    [InlineData("uninstall", "-s")]
    public void All_commands_accept_source_options(string command, string option)
    {
        var args = new List<string> { command, option, "src/App.csproj" };
        if (command == "uninstall") { args.Add("--stale"); }

        Assert.Empty(CommandLineBuilder.Build().Parse([.. args]).Errors);
    }

    [Theory]
    [InlineData("install")]
    [InlineData("list")]
    [InlineData("uninstall")]
    public void Help_advertises_only_the_new_source_options(string command)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(0, CommandLineBuilder.Invoke([command, "--help"], output, error));

        Assert.Empty(error.ToString());
        Assert.Contains("--source", output.ToString());
        Assert.Contains("-s,", output.ToString());
        Assert.Contains("directory to read package references from", output.ToString());
        Assert.DoesNotContain("--target", output.ToString());
        Assert.DoesNotContain("-t,", output.ToString());
    }

    [Theory]
    [InlineData("install", "--package")]
    [InlineData("install", "-p")]
    [InlineData("list", "--package")]
    [InlineData("list", "-p")]
    public void Bare_package_ids_use_the_selected_destination_manifest(string command, string option)
    {
        using var temp = new TempDirectory();
        temp.CreatePackageWithSkill("Mockly", "1.10.0", "usage");
        temp.CreatePackageWithSkill("Mockly", "9.0.0", "wrong-version");
        var destination = temp.CreateDirectory("custom-skills");
        var manifest = new InstallManifest();
        manifest.SetSkills([new TrackedSkill("Mockly", "1.10", "usage")]);
        manifest.Save(destination);
        var before = File.ReadAllBytes(Path.Combine(destination, InstallManifest.FileName));
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CommandLineBuilder.Invoke(
            [command, option, "mOcKlY", "--destination", destination, "--global-packages", temp.Combine("packages")],
            output, error);

        Assert.Equal(0, exitCode);
        Assert.Empty(error.ToString());
        Assert.Equal(command == "install", File.Exists(Path.Combine(destination, "usage", "SKILL.md")));
        Assert.False(Directory.Exists(Path.Combine(destination, "wrong-version")));
        Assert.Equal("1.10.0", InstallManifest.Load(destination).Packages["mockly"].Version);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(destination, InstallManifest.FileName)));
    }

    [Theory]
    [InlineData("install")]
    [InlineData("list")]
    public void Package_and_source_can_be_combined_for_bare_id_resolution(string command)
    {
        Assert.Empty(CommandLineBuilder.Build().Parse(
            [command, "-p", "Mockly", "-s", "src/App.csproj"]).Errors);
        Assert.Empty(CommandLineBuilder.Build().Parse(
            [command, "--package", "Mockly@1.0.0", "--source", "missing.sln"]).Errors);
    }

    [Theory]
    [InlineData("install", "--source")]
    [InlineData("install", "-s")]
    [InlineData("list", "--source")]
    [InlineData("list", "-s")]
    public void Package_and_source_flow_through_the_command_action(string command, string option)
    {
        using var temp = new TempDirectory();
        var target = temp.CreateFile("src/App.csproj");
        temp.CreatePackageWithSkill("Mockly", "1.10.0", "usage");
        var destination = temp.Combine("skills");
        var runner = new FakeDotnet(
            """{"projects":[{"frameworks":[{"topLevelPackages":[{"id":"Mockly","resolvedVersion":"1.10.0"}]}]}]}""");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CommandLineBuilder.Invoke(
            [
                command,
                "-p", "Mockly",
                option, target,
                "-d", destination,
                "--global-packages", temp.Combine("packages"),
            ],
            runner,
            output,
            error);

        Assert.Equal(0, exitCode);
        Assert.Empty(error.ToString());
        Assert.Equal(command == "install", File.Exists(Path.Combine(destination, "usage", "SKILL.md")));
        var invocation = Assert.Single(runner.Invocations);
        Assert.StartsWith("list ", invocation);
        Assert.Contains(target, invocation);
    }

    [Theory]
    [InlineData(InstallManifest.FileName, false)]
    [InlineData(InstallManifest.FileName, true)]
    [InlineData(".DOTNET-PACKAGE-SKILLS.JSON", false)]
    [InlineData(".DOTNET-PACKAGE-SKILLS.JSON", true)]
    public void Install_ignores_skills_named_for_the_ownership_manifest(string name, bool dryRun)
    {
        using var temp = new TempDirectory();
        temp.CreatePackageWithSkill("Collision.Package", "1.0.0", name);
        var destination = temp.Combine("skills");
        var runner = new FakeDotnet("{}");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var args = new List<string>
        {
            "install",
            "-p", "Collision.Package@1.0.0",
            "-d", destination,
            "--global-packages", temp.Combine("packages"),
        };
        if (dryRun) { args.Add("--dry-run"); }

        var exitCode = CommandLineBuilder.Invoke([.. args], runner, output, error);

        Assert.Equal(0, exitCode);
        Assert.Empty(error.ToString());
        Assert.False(Directory.Exists(destination));
        Assert.Empty(runner.Invocations);
    }

    [Theory]
    [InlineData("install")]
    [InlineData("list")]
    public void Package_help_documents_optional_versions_and_the_manifest_source(string command)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(0, CommandLineBuilder.Invoke([command, "--help"], output, error));

        Assert.Empty(error.ToString());
        Assert.Contains("ID[@VERSION]", output.ToString());
        Assert.Contains("manifest", output.ToString());
        Assert.Contains("--source", output.ToString());
        Assert.Contains("auto-detected solution or project", output.ToString());
        Assert.Contains("NuGet global-packages directory", output.ToString());
        Assert.Contains("invocation directory", output.ToString());
        Assert.DoesNotContain("global packages folder", output.ToString());
    }

    [Fact]
    public void List_with_an_explicit_version_does_not_need_a_readable_destination_manifest()
    {
        using var temp = new TempDirectory();
        temp.CreatePackageWithSkill("Mockly", "2.0.0", "explicit");
        var destination = temp.CreateDirectory("custom-skills");
        var manifest = temp.CreateFile($"custom-skills/{InstallManifest.FileName}", "{ invalid json");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CommandLineBuilder.Invoke(
            ["list", "-p", "Mockly@2.0.0", "-d", destination, "--global-packages", temp.Combine("packages")],
            output, error);

        Assert.Equal(0, exitCode);
        Assert.Empty(error.ToString());
        Assert.False(Directory.Exists(Path.Combine(destination, "explicit")));
        Assert.Equal("{ invalid json", File.ReadAllText(manifest));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    public void A_supplied_blank_uninstall_filter_is_rejected(string filter)
    {
        var error = Assert.Throws<PackageSkillsException>(() => CommandLineBuilder.ParseUninstallFilter(filter));

        Assert.Contains("non-empty package ID", error.Message);
        Assert.NotEmpty(CommandLineBuilder.Build().Parse(["uninstall", "--package", filter]).Errors);
    }

    [Theory]
    [InlineData("--package")]
    [InlineData("-p")]
    public void An_uninstall_package_option_without_a_value_is_rejected(string option)
    {
        Assert.NotEmpty(CommandLineBuilder.Build().Parse(["uninstall", option]).Errors);
        Assert.NotEmpty(CommandLineBuilder.Build().Parse(["uninstall", option, "--dry-run"]).Errors);
    }

    [Fact]
    public void Only_an_absent_uninstall_filter_means_all_packages()
    {
        Assert.Equal((null, null), CommandLineBuilder.ParseUninstallFilter(null));
        Assert.Equal(("Mockly", null), CommandLineBuilder.ParseUninstallFilter(" Mockly "));
    }

    [Theory]
    [InlineData("--package", "only once")]
    [InlineData("-p", "only once")]
    [InlineData("--package=", "only once")]
    [InlineData("--package=Alpha", "expects a single argument")]
    public void Repeated_uninstall_filters_are_rejected_even_when_the_last_value_is_missing(
        string repeated, string message)
    {
        var result = CommandLineBuilder.Build().Parse(
            ["uninstall", "--dry-run", "--package", "Alpha", repeated]);

        Assert.Contains(result.Errors, error => error.Message.Contains(message, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("_Acme")]
    [InlineData("Acme_")]
    [InlineData("_")]
    public void Uninstall_accepts_valid_underscore_boundary_package_ids(string id)
    {
        Assert.Equal((id, null), CommandLineBuilder.ParseUninstallFilter(id));
        Assert.Empty(CommandLineBuilder.Build().Parse(["uninstall", "--package", id, "--dry-run"]).Errors);
    }

    [Theory]
    [InlineData("1.2", "1.2.0")]
    [InlineData("1.2.0.0", "1.2.0")]
    [InlineData("1.2.0-RC.1", "1.2.0-rc.1")]
    [InlineData("01.02.0-BETA.1+Build.A", "1.2-beta.1+Build.B")]
    public void Uninstall_filter_matching_normalizes_versions_for_both_modes(string filterVersion, string installedVersion)
    {
        var (id, version) = CommandLineBuilder.ParseUninstallFilter($"mockly@{filterVersion}");

        Assert.True(SkillInstaller.Matches(new TrackedSkill("Mockly", installedVersion, "usage"), id, version));
        Assert.False(SkillInstaller.Matches(new TrackedSkill("Other", installedVersion, "usage"), id, version));
    }

    [Theory]
    [InlineData("1.0.0-alpha.")]
    [InlineData("1.0.0+a..b")]
    [InlineData("2147483648.0.0")]
    [InlineData("1.2.3.4.5")]
    public void Invalid_exact_versions_cannot_be_uninstall_filters_in_either_mode(string version)
    {
        Assert.Throws<PackageSkillsException>(() => CommandLineBuilder.ParseUninstallFilter($"Mockly@{version}"));
        Assert.NotEmpty(CommandLineBuilder.Build().Parse(["uninstall", "--package", $"Mockly@{version}"]).Errors);
        Assert.NotEmpty(CommandLineBuilder.Build().Parse(["uninstall", "-i", "--package", $"Mockly@{version}"]).Errors);
    }

    [Fact]
    public void Uninstall_accepts_the_interactive_flag()
    {
        Assert.Empty(CommandLineBuilder.Build().Parse(["uninstall", "-i"]).Errors);
        Assert.Empty(CommandLineBuilder.Build().Parse(["uninstall", "--interactive"]).Errors);
    }

    [Fact]
    public void Uninstall_stale_accepts_a_target_and_every_other_uninstall_option_but_a_package()
    {
        Assert.Empty(CommandLineBuilder.Build().Parse(
            ["uninstall", "--stale", "--source", "App.sln", "--dry-run", "-i", "-d", ".claude/skills"])
            .Errors);
        Assert.Empty(CommandLineBuilder.Build().Parse(["uninstall", "--stale", "-s", "src"]).Errors);
        Assert.Empty(CommandLineBuilder.Build().Parse(["uninstall", "--stale"]).Errors);
    }

    [Fact]
    public void Uninstall_stale_cannot_be_combined_with_a_package_filter()
    {
        var result = CommandLineBuilder.Build().Parse(["uninstall", "--stale", "--package", "Mockly"]);

        Assert.Contains(result.Errors, error => error.Message.Contains("--stale and --package cannot be combined"));
    }

    [Fact]
    public void Explicit_false_stale_can_be_combined_with_a_package_filter()
    {
        Assert.Empty(CommandLineBuilder.Build().Parse(
            ["uninstall", "--stale", "false", "--package", "Mockly", "--dry-run"]).Errors);
    }

    [Theory]
    [InlineData("--source")]
    [InlineData("-s")]
    public void Uninstall_target_needs_stale(string option)
    {
        var result = CommandLineBuilder.Build().Parse(["uninstall", option, "App.sln"]);
        var explicitFalse = CommandLineBuilder.Build().Parse(
            ["uninstall", "--stale", "false", option, "App.sln"]);

        Assert.Contains(result.Errors, error =>
            error.Message.Contains("--source can be used with uninstall only together with --stale", StringComparison.Ordinal));
        Assert.Contains(explicitFalse.Errors, error =>
            error.Message.Contains("--source can be used with uninstall only together with --stale", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void False_stale_with_target_stops_plain_uninstall_before_any_change(bool dryRun)
    {
        using var temp = new TempDirectory();
        var destination = temp.CreateDirectory("skills");
        var skill = temp.CreateFile("skills/mockly/SKILL.md", "installed guidance");
        var manifest = new InstallManifest();
        manifest.SetSkills([new TrackedSkill("Mockly", "1.10.0", "mockly")]);
        manifest.Save(destination);
        var manifestPath = Path.Combine(destination, InstallManifest.FileName);
        var beforeManifest = File.ReadAllBytes(manifestPath);
        var runner = new FakeDotnet("{}");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var args = new List<string>
        {
            "uninstall", "--stale", "false", "--source", temp.Combine("Missing.sln"), "--destination", destination,
        };
        if (dryRun) { args.Add("--dry-run"); }

        var exitCode = CommandLineBuilder.Invoke([.. args], runner, output, error);

        Assert.Equal(1, exitCode);
        Assert.Contains("--source can be used with uninstall only together with --stale", error.ToString());
        Assert.Equal("installed guidance", File.ReadAllText(skill));
        Assert.Equal(beforeManifest, File.ReadAllBytes(manifestPath));
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public void False_stale_with_target_stops_before_the_interactive_picker()
    {
        using var temp = new TempDirectory();
        var destination = temp.CreateDirectory("skills");
        var skill = temp.CreateFile("skills/mockly/SKILL.md", "installed guidance");
        var manifest = new InstallManifest();
        manifest.SetSkills([new TrackedSkill("Mockly", "1.10.0", "mockly")]);
        manifest.Save(destination);
        var manifestPath = Path.Combine(destination, InstallManifest.FileName);
        var beforeManifest = File.ReadAllBytes(manifestPath);
        var runner = new FakeDotnet("{}");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CommandLineBuilder.Invoke(
            [
                "uninstall", "--stale", "false", "--source", temp.Combine("Missing.sln"),
                "--destination", destination, "--interactive",
            ],
            runner,
            output,
            error);

        Assert.Equal(1, exitCode);
        Assert.Contains("--source can be used with uninstall only together with --stale", error.ToString());
        Assert.Equal("installed guidance", File.ReadAllText(skill));
        Assert.Equal(beforeManifest, File.ReadAllBytes(manifestPath));
        Assert.Empty(runner.Invocations);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Incomplete_package_graph_stops_stale_uninstall_before_any_change(bool dryRun, bool interactive)
    {
        using var temp = new TempDirectory();
        var target = temp.CreateFile("src/MyApp.csproj");
        var destination = temp.CreateDirectory("skills");
        var skill = temp.CreateFile("skills/mockly/SKILL.md", "installed guidance");
        var manifest = new InstallManifest();
        manifest.SetSkills([new TrackedSkill("Mockly", "1.10.0", "mockly")]);
        manifest.Save(destination);
        var manifestPath = Path.Combine(destination, InstallManifest.FileName);
        var beforeManifest = File.ReadAllBytes(manifestPath);
        var runner = new FakeDotnet(
            $$"""{"version":1,"projects":[{"path":"{{target.Replace("\\", "\\\\")}}"}]}""");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var args = new List<string>
        {
            "uninstall", "--stale", "--source", target, "--destination", destination,
        };
        if (dryRun) { args.Add("--dry-run"); }
        if (interactive) { args.Add("--interactive"); }

        var exitCode = CommandLineBuilder.Invoke([.. args], runner, output, error);

        Assert.Equal(1, exitCode);
        Assert.Equal("installed guidance", File.ReadAllText(skill));
        Assert.Equal(beforeManifest, File.ReadAllBytes(manifestPath));
        Assert.Single(runner.Invocations);
    }

    [Theory]
    [InlineData("{}", false, false)]
    [InlineData("{}", true, false)]
    [InlineData("{}", false, true)]
    [InlineData("""{ "version": 1, "projects": null }""", false, false)]
    [InlineData("""{ "version": 1, "projects": null }""", true, false)]
    [InlineData("""{ "version": 1, "projects": null }""", false, true)]
    public void Missing_projects_stops_stale_uninstall_before_any_change(
        string report,
        bool dryRun,
        bool interactive)
    {
        using var temp = new TempDirectory();
        var target = temp.CreateFile("src/MyApp.csproj");
        var destination = temp.CreateDirectory("skills");
        var skill = temp.CreateFile("skills/mockly/SKILL.md", "installed guidance");
        var manifest = new InstallManifest();
        manifest.SetSkills([new TrackedSkill("Mockly", "1.10.0", "mockly")]);
        manifest.Save(destination);
        var manifestPath = Path.Combine(destination, InstallManifest.FileName);
        var beforeManifest = File.ReadAllBytes(manifestPath);
        var runner = new FakeDotnet(report);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var args = new List<string>
        {
            "uninstall", "--stale", "--source", target, "--destination", destination,
        };
        if (dryRun) { args.Add("--dry-run"); }
        if (interactive) { args.Add("--interactive"); }

        var exitCode = CommandLineBuilder.Invoke(
            [.. args],
            runner,
            output,
            error);

        Assert.Equal(1, exitCode);
        Assert.Equal("installed guidance", File.ReadAllText(skill));
        Assert.Equal(beforeManifest, File.ReadAllBytes(manifestPath));
        Assert.Single(runner.Invocations);
    }

    [Theory]
    [InlineData("install", null)]
    [InlineData("list", null)]
    [InlineData("uninstall", "--stale")]
    public void No_command_offers_no_restore(string command, string? extra)
    {
        // Restoring is left to dotnet list package and to the customer, so there is nothing to
        // turn off here.
        Assert.DoesNotContain(
            CommandLineBuilder.Build().Subcommands.Single(candidate => candidate.Name == command).Options,
            option => option.Name == "--no-restore");
        string[] args = extra is null ? [command, "--no-restore"] : [command, extra, "--no-restore"];
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CommandLineBuilder.Invoke(args, output, error);

        Assert.Equal(1, exitCode);
        Assert.Contains("Unrecognized command or argument '--no-restore'", error.ToString());
    }

    [Theory]
    [InlineData("install")]
    [InlineData("list")]
    public void Only_uninstall_has_the_stale_option(string command)
    {
        Assert.NotEmpty(CommandLineBuilder.Build().Parse([command, "--stale"]).Errors);
    }

    [Fact]
    public void The_interactive_install_help_says_installed_skills_are_not_listed()
    {
        var install = CommandLineBuilder.Build().Subcommands.Single(command => command.Name == "install");

        var interactive = install.Options.Single(option => option.Name == "--interactive");

        Assert.Contains("aren't installed", interactive.Description);
        Assert.DoesNotContain("remove", interactive.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("install")]
    [InlineData("list")]
    [InlineData("uninstall")]
    public void No_command_offers_json_output(string command)
    {
        // Reports are for people. The manifest is the only machine-readable output.
        Assert.DoesNotContain(
            CommandLineBuilder.Build().Subcommands.Single(candidate => candidate.Name == command).Options,
            option => option.Name == "--json");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CommandLineBuilder.Invoke([command, "--json"], output, error);

        Assert.Equal(1, exitCode);
        Assert.Contains("Unrecognized command or argument '--json'", error.ToString());
    }

    [Theory]
    [InlineData("install")]
    [InlineData("list")]
    public void An_unknown_option_after_package_values_is_reported_as_unrecognized(string command)
    {
        // --package takes several values, so the parser hands it a trailing unknown option, such
        // as a --json left in an old script, as one more value.
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CommandLineBuilder.Invoke(
            [command, "--package", "Mockly@1.10.0", "--json"], output, error);

        Assert.Equal(1, exitCode);
        Assert.Contains("Unrecognized command or argument '--json'", error.ToString());
        Assert.DoesNotContain("missing a version", error.ToString());
    }

    [Fact]
    public void Uninstall_says_it_removes_from_the_destination_rather_than_copying_into_it()
    {
        var uninstall = CommandLineBuilder.Build()
            .Subcommands.Single(command => command.Name == "uninstall");

        var destination = uninstall.Options.Single(option => option.Name == "--destination");

        // The option is shared-looking but not shared: install's wording is about copying in,
        // which reads as nonsense on a command that only deletes.
        Assert.Contains("remove skills from", destination.Description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("copy", destination.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Uninstall_still_accepts_a_destination()
    {
        // Skills installed anywhere but the default are unreachable without it.
        Assert.Empty(CommandLineBuilder.Build().Parse(["uninstall", "-d", ".claude/skills"]).Errors);
    }

    [Fact]
    public void Install_still_says_it_copies_into_the_destination()
    {
        var install = CommandLineBuilder.Build()
            .Subcommands.Single(command => command.Name == "install");

        var destination = install.Options.Single(option => option.Name == "--destination");

        Assert.Contains("copy skills into", destination.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_removed_sync_verb_is_rejected()
    {
        // Renamed to install, which pairs with uninstall. A clean break at 0.1.0 rather than
        // an alias, so nothing has to carry the old name forward.
        var result = CommandLineBuilder.Build().Parse(["sync"]);

        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Install_rejects_the_removed_include_transitive_option()
    {
        var result = CommandLineBuilder.Build().Parse(["install", "--include-transitive"]);

        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Install_accepts_the_short_interactive_alias()
    {
        Assert.Empty(CommandLineBuilder.Build().Parse(["install", "-i"]).Errors);
    }

    [Fact]
    public void Install_accepts_interactive_alongside_a_named_package()
    {
        // One package can ship a dozen skills, so choosing among them is exactly the case
        // --package plus --interactive exists for.
        Assert.Empty(CommandLineBuilder.Build().Parse(["install", "--package", "Mockly@1.10.0", "-i"]).Errors);
        Assert.Empty(CommandLineBuilder.Build().Parse(["install", "-i", "--package", "Mockly@1.10.0"]).Errors);
    }

    [Fact]
    public void List_does_not_offer_interactive_selection()
    {
        // list writes nothing, so there is nothing to choose between.
        Assert.NotEmpty(CommandLineBuilder.Build().Parse(["list", "--interactive"]).Errors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Invalid_uninstall_filters_are_rejected_without_printing_terminal_controls(bool interactive)
    {
        const string Filter = "Safe\u001b]52;c;SECRET\aPackage";
        string[] args = interactive
            ? ["uninstall", "--interactive", "--package", Filter]
            : ["uninstall", "--package", Filter];
        var parsed = CommandLineBuilder.Build().Parse(args);
        Assert.Contains(parsed.Errors, error => error.Message.Contains(Filter, StringComparison.Ordinal));
        Assert.Equal(Filter, parsed.GetValue<string>("--package"));
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CommandLineBuilder.Invoke(args, output, error);

        Assert.Equal(1, exitCode);
        Assert.Contains("'SafePackage' is not a valid package id.", error.ToString());
        Assert.DoesNotContain('\u001b', error.ToString());
        Assert.DoesNotContain('\a', error.ToString());
        Assert.DoesNotContain("SECRET", error.ToString());
        Assert.Contains("Usage:", output.ToString());
    }

    [Fact]
    public void Uninstall_validation_keeps_multiline_guidance_readable()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CommandLineBuilder.Invoke(
            ["uninstall", "--package", "Mockly@1.*\u001b]52;c;SECRET\a"], output, error);

        Assert.Equal(1, exitCode);
        Assert.Contains(
            "'1.*' is a floating version or a version range, and this tool needs an exact version.\n" +
            "Write it out, for example --package Mockly@1.10.0.\n" +
            "To let restore choose the version, point at a project or solution with --source instead.",
            error.ToString().ReplaceLineEndings("\n"));
        Assert.DoesNotContain('\u001b', error.ToString());
        Assert.DoesNotContain("SECRET", error.ToString());
    }

    [Theory]
    [InlineData("--unknown\u001b]52;c;SECRET\a")]
    [InlineData("--unknown\u009d52;c;SECRET\u009c")]
    [InlineData("--dry-run=false\u001b]52;c;SECRET\u001b\\")]
    public void Framework_argument_errors_do_not_emit_terminal_controls(string token)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CommandLineBuilder.Invoke(["uninstall", token], output, error);

        Assert.Equal(1, exitCode);
        Assert.NotEmpty(error.ToString());
        Assert.DoesNotContain("SECRET", output.ToString() + error.ToString());
        Assert.DoesNotContain(output.ToString() + error.ToString(),
            character => char.IsControl(character) && character is not ('\r' or '\n'));
    }

    [Fact]
    public void Framework_typo_suggestions_are_sanitized_on_standard_output_too()
    {
        const string Token = "uninstal\u001b";
        var parsed = CommandLineBuilder.Build().Parse([Token]);
        Assert.Contains(Token, parsed.UnmatchedTokens);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CommandLineBuilder.Invoke([Token], output, error);

        Assert.Equal(1, exitCode);
        Assert.Contains("Did you mean", output.ToString());
        Assert.Contains("uninstall", output.ToString());
        Assert.Contains("Unrecognized", error.ToString());
        Assert.DoesNotContain('\u001b', output.ToString() + error.ToString());
    }

    [Theory]
    [InlineData("install", false)]
    [InlineData("install", true)]
    [InlineData("list", false)]
    [InlineData("list", true)]
    [InlineData("uninstall", false)]
    [InlineData("uninstall", true)]
    public void Overlength_selectors_fail_before_runner_or_destination_changes(
        string command, bool exact)
    {
        using var temp = new TempDirectory();
        var destination = temp.CreateDirectory("skills");
        var ownedPath = temp.CreateFile(Path.Combine("skills", "owned", "SKILL.md"), "keep");
        var manifest = new InstallManifest();
        manifest.SetSkills([new TrackedSkill("Keep.Package", "1.0.0", "owned")]);
        manifest.Save(destination);
        var manifestPath = Path.Combine(destination, InstallManifest.FileName);
        var beforeManifest = File.ReadAllBytes(manifestPath);
        var beforeSkill = File.ReadAllBytes(ownedPath);
        var beforePaths = Directory.GetFileSystemEntries(
            destination, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        var id = new string('A', 101);
        temp.CreatePackageWithSkill(id, "1.0.0", "usage");
        var target = temp.CreateFile("App.csproj");
        var runner = new FakeDotnet($$"""
            {"version":1,"projects":[{"frameworks":[{"topLevelPackages":[
              {"id":"{{id}}","resolvedVersion":"1.0.0"}
            ]}]}]}
            """);
        var arguments = new List<string>
        {
            command, "--package", exact ? $"{id}@1.0.0" : id, "--destination", destination,
        };
        if (command != "uninstall")
        {
            arguments.AddRange(["--source", target, "--global-packages", temp.Combine("packages")]);
        }

        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CommandLineBuilder.Invoke([.. arguments], runner, output, error);

        Assert.Equal(1, exitCode);
        Assert.Empty(runner.Invocations);
        Assert.Equal(beforeManifest, File.ReadAllBytes(manifestPath));
        Assert.Equal(beforeSkill, File.ReadAllBytes(ownedPath));
        Assert.Equal(
            beforePaths,
            Directory.GetFileSystemEntries(
                destination, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray());
    }

    [Theory]
    [InlineData("--help", null)]
    [InlineData("--version", null)]
    [InlineData("uninstall", "--help")]
    [InlineData("uninstall", "--missing")]
    [InlineData("uninstal", null)]
    public void Ordinary_framework_output_and_exit_codes_are_unchanged(string first, string? second)
    {
        string[] args = second is null ? [first] : [first, second];
        using var expectedOutput = new StringWriter();
        using var expectedError = new StringWriter();
        var expectedExitCode = CommandLineBuilder.Build().Parse(args).Invoke(new InvocationConfiguration
        {
            Output = expectedOutput,
            Error = expectedError,
        });
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CommandLineBuilder.Invoke(args, output, error);

        Assert.Equal(expectedExitCode, exitCode);
        Assert.Equal(expectedOutput.ToString(), output.ToString());
        Assert.Equal(expectedError.ToString(), error.ToString());
    }
}

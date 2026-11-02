using DotnetPackageSkills.Cli;
using DotnetPackageSkills.Infrastructure;
using DotnetPackageSkills.NuGet;
using DotnetPackageSkills.Skills;

namespace DotnetPackageSkills.Tests;

public class PackageListingProgressTests
{
    private const string Report =
        """{"projects":[{"frameworks":[{"topLevelPackages":[{"id":"Mockly","resolvedVersion":"1.0.0"}]}]}]}""";

    [Fact]
    public async Task The_activity_remains_active_until_the_package_list_process_returns()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var active = 0;
        var stopped = 0;
        var runner = new CallbackRunner((arguments, workingDirectory) =>
        {
            Assert.Equal(["list", "App.csproj", "package", "--no-restore", "--format", "json"], arguments);
            Assert.Equal(Path.GetDirectoryName("App.csproj"), workingDirectory);
            Assert.Equal(1, Volatile.Read(ref active));
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return new ProcessResult(0, Report, "");
        });
        var lister = new PackageLister(new DotnetCli(runner), () =>
        {
            Interlocked.Increment(ref active);
            return new Scope(() =>
            {
                Interlocked.Decrement(ref active);
                Interlocked.Increment(ref stopped);
            });
        });
        var listing = Task.Run(() => lister.List("App.csproj"));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.False(listing.IsCompleted);
            Assert.Equal(1, Volatile.Read(ref active));
            Assert.Equal(0, Volatile.Read(ref stopped));
        }
        finally
        {
            release.Set();
            await listing.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(0, active);
        Assert.Equal(1, stopped);
        Assert.Equal("Mockly", Assert.Single(await listing).Id);
    }

    [Theory]
    [InlineData(0, Report, null)]
    [InlineData(1, "", "SDK failure")]
    [InlineData(0, "not json", "Could not parse")]
    public void Results_and_errors_keep_their_behavior_and_always_end_the_scope(
        int exitCode, string output, string? expectedError)
    {
        var events = new List<string>();
        var runner = new CallbackRunner((_, _) =>
        {
            Assert.Equal(["start"], events);
            events.Add("run");
            return new ProcessResult(exitCode, output, "SDK failure");
        });
        var lister = new PackageLister(new DotnetCli(runner), () =>
        {
            events.Add("start");
            return new Scope(() => events.Add("stop"));
        });

        if (expectedError is null)
        {
            Assert.Equal("Mockly", Assert.Single(lister.List("App.csproj")).Id);
        }
        else
        {
            var error = Assert.Throws<PackageSkillsException>(() => lister.List("App.csproj"));
            Assert.Contains(expectedError, error.Message);
        }

        Assert.Equal(["start", "run", "stop"], events);
    }

    [Theory]
    [InlineData("Could not start the SDK.")]
    [InlineData("The SDK did not finish within the timeout.")]
    public void Launch_failures_and_timeouts_end_the_scope_without_replacing_the_exception(string message)
    {
        var stopped = false;
        var failure = new ProcessExecutionException(message);
        var runner = new CallbackRunner((_, _) => throw failure);
        var lister = new PackageLister(new DotnetCli(runner), () => new Scope(() => stopped = true));

        Assert.Same(failure, Assert.Throws<ProcessExecutionException>(() => lister.List("App.csproj")));
        Assert.True(stopped);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Display_failures_do_not_prevent_a_successful_sdk_lookup(bool failDuringStartup)
    {
        var terminal = FailingDisplay(failDuringStartup);
        using var error = new StringWriter();
        var runner = new CallbackRunner((_, _) => new ProcessResult(0, Report, ""));
        var lister = new PackageLister(new DotnetCli(runner),
            () => new PackageListingIndicator(terminal, TimeProvider.System, error: error));

        Assert.Equal("Mockly", Assert.Single(lister.List("App.csproj")).Id);
        Assert.Contains("Warning: could not display package-reference progress:", error.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Display_failures_do_not_replace_sdk_error_diagnostics(bool failDuringStartup)
    {
        var terminal = FailingDisplay(failDuringStartup);
        using var error = new StringWriter();
        var runner = new CallbackRunner((_, _) => new ProcessResult(1, "", "Original SDK diagnostics."));
        var lister = new PackageLister(new DotnetCli(runner),
            () => new PackageListingIndicator(terminal, TimeProvider.System, error: error));

        var failure = Assert.Throws<PackageSkillsException>(() => lister.List("App.csproj"));

        Assert.Contains("Original SDK diagnostics.", failure.Message);
        Assert.Contains("Warning: could not display package-reference progress:", error.ToString());
    }

    [Theory]
    [InlineData(true, "Could not start the SDK.")]
    [InlineData(false, "Could not start the SDK.")]
    [InlineData(true, "The SDK did not finish within the timeout.")]
    [InlineData(false, "The SDK did not finish within the timeout.")]
    public void Display_failures_do_not_replace_sdk_launch_or_timeout_exceptions(
        bool failDuringStartup, string message)
    {
        var terminal = FailingDisplay(failDuringStartup);
        using var error = new StringWriter();
        var failure = new ProcessExecutionException(message);
        var runner = new CallbackRunner((_, _) => throw failure);
        var lister = new PackageLister(new DotnetCli(runner),
            () => new PackageListingIndicator(terminal, TimeProvider.System, error: error));

        Assert.Same(failure, Assert.Throws<ProcessExecutionException>(() => lister.List("App.csproj")));
        Assert.Contains("Warning: could not display package-reference progress:", error.ToString());
    }

    [Fact]
    public void An_unavailable_warning_writer_does_not_replace_the_sdk_exception()
    {
        var terminal = FailingDisplay(failDuringStartup: false);
        using var error = new UnavailableErrorWriter();
        var failure = new ProcessExecutionException("Original SDK launch failure.");
        var runner = new CallbackRunner((_, _) => throw failure);
        var lister = new PackageLister(new DotnetCli(runner),
            () => new PackageListingIndicator(terminal, TimeProvider.System, error: error));

        Assert.Same(failure, Assert.Throws<ProcessExecutionException>(() => lister.List("App.csproj")));
    }

    [Fact]
    public void Suppressed_activity_can_return_no_scope()
    {
        var starts = 0;
        var runner = new CallbackRunner((_, _) => new ProcessResult(0, Report, ""));
        var lister = new PackageLister(new DotnetCli(runner), () =>
        {
            starts++;
            return null;
        });

        Assert.Single(lister.List("App.csproj"));
        Assert.Equal(1, starts);
    }

    [Theory]
    [InlineData("install", false)]
    [InlineData("install", true)]
    [InlineData("list", false)]
    [InlineData("list", true)]
    [InlineData("uninstall", false)]
    public void CLI_actions_scope_all_actual_reference_reading_paths(string command, bool barePackage)
    {
        using var temp = new TempDirectory();
        var target = temp.CreateFile("App.csproj");
        temp.CreatePackageWithSkill("Mockly", "1.0.0", "usage");
        var active = false;
        var starts = 0;
        var stops = 0;
        var invocations = 0;
        var runner = new CallbackRunner((arguments, workingDirectory) =>
        {
            Assert.True(active);
            Assert.Equal(["list", target, "package", "--no-restore", "--format", "json"], arguments);
            Assert.Equal(temp.Path, workingDirectory);
            invocations++;
            return new ProcessResult(0, Report, "");
        });
        var arguments = new List<string> { command, "-s", target, "-d", temp.Combine("skills") };
        if (command == "uninstall")
        {
            arguments.AddRange(["--stale", "--dry-run"]);
        }
        else
        {
            arguments.AddRange(["--global-packages", temp.Combine("packages")]);
            if (barePackage) { arguments.AddRange(["-p", "Mockly"]); }
            if (command == "install") { arguments.Add("--dry-run"); }
        }
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CommandLineBuilder.Invoke([.. arguments], runner, output, error, () =>
        {
            Assert.False(active);
            active = true;
            starts++;
            return new Scope(() =>
            {
                active = false;
                stops++;
            });
        });

        Assert.Equal(0, exitCode);
        Assert.Empty(error.ToString());
        Assert.Equal(1, starts);
        Assert.Equal(1, stops);
        Assert.Equal(1, invocations);
        Assert.False(active);
        Assert.False(Directory.Exists(temp.Combine("skills")));
    }

    [Theory]
    [InlineData("install", false)]
    [InlineData("install", true)]
    [InlineData("list", false)]
    [InlineData("list", true)]
    [InlineData("uninstall", false)]
    public void No_reference_read_means_no_activity(string command, bool explicitVersion)
    {
        using var temp = new TempDirectory();
        temp.CreatePackageWithSkill("Mockly", "1.0.0", "usage");
        var destination = temp.CreateDirectory("skills");
        var manifest = new InstallManifest();
        manifest.SetSkills([new TrackedSkill("Mockly", "1.0.0", "usage")]);
        manifest.Save(destination);
        var runner = new CallbackRunner((_, _) => throw new InvalidOperationException("The SDK must not run."));
        var arguments = new List<string> { command, "-d", destination };
        if (command == "uninstall")
        {
            arguments.Add("--dry-run");
        }
        else
        {
            arguments.AddRange([
                "-p", explicitVersion ? "Mockly@1.0.0" : "Mockly",
                "-s", temp.Combine("missing.csproj"),
                "--global-packages", temp.Combine("packages"),
            ]);
            if (command == "install") { arguments.Add("--dry-run"); }
        }
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CommandLineBuilder.Invoke([.. arguments], runner, output, error,
            () => throw new InvalidOperationException("The loading indicator must not start."));

        Assert.Equal(0, exitCode);
        Assert.Empty(error.ToString());
    }

    private static FakeTerminal FailingDisplay(bool failDuringStartup) => new()
    {
        BeforeOperation = operation =>
        {
            if (failDuringStartup
                ? operation is nameof(ITerminal.Write) or nameof(ITerminal.WriteStatusLine)
                : operation == nameof(ITerminal.ClearStatusLine))
            {
                throw new IOException("Terminal display failed.");
            }
        },
    };

    private sealed class UnavailableErrorWriter : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void WriteLine(string? value) => throw new IOException("stderr unavailable.");
    }

    private sealed class Scope(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    private sealed class CallbackRunner(Func<IReadOnlyList<string>, string?, ProcessResult> run) : IProcessRunner
    {
        public ProcessResult Run(string fileName, IReadOnlyList<string> arguments, string? workingDirectory = null) =>
            run(arguments, workingDirectory);
    }
}

using DotnetPackageSkills.Infrastructure;
using DotnetPackageSkills.NuGet;
using DotnetPackageSkills.Skills;

namespace DotnetPackageSkills;

/// <summary>Inputs for an install or a list.</summary>
public sealed record InstallRequest
{
    /// <summary>Where skills go when no destination is given.</summary>
    public const string DefaultDestination = ".agents/skills";

    /// <summary>
    /// Solution or project to inspect, or to resolve bare package IDs that the manifest does not track.
    /// </summary>
    public string? Target { get; init; }

    /// <summary>
    /// Packages to inspect, with exact versions or bare IDs resolved from the manifest or target.
    /// </summary>
    public IReadOnlyList<PackageCoordinate> Packages { get; init; } = [];

    public required string Destination { get; init; }
    public required string WorkingDirectory { get; init; }

    /// <summary>An explicit cache path, resolved relative to <see cref="WorkingDirectory"/>.</summary>
    public string? GlobalPackagesOverride { get; init; }
    public bool DryRun { get; init; }
}

/// <summary>What an install or a list produced.</summary>
public sealed record InstallResult
{
    /// <summary>The solution or project inspected, or null when packages were named explicitly.</summary>
    public string? Target { get; init; }

    public required string GlobalPackagesFolder { get; init; }
    public required string Destination { get; init; }
    public required int PackagesScanned { get; init; }
    public required bool DryRun { get; init; }
    public required IReadOnlyList<BundledSkill> Skills { get; init; }

    /// <summary>
    /// How many skills discovery turned up, which stays put even after <see cref="Skills"/> is
    /// narrowed to what was actually installed. Without it a report cannot tell "no package ships
    /// a skill" apart from "you chose none of the ones that do".
    /// </summary>
    public int SkillsDiscovered { get; init; }

    public IReadOnlyList<TrackedSkill> Removed { get; init; } = [];
    public IReadOnlyList<SkippedSkill> Skipped { get; init; } = [];
    public IReadOnlyList<IgnoredPackage> Ignored { get; init; } = [];
    public IReadOnlyList<PackageVersionWarning> Warnings { get; init; } = [];
    public IReadOnlyList<SkillCollision> Errors { get; init; } = [];

    /// <summary>
    /// Installed skills whose package the target no longer references. Install keeps them, and
    /// the report points at uninstall's <c>--stale</c> option for removing them.
    /// </summary>
    public IReadOnlyList<TrackedSkill> Unreferenced { get; init; } = [];

    /// <summary>
    /// Set when an interactive install found skills but every one is installed already or
    /// skipped, so there was nothing to choose from and no checklist was shown.
    /// </summary>
    public bool NothingNewToInstall { get; init; }

    internal IReadOnlyList<PackageReferenceInfo> ResolvedPackages { get; init; } = [];
    internal IReadOnlyList<BundledSkill>? AllCandidates { get; init; }
}

/// <summary>The skills the user checked. Installing a choice copies these and changes nothing else.</summary>
public sealed record SkillChoice(IReadOnlyList<BundledSkill> Selected)
{
    public IReadOnlyCollection<TrackedSkill>? ExpectedInstalled { get; init; }
}

/// <summary>Ties package listing, skill discovery, and installation together.</summary>
public sealed class SkillInstallService(DotnetCli dotnet, SkillInstaller installer)
{
    internal const string StaleSkillAdvice =
        "Use uninstall with --stale to remove skills that no longer match the project.";

    public SkillInstallService(IProcessRunner runner) : this(new DotnetCli(runner), new SkillInstaller())
    {
    }

    /// <summary>Discovers bundled skills without writing anything.</summary>
    public InstallResult Discover(InstallRequest request)
    {
        return request.Packages.Count > 0
            ? DiscoverFromCoordinates(request)
            : DiscoverFromTarget(request);
    }

    private InstallResult DiscoverFromTarget(InstallRequest request)
    {
        var target = TargetLocator.Resolve(request.Target, request.WorkingDirectory);

        // Ask for the NuGet global-packages directory from the repository, not from wherever the user
        // happened to invoke the tool: nuget.config discovery walks up from the current
        // directory, and a repo-level config is exactly the case worth honouring.
        var globalPackages = LocateGlobalPackages(request, Path.GetDirectoryName(target));

        // Every version can contribute distinct skills; keep the complete reference graph.
        var packages = new PackageLister(dotnet).List(target);

        return Build(request, target, globalPackages, packages);
    }

    private InstallResult DiscoverFromCoordinates(InstallRequest request)
    {
        var (resolved, resolutionTarget) = ResolveCoordinates(request);
        var packages = resolved.DistinctBy(package =>
            (package.Id.ToLowerInvariant(), PackagePathResolver.NormalizeVersion(package.Version))).ToArray();
        var globalPackages = LocateGlobalPackages(
            request,
            resolutionTarget is null ? request.WorkingDirectory : Path.GetDirectoryName(resolutionTarget));

        return Build(request, target: null, globalPackages, packages);
    }

    private (IReadOnlyList<PackageReferenceInfo> Packages, string? Target) ResolveCoordinates(InstallRequest request)
    {
        InstallManifest? manifest = null;
        var destination = Path.GetFullPath(request.Destination, request.WorkingDirectory);
        if (request.Packages.Any(package => package.Version is null))
        {
            using var destinationLock = DestinationLock.Acquire(destination);
            manifest = InstallManifest.Load(destination);
        }

        var packages = new List<PackageReferenceInfo>(request.Packages.Count);
        var unresolved = new List<PackageCoordinate>();
        foreach (var package in request.Packages)
        {
            PackageCoordinate.ValidateId(package.Id);
            var version = package.Version;
            if (version is null)
            {
                if (manifest is null || !manifest.Packages.TryGetValue(package.Id.ToLowerInvariant(), out var tracked))
                {
                    unresolved.Add(package);
                    continue;
                }

                packages.AddRange(tracked.Versions.Keys.Select(trackedVersion =>
                    new PackageReferenceInfo(package.Id, trackedVersion)));
                continue;
            }

            packages.Add(new PackageReferenceInfo(package.Id, version));
        }

        if (unresolved.Count == 0)
        {
            return (packages, null);
        }

        string target;
        try
        {
            target = TargetLocator.Resolve(request.Target, request.WorkingDirectory);
        }
        catch (PackageSkillsException error) when (string.IsNullOrWhiteSpace(request.Target))
        {
            var ids = unresolved
                .Select(package => package.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var label = ids.Length == 1
                ? $"bare package ID '{ids[0]}'"
                : $"bare package IDs {string.Join(", ", ids.Select(id => $"'{id}'"))}";

            throw new PackageSkillsException(
                $"Could not resolve {label} because target resolution was unavailable. " +
                $"{error.Message} Pass --target to select a solution or project, or specify ID@VERSION.",
                error);
        }

        var targetPackages = new PackageLister(dotnet).List(target);

        foreach (var package in unresolved)
        {
            var versions = targetPackages
                .Where(candidate => candidate.Id.Equals(package.Id, StringComparison.OrdinalIgnoreCase))
                .GroupBy(candidate => PackagePathResolver.NormalizeVersion(candidate.Version))
                .Select(group => new PackageReferenceInfo(group.First().Id, group.First().Version))
                .OrderBy(candidate => PackagePathResolver.NormalizeVersion(candidate.Version), StringComparer.Ordinal)
                .ToList();

            if (versions.Count == 0)
            {
                throw new PackageSkillsException(
                    $"Package '{package.Id}' is not a direct package resolved for target '{target}'. " +
                    "Specify ID@VERSION to choose an exact version. No skills were changed.");
            }

            packages.AddRange(versions.Select(version => new PackageReferenceInfo(package.Id, version.Version)));
        }

        return (packages, target);
    }

    private string LocateGlobalPackages(InstallRequest request, string? preferredDirectory)
    {
        var explicitOverride = string.IsNullOrWhiteSpace(request.GlobalPackagesOverride)
            ? request.GlobalPackagesOverride
            : Path.GetFullPath(request.GlobalPackagesOverride, request.WorkingDirectory);
        return new GlobalPackagesLocator(dotnet).Locate(
            explicitOverride,
            preferredDirectory ?? request.WorkingDirectory);
    }

    /// <remarks>
    /// A package missing from the NuGet global-packages directory contributes nothing, like one
    /// that ships no skills. Extracting packages is restore's job, not this tool's. Only a target
    /// install checks for missing packages, because it would otherwise report their skills as stale.
    /// </remarks>
    private static (List<BundledSkill> Candidates, List<IgnoredPackage> Ignored) Collect(
        string globalPackages,
        IReadOnlyList<PackageReferenceInfo> packages)
    {
        var candidates = new List<BundledSkill>();
        var ignored = new List<IgnoredPackage>();

        foreach (var (id, version) in packages.OrderBy(package => package.Id, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(package => PackagePathResolver.NormalizeVersion(package.Version), StringComparer.Ordinal))
        {
            var packageDirectory = PackagePathResolver.Resolve(globalPackages, id, version);

            if (packageDirectory is null)
            {
                continue;
            }

            var skills = SkillDiscovery.Discover(packageDirectory, id, version);
            candidates.AddRange(skills);
            if (skills.Count == 0)
            {
                ignored.Add(new IgnoredPackage(id, PackagePathResolver.NormalizeVersion(version), "no valid bundled skills"));
            }
        }

        return (candidates, ignored);
    }

    private static InstallResult Build(
        InstallRequest request,
        string? target,
        string globalPackages,
        IReadOnlyList<PackageReferenceInfo> packages)
    {
        var (candidates, ignored) = Collect(globalPackages, packages);
        var classified = SkillCandidateClassifier.Classify(candidates);
        var warnings = candidates
            .GroupBy(skill => skill.PackageId, StringComparer.OrdinalIgnoreCase)
            .Select(group => new PackageVersionWarning(
                group.First().PackageId,
                group.Select(skill => PackagePathResolver.NormalizeVersion(skill.PackageVersion))
                    .Distinct(StringComparer.Ordinal).OrderBy(version => version, StringComparer.Ordinal).ToArray()))
            .Where(warning => warning.Versions.Count > 1)
            .OrderBy(warning => warning.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new InstallResult
        {
            Target = target,
            GlobalPackagesFolder = globalPackages,
            Destination = Path.GetFullPath(request.Destination, request.WorkingDirectory),
            PackagesScanned = packages.Count,
            DryRun = request.DryRun,
            Skills = classified.Valid,
            SkillsDiscovered = candidates.Count,
            Skipped = classified.Skipped,
            Ignored = ignored,
            Warnings = warnings,
            Errors = classified.Collisions,
            ResolvedPackages = packages,
            AllCandidates = candidates,
        };
    }

    /// <summary>Discovers bundled skills and copies them into the destination.</summary>
    public InstallResult Install(InstallRequest request) => Install(request, Discover(request), choice: null);

    /// <summary>
    /// Copies a caller-chosen subset of already-discovered skills, which is what the interactive
    /// picker produces. Passing a null <paramref name="choice"/> installs everything discovered.
    /// </summary>
    public InstallResult Install(InstallRequest request, InstallResult discovered, SkillChoice? choice)
    {
        if (request.Packages.Count == 0)
        {
            RequireEveryPackageInDirectory(discovered);
        }

        // A choice only adds: it never refreshes or removes what it was not asked about. Without
        // one, cleanup requires inspecting every selected version of a package: a cache gap
        // cannot establish that the package stopped shipping an installed skill.
        var offered = choice is null
            ? discovered.ResolvedPackages
                .GroupBy(package => package.Id, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.All(package =>
                    PackagePathResolver.Resolve(discovered.GlobalPackagesFolder, package.Id, package.Version) is not null))
                .ToDictionary(group => group.Key,
                    group => (IReadOnlyCollection<string>)group.Select(package => package.Version).ToArray(),
                    StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase);

        var outcome = installer.Install(
            discovered.Destination,
            choice is null
                ? discovered.AllCandidates ?? discovered.Skills
                : discovered.Skills.Where(skill => choice.Selected.Contains(skill)).ToArray(),
            request.DryRun,
            offered,
            choice?.ExpectedInstalled);

        return discovered with
        {
            DryRun = request.DryRun,
            Skills = outcome.Installed,
            Removed = outcome.Removed,
            Skipped = [.. discovered.Skipped.Concat(outcome.Skipped).DistinctBy(skill =>
                (skill.PackageId.ToLowerInvariant(), PackagePathResolver.NormalizeVersion(skill.PackageVersion),
                    skill.RelativePath, skill.SkillName))],
            Errors = [.. discovered.Errors.Concat(outcome.Errors)
                .DistinctBy(error => error.RelativePath, StringComparer.OrdinalIgnoreCase)],
            // A target lists every package it references, so anything it did not offer has left
            // the project. Named packages say nothing about the rest, so they report nothing.
            Unreferenced = request.Packages.Count == 0 && choice is null ? outcome.Untouched : [],
            AllCandidates = null,
        };
    }

    /// <summary>
    /// Stops a target install when a referenced package is missing from the NuGet global-packages directory.
    /// </summary>
    /// <remarks>
    /// Restoring is not this tool's job, but carrying on would read an unextracted package as
    /// one that ships nothing and report its installed skills as no longer referenced. Asking
    /// for a restore is the honest answer. Named packages never reach this check: naming one
    /// missing from the NuGet global-packages directory simply finds no skills.
    /// </remarks>
    private static void RequireEveryPackageInDirectory(InstallResult discovered)
    {
        var missing = discovered.ResolvedPackages
            .Where(package => PackagePathResolver.Resolve(discovered.GlobalPackagesFolder, package.Id, package.Version) is null)
            .Select(package => $"{package.Id} {package.Version}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (missing.Length > 0)
        {
            throw new PackageSkillsException(
                "Cannot install skills because resolved packages are missing from the NuGet global-packages directory " +
                $"'{discovered.GlobalPackagesFolder}': " +
                $"{string.Join(", ", missing)}. " +
                "Run dotnet restore for the target using this NuGet global-packages directory, then try again. " +
                "No skills were changed.");
        }
    }

    /// <summary>
    /// Runs every check that stops an interactive install before a checklist opens, and returns
    /// what it can offer: the skills that would install cleanly and are not installed yet.
    /// </summary>
    /// <remarks>
    /// An interactive install only adds, so it cannot settle a mismatch between the installed
    /// skills and the packages. With a target, any stale skill stops it until
    /// <c>uninstall --stale</c> removes them. Named packages can add disjoint skills from another
    /// version without changing existing ownership.
    /// </remarks>
    internal InstallResult PrepareInteractiveInstall(
        InstallRequest request,
        InstallResult discovered,
        IReadOnlyCollection<TrackedSkill> installed)
    {
        if (request.Packages.Count == 0)
        {
            RequireEveryPackageInDirectory(discovered);
            RequireNoStaleSkills(discovered, installed);
        }
        var preview = Install(
            request with { DryRun = true },
            discovered,
            new SkillChoice(discovered.Skills) { ExpectedInstalled = installed });
        var tracked = installed.Select(entry => entry.Skill).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return preview with
        {
            DryRun = request.DryRun,
            Skills = [.. preview.Skills.Where(skill => !tracked.Contains(skill.RelativePath))],
        };
    }

    private static void RequireNoStaleSkills(
        InstallResult discovered,
        IReadOnlyCollection<TrackedSkill> installed)
    {
        var stale = installed
            .Where(entry => SkillInstaller.IsStale(entry, discovered.ResolvedPackages))
            .OrderBy(entry => entry.Skill, StringComparer.Ordinal)
            .ToList();

        if (stale.Count == 0)
        {
            return;
        }

        throw new PackageSkillsException(
            $"Cannot choose skills interactively because {stale.Count} installed " +
            $"{(stale.Count == 1 ? "skill doesn't" : "skills don't")} match the target: " +
            $"{string.Join(", ", stale.Select(entry => $"{entry.Skill} ({entry.Package} {entry.Version})"))}. " +
            StaleSkillAdvice + " Then try again. " +
            "No skills were changed.");
    }

    /// <summary>Skill folder names the manifest in <paramref name="destination"/> already tracks.</summary>
    public static IReadOnlySet<string> InstalledSkillNames(string destination) =>
        InstalledSkills(destination, Directory.GetCurrentDirectory())
            .Select(entry => entry.Skill)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Everything the manifest tracks, in the order a list should show it.
    /// </summary>
    /// <remarks>
    /// This is what uninstall offers to choose from. It reads the manifest rather than the
    /// folder, so skills the user wrote themselves are never on the list — the same reason
    /// removal is manifest-driven in the first place.
    /// </remarks>
    public static IReadOnlyList<TrackedSkill> InstalledSkills(string destination, string workingDirectory)
    {
        var root = Path.GetFullPath(destination, workingDirectory);
        using var destinationLock = DestinationLock.Acquire(root);
        return
        [
            .. InstallManifest.Load(root)
                .EnumerateSkills()
                .OrderBy(entry => entry.Skill, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Skill, StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// Removes skills this tool installed, optionally limited to one package, one exact
    /// version, the names the caller chose, or the skills that are stale against a target.
    /// </summary>
    public IReadOnlyList<TrackedSkill> Uninstall(
        string destination,
        string workingDirectory,
        string? packageId,
        string? packageVersion,
        bool dryRun,
        IReadOnlyCollection<string>? only = null,
        IReadOnlyCollection<TrackedSkill>? expectedInstalled = null,
        IReadOnlyCollection<PackageReferenceInfo>? staleAgainst = null)
    {
        var root = Path.GetFullPath(destination, workingDirectory);
        return installer.Uninstall(root, packageId, packageVersion, dryRun, only, expectedInstalled, staleAgainst);
    }

    /// <summary>
    /// Finds the target and lists its direct package references with <c>dotnet list package</c>.
    /// This is all <c>uninstall --stale</c> reads: deciding which skills are stale needs the
    /// references, not the packages, so the tool never searches the NuGet global-packages directory.
    /// </summary>
    public TargetReferences ReadReferences(string? target, string workingDirectory)
    {
        var resolved = TargetLocator.Resolve(target, workingDirectory);
        return new TargetReferences(resolved, new PackageLister(dotnet).List(resolved));
    }

}

/// <summary>A solution or project and the package versions it references directly.</summary>
public sealed record TargetReferences(string Target, IReadOnlyList<PackageReferenceInfo> Packages);

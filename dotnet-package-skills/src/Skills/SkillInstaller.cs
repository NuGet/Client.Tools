using DotnetPackageSkills.NuGet;
using NuGet.Versioning;

namespace DotnetPackageSkills.Skills;

/// <summary>Outcome of an install.</summary>
public sealed record InstallOutcome(
    IReadOnlyList<BundledSkill> Installed,
    IReadOnlyList<TrackedSkill> Removed,
    IReadOnlyList<SkippedSkill> Skipped)
{
    /// <summary>Tracked skills whose package this run did not offer. They were left exactly as they were.</summary>
    public IReadOnlyList<TrackedSkill> Untouched { get; init; } = [];
    public IReadOnlyList<SkillCollision> Errors { get; init; } = [];
}

/// <summary>Copies discovered skills into the destination and keeps the manifest in step.</summary>
public sealed class SkillInstaller
{
    /// <summary>
    /// Copies every skill into <paramref name="destinationRoot"/>, refreshing the ones this tool
    /// already installed.
    /// </summary>
    /// <param name="offered">
    /// The inspected versions for each package this run covers, by package id. A tracked
    /// skill is removed only when its owning version is no longer offered and its package no longer
    /// ships it, which is what lets an upgrade drop a skill instead of keeping a stale copy.
    /// Tracked skills of packages that are not offered are left alone and reported as
    /// untouched: a package leaving the project is not a reason to delete its skills here, and
    /// that cleanup belongs to <c>uninstall --stale</c>. Pass an empty map to only add skills.
    /// Null offers the packages of <paramref name="skills"/> at their versions.
    /// </param>
    public InstallOutcome Install(
        string destinationRoot,
        IReadOnlyList<BundledSkill> skills,
        bool dryRun,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>>? offered = null,
        IReadOnlyCollection<TrackedSkill>? expectedInstalled = null)
    {
        // Package ids compare without regard to case, whatever comparer the caller's map uses:
        // the manifest spells them in lowercase, and packages keep NuGet's casing.
        var offers = offered ?? skills
                .GroupBy(skill => skill.PackageId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key,
                    group => (IReadOnlyCollection<string>)group.Select(skill => skill.PackageVersion).ToArray(),
                    StringComparer.OrdinalIgnoreCase);
        var versions = offers.ToDictionary(pair => pair.Key,
            pair => pair.Value.Select(PackagePathResolver.NormalizeVersion).ToHashSet(StringComparer.Ordinal),
            StringComparer.OrdinalIgnoreCase);
        bool VersionWasReplaced(TrackedSkill entry) =>
            versions.TryGetValue(entry.Package, out var offeredVersions) && !offeredVersions.Contains(entry.Version);

        using var destinationLock = DestinationLock.Acquire(destinationRoot);
        var manifest = InstallManifest.Load(destinationRoot);
        var trackedSkills = manifest.EnumerateSkills().ToList();
        CheckOwnershipSnapshot(trackedSkills, expectedInstalled);
        var classification = SkillCandidateClassifier.Classify(skills);
        var accepted = new List<BundledSkill>();
        var skipped = new List<SkippedSkill>(classification.Skipped);
        var errors = new List<SkillCollision>(classification.Collisions);
        var protectedPaths = classification.Collisions
            .Select(collision => collision.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var blockedFiles = new List<TrackedSkill>();
        var reusableDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var skill in classification.Valid)
        {
            var tracked = trackedSkills.FirstOrDefault(entry =>
                entry.Skill.Equals(skill.RelativePath, StringComparison.OrdinalIgnoreCase));
            var destination = ToAbsolute(destinationRoot, skill.RelativePath);

            if (tracked is not null && !HasSameOwner(tracked, skill))
            {
                var reason = $"the destination folder is managed for {tracked.Package} {tracked.Version} " +
                             $"skill '{tracked.Skill}'; use uninstall with --package to remove the current owner's skills " +
                             "before replacing it";
                skipped.Add(SkillCandidateClassifier.ToSkipped(skill, reason));
                errors.Add(new SkillCollision(skill.RelativePath, [skill], reason));
                protectedPaths.Add(tracked.Skill);
                continue;
            }

            if (File.Exists(destination))
            {
                skipped.Add(SkillCandidateClassifier.ToSkipped(skill, "the destination path already exists as a file"));
                if (tracked is not null)
                {
                    protectedPaths.Add(tracked.Skill);
                    if (VersionWasReplaced(tracked)) { blockedFiles.Add(tracked); }
                }

                continue;
            }

            var destinationDirectory = new DirectoryInfo(destination);
            if (tracked is null &&
                (destinationDirectory.Exists || destinationDirectory.LinkTarget is not null))
            {
                if (!IsReusableEmptyDirectory(destinationDirectory))
                {
                    skipped.Add(SkillCandidateClassifier.ToSkipped(
                        skill,
                        "the destination folder already exists and is not managed by this tool"));
                    continue;
                }

                reusableDestinations.Add(skill.RelativePath);
            }

            accepted.Add(skill);
        }

        if (blockedFiles.Count > 0)
        {
            throw CannotReplaceTrackedSkills(blockedFiles, versions);
        }

        var current = accepted.Select(skill => skill.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A skill involved in a conflict is never removed by the same run.
        var removed = trackedSkills
            .Where(entry => !current.Contains(entry.Skill) && !protectedPaths.Contains(entry.Skill))
            .Where(VersionWasReplaced)
            .OrderBy(entry => entry.Skill, StringComparer.Ordinal)
            .ToList();
        var removedPaths = removed.Select(entry => ToAbsolute(destinationRoot, entry.Skill)).ToList();
        var untouched = trackedSkills
            .Where(entry => !versions.ContainsKey(entry.Package))
            .OrderBy(entry => entry.Skill, StringComparer.Ordinal)
            .ToList();

        foreach (var skill in accepted)
        {
            if (!Directory.Exists(skill.SourcePath) ||
                !File.Exists(Path.Combine(skill.SourcePath, SkillDiscovery.SkillManifestFileName)))
            {
                throw new PackageSkillsException(
                    $"The source for skill '{skill.SkillName}' is no longer available at '{skill.SourcePath}'. " +
                    "Restore its package and run the command again. No skills were changed.");
            }
        }

        // Skipped paths keep their exact ownership, even when that version is no longer offered.
        var kept = trackedSkills
            .Where(entry => !current.Contains(entry.Skill) && !removed.Contains(entry));
        var installed = accepted.Select(skill =>
            new TrackedSkill(skill.PackageId, skill.PackageVersion, skill.SkillName));

        // Build the new ownership record before touching any file, so a record that breaks a
        // manifest rule stops the operation while the destination is still unchanged.
        manifest.SetSkills(kept.Concat(installed));

        var outcome = new InstallOutcome(accepted, removed, skipped) { Untouched = untouched, Errors = errors };

        if (dryRun)
        {
            return outcome;
        }

        foreach (var path in removedPaths)
        {
            RemoveSkillDirectory(path);
        }

        foreach (var skill in accepted)
        {
            CopyDirectory(
                skill.SourcePath,
                ToAbsolute(destinationRoot, skill.RelativePath),
                reusableDestinations.Contains(skill.RelativePath));
        }

        if (manifest.IsEmpty)
        {
            // Nothing is tracked, so there is nothing for the manifest to be the source of truth
            // about. Match uninstall rather than leaving an empty manifest, and a destination
            // folder, that the user never asked for. The folder only goes if it is empty, so
            // skills they wrote themselves keep it alive.
            InstallManifest.Delete(destinationRoot);
            TryRemoveEmptyDirectory(destinationRoot);
        }
        else
        {
            manifest.Save(destinationRoot);
        }

        return outcome;
    }

    internal static bool SameVersion(string left, string right) =>
        VersionComparer.VersionRelease.Equals(PackageCoordinate.ParseVersion(left), PackageCoordinate.ParseVersion(right));

    private static PackageSkillsException CannotReplaceTrackedSkills(
        IReadOnlyList<TrackedSkill> blocked,
        IReadOnlyDictionary<string, HashSet<string>> versions)
    {
        string OwnerId(TrackedSkill entry) =>
            versions.Keys.First(id => id.Equals(entry.Package, StringComparison.OrdinalIgnoreCase));

        var reasons = blocked.Select(entry =>
            $"{OwnerId(entry)} {string.Join(", ", versions[entry.Package].OrderBy(version => version, StringComparer.Ordinal))} " +
            $"cannot replace installed skill '{entry.Skill}' " +
            "because its destination path is a file");
        return new PackageSkillsException(
            $"Cannot install skills because {string.Join("; ", reasons)}. " +
            "Remove or rename each blocking file, or use uninstall with --package to remove the package's " +
            "installed skills, then try again. No skills were changed.");
    }

    /// <summary>
    /// A tracked skill is stale when the target references no package at its installed version:
    /// the package left the project, or the project now uses another version of it.
    /// </summary>
    internal static bool IsStale(TrackedSkill entry, IEnumerable<PackageReferenceInfo> referenced) =>
        !referenced.Any(package =>
            package.Id.Equals(entry.Package, StringComparison.OrdinalIgnoreCase) &&
            SameVersion(package.Version, entry.Version));

    /// <summary>
    /// Removes skills this tool installed, narrowed to one package, one exact version of it,
    /// an explicit set of skill names, or the skills that are stale against a target.
    /// </summary>
    /// <param name="only">
    /// Skill folder names to remove. Null removes everything the other filters match, which is
    /// what an unattended uninstall does; a set is what the interactive picker returns.
    /// </param>
    /// <param name="staleAgainst">
    /// A target's direct package references. When given, only skills whose installed version
    /// the target does not reference are removed.
    /// </param>
    public IReadOnlyList<TrackedSkill> Uninstall(
        string destinationRoot,
        string? packageId,
        string? packageVersion,
        bool dryRun,
        IReadOnlyCollection<string>? only = null,
        IReadOnlyCollection<TrackedSkill>? expectedInstalled = null,
        IReadOnlyCollection<PackageReferenceInfo>? staleAgainst = null)
    {
        if (packageVersion is not null) { PackageCoordinate.ParseVersion(packageVersion); }

        using var destinationLock = DestinationLock.Acquire(destinationRoot);
        var manifest = InstallManifest.Load(destinationRoot);

        var chosen = only is null
            ? null
            : new HashSet<string>(only, StringComparer.OrdinalIgnoreCase);

        var trackedSkills = manifest.EnumerateSkills().ToList();
        CheckOwnershipSnapshot(trackedSkills, expectedInstalled);
        var targeted = trackedSkills
            .Where(entry => Matches(entry, packageId, packageVersion))
            .Where(entry => staleAgainst is null || IsStale(entry, staleAgainst))
            .Where(entry => chosen is null || chosen.Contains(entry.Skill))
            .OrderBy(entry => entry.Skill, StringComparer.Ordinal)
            .ToList();
        var targetedPaths = targeted.Select(entry => ToAbsolute(destinationRoot, entry.Skill)).ToList();

        if (targeted.Count == 0 || dryRun)
        {
            return targeted;
        }

        manifest.SetSkills(trackedSkills.Except(targeted));

        foreach (var path in targetedPaths)
        {
            RemoveSkillDirectory(path);
        }

        if (manifest.IsEmpty)
        {
            InstallManifest.Delete(destinationRoot);
            TryRemoveEmptyDirectory(destinationRoot);
        }
        else
        {
            manifest.Save(destinationRoot);
        }

        // Report everything targeted, including entries whose folder a user had already
        // deleted by hand: they are gone either way, and the manifest no longer claims them.
        return targeted;
    }

    internal static bool Matches(TrackedSkill entry, string? packageId, string? packageVersion)
    {
        if (packageId is not null && !entry.Package.Equals(packageId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Compare normalized, so 1.2 and 1.2.0 identify the same installed folder.
        return packageVersion is null ||
               SameVersion(entry.Version, packageVersion);
    }

    private static void CheckOwnershipSnapshot(
        IReadOnlyCollection<TrackedSkill> installed,
        IReadOnlyCollection<TrackedSkill>? expected)
    {
        if (expected is not null && !expected.ToHashSet().SetEquals(installed))
        {
            throw new PackageSkillsException(
                "Installed skill ownership changed while the picker was open. " +
                "No skills were changed by this operation. Run the command again to review the current state.");
        }
    }

    private static string ToAbsolute(string destinationRoot, string relativePath)
    {
        if (!SkillDiscovery.IsSafeSkillName(relativePath))
        {
            throw UnsafeSkillPath(destinationRoot, relativePath);
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationRoot));
        var absolute = Path.GetFullPath(Path.Combine(root, relativePath));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (!string.Equals(Path.GetDirectoryName(absolute), root, comparison))
        {
            throw UnsafeSkillPath(destinationRoot, relativePath);
        }

        return absolute;
    }

    private static PackageSkillsException UnsafeSkillPath(string destinationRoot, string relativePath) =>
        new(
            $"The path '{relativePath}' is not a safe skill folder directly inside '{destinationRoot}'. " +
            "Restore the package or repair the install manifest before retrying. No skills were changed.");

    private static void RemoveSkillDirectory(string absolute)
    {
        if (Directory.Exists(absolute))
        {
            Directory.Delete(absolute, recursive: true);
        }
    }

    private static bool TryRemoveEmptyDirectory(string directory)
    {
        if (!Directory.Exists(directory) || Directory.EnumerateFileSystemEntries(directory).Any())
        {
            return false;
        }

        try
        {
            Directory.Delete(directory);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Replaces the destination with a fresh copy of the source.
    /// </summary>
    /// <remarks>
    /// This copies rather than moves: the NuGet global-packages directory is validated during
    /// restore and shared by every project on the machine. Moving files out can make restore treat a
    /// package as corrupt and strips the skill from every other repository using it.
    /// </remarks>
    private static void CopyDirectory(string source, string destination, bool reuseExistingEmptyDirectory)
    {
        if (reuseExistingEmptyDirectory)
        {
            var directory = new DirectoryInfo(destination);
            if (File.Exists(destination) || directory.Exists || directory.LinkTarget is not null)
            {
                if (!IsReusableEmptyDirectory(directory))
                {
                    throw new IOException(
                        $"The destination folder '{destination}' is no longer an empty ordinary directory.");
                }

                // This path was not managed by the tool, so never remove it recursively.
                Directory.Delete(destination, recursive: false);
            }
        }
        else if (Directory.Exists(destination))
        {
            // Delete first so files removed in a newer package version do not survive.
            Directory.Delete(destination, recursive: true);
        }

        Directory.CreateDirectory(destination);

        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);

            // Files in the NuGet global-packages directory are marked read-only by restore. Copying
            // carries that attribute over, which would make the next install fail to overwrite.
            ClearReadOnly(target);
        }
    }

    private static bool IsReusableEmptyDirectory(DirectoryInfo directory) =>
        directory.LinkTarget is null &&
        directory.Exists &&
        (directory.Attributes & FileAttributes.ReparsePoint) == 0 &&
        !directory.EnumerateFileSystemInfos().Any();

    private static void ClearReadOnly(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
    }

    internal static bool HasSameOwner(TrackedSkill entry, BundledSkill skill) =>
        entry.Package.Equals(skill.PackageId, StringComparison.OrdinalIgnoreCase) &&
        entry.Skill.Equals(skill.SkillName, StringComparison.OrdinalIgnoreCase);

}

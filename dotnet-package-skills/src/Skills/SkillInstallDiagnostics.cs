using DotnetPackageSkills.NuGet;

namespace DotnetPackageSkills.Skills;

public sealed record IgnoredPackage(string PackageId, string PackageVersion, string Reason);

public sealed record PackageVersionWarning(string PackageId, IReadOnlyList<string> Versions);

public sealed record SkillCollision(string RelativePath, IReadOnlyList<BundledSkill> Candidates, string Reason);

public sealed record SkillCandidateClassification(
    IReadOnlyList<BundledSkill> Valid,
    IReadOnlyList<SkippedSkill> Skipped,
    IReadOnlyList<SkillCollision> Collisions);

/// <summary>Rejects every candidate for an ambiguous destination, without choosing an owner.</summary>
public static class SkillCandidateClassifier
{
    public static SkillCandidateClassification Classify(IReadOnlyList<BundledSkill> skills)
    {
        var candidates = skills
            .Select(skill => skill with { PackageVersion = PackagePathResolver.NormalizeVersion(skill.PackageVersion) })
            .OrderBy(skill => skill.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(skill => skill.RelativePath, StringComparer.Ordinal)
            .ThenBy(skill => skill.PackageId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(skill => skill.PackageId, StringComparer.Ordinal)
            .ThenBy(skill => skill.PackageVersion, StringComparer.Ordinal)
            .ThenBy(skill => skill.SourcePath, StringComparer.Ordinal)
            .DistinctBy(skill => (skill.PackageId.ToLowerInvariant(), skill.PackageVersion, skill.RelativePath, skill.SourcePath));
        var valid = new List<BundledSkill>();
        var skipped = new List<SkippedSkill>();
        var collisions = new List<SkillCollision>();

        foreach (var group in candidates.GroupBy(skill => skill.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            var owners = group.ToArray();
            if (owners.Length == 1)
            {
                valid.Add(owners[0]);
                continue;
            }

            const string reason = "multiple package skills target the same destination; all candidates were skipped";
            collisions.Add(new SkillCollision(group.Key, owners, reason));
            skipped.AddRange(owners.Select(skill => ToSkipped(skill, reason)));
        }

        return new SkillCandidateClassification(valid, skipped, collisions);
    }

    internal static SkippedSkill ToSkipped(BundledSkill skill, string reason) =>
        new(skill.RelativePath, skill.PackageId, skill.PackageVersion, skill.SkillName, reason);
}

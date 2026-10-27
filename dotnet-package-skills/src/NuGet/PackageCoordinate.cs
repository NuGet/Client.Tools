using NuGet.Versioning;

namespace DotnetPackageSkills.NuGet;

/// <summary>A package selection, written as <c>Id</c> or <c>Id@Version</c> on the command line.</summary>
public sealed record PackageCoordinate(string Id, string? Version = null)
{
    public const char Separator = '@';

    /// <summary>
    /// Parses <c>Id</c> or <c>Id@Version</c>, rejecting a supplied version that is not exact.
    /// </summary>
    /// <remarks>
    /// Version ranges and floating versions are refused rather than resolved. Resolving one
    /// means picking a version, and the only correct answer to "which version" comes from a
    /// project's restore — which is what <c>--target</c> is for. Guessing here would copy
    /// skills that describe a version the user does not actually reference.
    /// An omitted version is resolved from the destination manifest first, then from a target,
    /// never from package directories.
    /// </remarks>
    public static PackageCoordinate Parse(string value)
    {
        var input = value?.Trim() ?? string.Empty;

        if (input.Length == 0)
        {
            throw new PackageSkillsException(
                "--package needs a package ID or Id@Version, for example Mockly@1.10.0. " +
                "A bare ID uses the destination manifest first, then --target or an auto-detected target.");
        }

        var separator = input.IndexOf(Separator);

        if (separator < 0)
        {
            ValidateId(input);
            return new PackageCoordinate(input);
        }

        if (input.IndexOf(Separator, separator + 1) >= 0)
        {
            throw new PackageSkillsException($"'{input}' has more than one '{Separator}'. Expected Id@Version.");
        }

        var id = input[..separator].Trim();
        var version = input[(separator + 1)..].Trim();

        if (id.Length == 0)
        {
            throw new PackageSkillsException($"'{input}' is missing a package id before the '{Separator}'.");
        }

        ValidateId(id);

        if (version.Length == 0)
        {
            throw new PackageSkillsException($"'{input}' is missing a version after the '{Separator}'.");
        }

        if (IsFloatingOrRange(version))
        {
            throw new PackageSkillsException(
                $"""
                 '{version}' is a floating version or a version range, and this tool needs an exact version.
                 Write it out, for example --package {id}@1.10.0.
                 To let restore choose the version, point at a project or solution with --target instead.
                 """);
        }

        ParseVersion(version);

        return new PackageCoordinate(id, version);
    }

    internal static NuGetVersion ParseVersion(string version)
    {
        if (!NuGetVersion.TryParse(version, out var parsed))
        {
            throw new PackageSkillsException(
                $"'{version}' is not a version this tool recognises. Expected something like 1.10.0 or 2.0.0-beta.1.");
        }

        return parsed;
    }

    internal static void ValidateId(string id)
    {
        if (!IsValidId(id))
        {
            throw new PackageSkillsException(
                $"'{id}' is not a valid package id. Ids must be {PackageIdValidator.MaxPackageIdLength} characters or fewer " +
                "and use letters, digits and '_', joined by single '.' or '-' characters.");
        }
    }

    /// <summary>
    /// Uses NuGet's packaging rules for package ids, including Unicode characters and length.
    /// </summary>
    internal static bool IsValidId(string id)
    {
        try
        {
            PackageIdValidator.ValidatePackageId(id);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Wildcards and NuGet interval notation: <c>4.*</c>, <c>[1.0,2.0)</c>, <c>(,3.0]</c>.</summary>
    private static readonly char[] RangeCharacters = ['*', '[', ']', '(', ')', ','];

    private static bool IsFloatingOrRange(string version) => version.IndexOfAny(RangeCharacters) >= 0;

    public override string ToString() => Version is null ? Id : $"{Id}{Separator}{Version}";
}

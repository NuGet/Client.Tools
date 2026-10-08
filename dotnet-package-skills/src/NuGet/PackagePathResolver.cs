namespace DotnetPackageSkills.NuGet;

/// <summary>
/// Maps a package id and version to its folder inside the global packages cache.
/// </summary>
/// <remarks>
/// Restore extracts each package to <c>&lt;global-packages&gt;/&lt;id&gt;/&lt;version&gt;/</c>
/// with both segments lowercased and the version normalized. This mirrors NuGet's own
/// normalization rules. A case-insensitive directory scan also supports older cache
/// layouts that retained an original valid version spelling.
/// </remarks>
public static class PackagePathResolver
{
    /// <summary>Returns the extracted package folder, or null when it is not on disk.</summary>
    public static string? Resolve(string globalPackagesFolder, string packageId, string version)
    {
        var normalized = NormalizeVersion(version);
        var packageDirectory = Path.Combine(globalPackagesFolder, packageId.ToLowerInvariant());

        if (!Directory.Exists(packageDirectory))
        {
            return null;
        }

        var candidate = Path.Combine(packageDirectory, normalized);
        if (Directory.Exists(candidate))
        {
            return candidate;
        }

        // Older cache layouts may retain the original spelling of a valid version.
        foreach (var directory in Directory.EnumerateDirectories(packageDirectory))
        {
            var name = Path.GetFileName(directory);
            if (name.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
                name.Equals(version, StringComparison.OrdinalIgnoreCase))
            {
                return directory;
            }
        }

        return null;
    }

    /// <summary>
    /// Normalizes a version the way NuGet does for folder names: lowercased, build
    /// metadata dropped, padded to three parts, and a fourth part dropped when zero.
    /// So <c>1.2</c> becomes <c>1.2.0</c> and <c>1.2.3.0</c> becomes <c>1.2.3</c>.
    /// </summary>
    public static string NormalizeVersion(string version) =>
        PackageCoordinate.ParseVersion(version).ToNormalizedString().ToLowerInvariant();
}

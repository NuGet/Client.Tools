namespace DotnetPackageSkills.Infrastructure;

internal static class DestinationLocator
{
    public static string Resolve(string? destination, string workingDirectory)
    {
        if (destination is not null)
        {
            return Path.GetFullPath(destination, workingDirectory);
        }

        return ResolveDefault(workingDirectory, Ancestors(workingDirectory));
    }

    internal static string ResolveDefault(string workingDirectory, IEnumerable<DirectoryInfo> ancestors)
    {
        foreach (var directory in ancestors)
        {
            var git = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(git))
            {
                return Path.GetFullPath(InstallRequest.DefaultDestination, directory.FullName);
            }

            try
            {
                if (directory.EnumerateFiles().Any(file =>
                    file.Extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
                    file.Extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)) ||
                    File.Exists(git))
                {
                    return Path.GetFullPath(InstallRequest.DefaultDestination, directory.FullName);
                }
            }
            catch (UnauthorizedAccessException error)
            {
                throw new PackageSkillsException(
                    $"Could not read directory '{directory.FullName}' while discovering the default skills destination. " +
                    "Check its permissions or pass an explicit --destination to bypass discovery. " +
                    "No skills were changed.",
                    error);
            }
        }

        return Path.GetFullPath(InstallRequest.DefaultDestination, workingDirectory);
    }

    internal static IEnumerable<DirectoryInfo> Ancestors(string workingDirectory)
    {
        for (DirectoryInfo? directory = new(workingDirectory); directory?.Parent is not null; directory = directory.Parent)
        {
            yield return directory;
        }
    }
}

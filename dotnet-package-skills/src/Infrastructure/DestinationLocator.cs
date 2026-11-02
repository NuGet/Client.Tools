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
            if (Directory.Exists(git) || File.Exists(git) ||
                directory.EnumerateFiles().Any(file =>
                    file.Extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
                    file.Extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)))
            {
                return Path.GetFullPath(InstallRequest.DefaultDestination, directory.FullName);
            }
        }

        return Path.GetFullPath(InstallRequest.DefaultDestination, workingDirectory);
    }

    private static IEnumerable<DirectoryInfo> Ancestors(string workingDirectory)
    {
        for (DirectoryInfo? directory = new(workingDirectory); directory is not null; directory = directory.Parent)
        {
            yield return directory;
        }
    }
}

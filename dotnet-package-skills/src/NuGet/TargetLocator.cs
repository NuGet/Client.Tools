namespace DotnetPackageSkills.NuGet;

/// <summary>Finds the solution or project to inspect when the user does not name one.</summary>
public static class TargetLocator
{
    private static readonly string[] SolutionExtensions = [".slnx", ".sln"];
    private static readonly string[] ProjectExtensions = [".csproj", ".fsproj", ".vbproj"];
    private static readonly string[] IgnoredDirectories = ["bin", "obj", ".git", "node_modules", "artifacts"];

    /// <summary>
    /// Resolves an explicit target, or auto-detects one under <paramref name="workingDirectory"/>.
    /// A directory is accepted and searched.
    /// </summary>
    public static string Resolve(string? requested, string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return Detect(workingDirectory);
        }

        var path = Path.GetFullPath(requested, workingDirectory);

        if (Directory.Exists(path))
        {
            return Detect(path);
        }

        if (!File.Exists(path))
        {
            throw new PackageSkillsException($"--target does not exist: {path}");
        }

        var extension = Path.GetExtension(path);
        if (!SolutionExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) &&
            !ProjectExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            throw new PackageSkillsException(
                $"--target must be a solution or project file, but got '{Path.GetFileName(path)}'. " +
                "Supported extensions: .sln, .slnx, .csproj, .fsproj, .vbproj.");
        }

        return path;
    }

    /// <summary>
    /// Searches for a target, preferring a solution over a project and the top level
    /// over nested directories. A solution covers every project in one pass, which is
    /// almost always what someone means by "my repo".
    /// </summary>
    public static string Detect(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new PackageSkillsException($"Directory does not exist: {directory}");
        }

        var (topLevel, children) = ReadDirectory(directory, explicitRoot: true);
        if (BestTarget(topLevel) is { } rootTarget) { return rootTarget; }

        var nested = new List<string>();
        var pending = new Stack<string>(children);
        while (pending.TryPop(out var child))
        {
            var (files, directories) = ReadDirectory(child, explicitRoot: false);
            nested.AddRange(files);
            foreach (var descendant in directories) { pending.Push(descendant); }
        }

        if (BestTarget(nested) is { } nestedTarget) { return nestedTarget; }

        throw new PackageSkillsException(
            $"No solution or project found under {directory}. " +
            "Pass one explicitly, for example: --target src/MyApp.sln");
    }

    private static (List<string> Files, List<string> Children) ReadDirectory(string directory, bool explicitRoot)
    {
        var files = new List<string>();
        var children = new List<string>();
        try
        {
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if (entry is DirectoryInfo child)
                {
                    if (!IgnoredDirectories.Contains(child.Name, StringComparer.OrdinalIgnoreCase) &&
                        (child.Attributes & FileAttributes.ReparsePoint) == 0)
                    {
                        children.Add(child.FullName);
                    }
                }
                else if (Rank(entry.FullName) >= 0)
                {
                    files.Add(entry.FullName);
                }
            }
        }
        catch (UnauthorizedAccessException error)
        {
            if (explicitRoot)
            {
                throw new PackageSkillsException(
                    $"Could not read target directory '{directory}'. Check its permissions or pass a readable --target.",
                    error);
            }

            return ([], []);
        }
        catch (DirectoryNotFoundException) when (!explicitRoot) { return ([], []); }

        return (files, children);
    }

    private static string? BestTarget(IEnumerable<string> files) =>
        files.OrderBy(Rank).ThenBy(file => file, StringComparer.Ordinal).FirstOrDefault();

    private static int Rank(string file)
    {
        var extension = Path.GetExtension(file);
        var solution = Array.FindIndex(SolutionExtensions,
            candidate => candidate.Equals(extension, StringComparison.OrdinalIgnoreCase));
        if (solution >= 0) { return solution; }

        var project = Array.FindIndex(ProjectExtensions,
            candidate => candidate.Equals(extension, StringComparison.OrdinalIgnoreCase));
        return project < 0 ? -1 : SolutionExtensions.Length + project;
    }
}

namespace JJConsulting.DbUp.Cli;

internal static class DirectoryDiscovery
{
    // Search locally first, and never escape a repository or solution boundary.
    public static IEnumerable<DirectoryInfo> Ancestors(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
        {
            yield return directory;
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                File.Exists(Path.Combine(directory.FullName, ".git")) ||
                directory.EnumerateFiles("*.sln*").Any())
                yield break;
        }
    }

    public static string FindScripts(string workingDirectory)
    {
        foreach (var directory in Ancestors(workingDirectory))
        {
            if (IsScriptRoot(directory.FullName))
                return directory.FullName;

            var candidates = directory.EnumerateDirectories()
                .Where(child => child.LinkTarget is null && child.Name is not ("bin" or "obj" or "node_modules") &&
                                !child.Name.StartsWith('.') && IsScriptRoot(child.FullName))
                .Select(child => child.FullName).ToArray();
            if (candidates.Length == 1)
                return candidates[0];
            if (candidates.Length > 1)
                throw new CliException("Multiple scripts directories were found. Select one with --scripts.");
        }

        // Allows `new` and `baseline` to initialize an empty directory as well.
        return Path.GetFullPath(workingDirectory);
    }

    private static bool IsScriptRoot(string path)
    {
        return Directory.Exists(Path.Combine(path, "Migrations")) || Directory.Exists(Path.Combine(path, "ProgrammableObjects"));
    }
}

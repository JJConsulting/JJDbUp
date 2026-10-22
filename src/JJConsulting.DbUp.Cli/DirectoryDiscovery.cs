using System.Xml.Linq;

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

    public static string InferNamespace(string scriptsPath)
    {
        foreach (var directory in Ancestors(scriptsPath))
        {
            if (!directory.Exists)
                continue;
            var projects = directory.EnumerateFiles("*.csproj").ToArray();
            if (projects.Length > 1)
                throw new CliException("Multiple projects were found. Set --base-namespace explicitly.");
            if (projects.Length == 0)
                continue;

            XDocument project;
            try
            {
                project = XDocument.Load(projects[0].FullName);
            }
            catch (System.Xml.XmlException)
            {
                throw new CliException("The project could not be read. Set --base-namespace explicitly.");
            }
            var namespaces = project.Descendants().Where(element => element.Name.LocalName == "RootNamespace")
                .Select(element => element.Value.Trim()).Distinct().ToArray();
            if (namespaces.Length > 1 || namespaces.Any(value => string.IsNullOrWhiteSpace(value) || value.Contains("$(")))
                throw new CliException("The project namespace could not be inferred. Set --base-namespace explicitly.");
            var rootNamespace = namespaces.SingleOrDefault() ?? Path.GetFileNameWithoutExtension(projects[0].Name).Replace(' ', '_');
            var relative = Path.GetRelativePath(directory.FullName, scriptsPath);
            return relative == "." ? rootNamespace : rootNamespace + "." +
                relative.Replace(Path.DirectorySeparatorChar, '.').Replace(Path.AltDirectorySeparatorChar, '.');
        }

        return new DirectoryInfo(scriptsPath).Name;
    }

    private static bool IsScriptRoot(string path)
    {
        return Directory.Exists(Path.Combine(path, "Migrations")) || Directory.Exists(Path.Combine(path, "ProgrammableObjects"));
    }
}

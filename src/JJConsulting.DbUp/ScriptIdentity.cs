using System.Xml;
using System.Xml.Linq;

namespace JJConsulting.DbUp;

/// <summary>Preserves journal names without a separately configured namespace.</summary>
internal static class ScriptIdentity
{
    public static string DirectoryPrefix(string scriptsPath)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(scriptsPath));
        for (var directory = new DirectoryInfo(fullPath); directory is not null; directory = directory.Parent)
        {
            var projects = directory.EnumerateFiles("*.csproj").ToArray();
            if (projects.Length > 1)
                throw new InvalidDataException("Multiple projects were found for the scripts directory.");
            if (projects.Length == 1)
            {
                XDocument project;
                try
                {
                    project = XDocument.Load(projects[0].FullName);
                }
                catch (XmlException)
                {
                    throw new InvalidDataException("The scripts project could not be read.");
                }
                var namespaces = project.Descendants().Where(element => element.Name.LocalName == "RootNamespace")
                    .Select(element => element.Value.Trim()).Distinct().ToArray();
                if (namespaces.Length > 1 || namespaces.Any(value => string.IsNullOrWhiteSpace(value) || value.Contains("$(")))
                    throw new InvalidDataException("The scripts project namespace could not be inferred.");
                var root = namespaces.SingleOrDefault() ?? Path.GetFileNameWithoutExtension(projects[0].Name).Replace(' ', '_');
                var relative = Path.GetRelativePath(directory.FullName, fullPath);
                return relative == "." ? root : root + "." +
                    relative.Replace(Path.DirectorySeparatorChar, '.').Replace(Path.AltDirectorySeparatorChar, '.');
            }
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                File.Exists(Path.Combine(directory.FullName, ".git")) ||
                directory.EnumerateFiles("*.sln*").Any())
                break;
        }
        return new DirectoryInfo(fullPath).Name;
    }

    public static bool IsScript(string name) => CategoryPrefixes(name).Any();

    public static Dictionary<string, string> ResolvePrefixes(IEnumerable<string> names)
    {
        var candidates = names.Distinct(StringComparer.Ordinal)
            .SelectMany(CategoryPrefixes)
            .ToArray();
        // Unambiguous names and sibling categories establish the script roots.
        var roots = candidates.GroupBy(candidate => candidate.Prefix, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(candidate => candidate.Category).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2)
            .Select(group => group.Key)
            .Concat(candidates.GroupBy(candidate => candidate.Name, StringComparer.Ordinal)
                .Where(group => group.Count() == 1).Select(group => group.Single().Prefix))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(prefix => prefix.Length).ToArray();
        var prefixes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var script in candidates.GroupBy(candidate => candidate.Name, StringComparer.Ordinal))
        {
            var root = roots.FirstOrDefault(prefix => script.Any(candidate =>
                candidate.Prefix.Equals(prefix, StringComparison.OrdinalIgnoreCase)));
            if (root is null && script.Count() > 1)
                throw new InvalidDataException("The SQL script category is ambiguous. Use a scripts directory or unambiguous resource names.");
            prefixes.Add(script.Key, root ?? script.Single().Prefix);
        }
        return prefixes;
    }

    public static string? RelativeName(string name, string category, string prefix)
    {
        if (!name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            return null;
        var categoryPrefix = (prefix.Length == 0 ? "" : prefix + ".") + category + ".";
        return name.StartsWith(categoryPrefix, StringComparison.OrdinalIgnoreCase) ? name[categoryPrefix.Length..] : null;
    }

    private static IEnumerable<ScriptCategoryPrefix> CategoryPrefixes(string name)
    {
        if (!name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            yield break;
        var segments = name.Split('.');
        for (var index = 0; index < segments.Length - 1; index++)
        {
            var category = segments[index];
            if (category.Equals("Migrations", StringComparison.OrdinalIgnoreCase) ||
                category.Equals("ProgrammableObjects", StringComparison.OrdinalIgnoreCase))
                yield return new ScriptCategoryPrefix(name, string.Join('.', segments[..index]), category);
        }
    }

    private sealed record ScriptCategoryPrefix(string Name, string Prefix, string Category);
}

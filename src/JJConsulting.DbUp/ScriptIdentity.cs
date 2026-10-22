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

    public static string? RelativeName(string name, string category, string? directoryPrefix = null)
    {
        if (!name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            return null;
        if (directoryPrefix is not null)
        {
            var prefix = directoryPrefix + "." + category + ".";
            return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? name[prefix.Length..] : null;
        }
        var index = CategoryIndex(name, category);
        var otherCategory = category == "Migrations" ? "ProgrammableObjects" : "Migrations";
        if (index < 0 || CategoryIndex(name, otherCategory) > index)
            return null;
        return name[(index + category.Length + 1)..];
    }

    private static int CategoryIndex(string name, string category)
    {
        var index = name.LastIndexOf("." + category + ".", StringComparison.OrdinalIgnoreCase);
        return index >= 0 ? index + 1 : name.StartsWith(category + ".", StringComparison.OrdinalIgnoreCase) ? 0 : -1;
    }
}

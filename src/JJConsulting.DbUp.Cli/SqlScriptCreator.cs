namespace JJConsulting.DbUp.Cli;

internal static class SqlScriptCreator
{
    public static string Create(DbUpOptionsLoader optionsLoader, string name, string type)
    {
        if (optionsLoader.Migrations.ScriptsPath is null)
            throw new CliException("The new command requires a scripts directory.");

        if (type is not ("migration" or "object"))
            throw new CliException("Type must be migration or object.");

        name = NormalizeScriptName(name);
        var root = Path.GetFullPath(optionsLoader.Migrations.ScriptsPath);
        var categoryDirectory = Path.Combine(root, type == "migration" ? "Migrations" : "ProgrammableObjects");
        var destination = Path.GetFullPath(Path.Combine(categoryDirectory, name));

        if (!destination.StartsWith(categoryDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new CliException("The script must remain inside its category directory.");

        var destinationDirectory = Path.GetDirectoryName(destination)!;
        
        EnsureNoSymbolicLinkDirectories(destinationDirectory);

        if (File.Exists(destination) || new FileInfo(destination).LinkTarget is not null)
            throw new CliException("The script already exists.");

        Directory.CreateDirectory(destinationDirectory);
        using var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
        using var writer = new StreamWriter(stream);
        
        if (type == "migration")
            writer.WriteLine("-- Schema migration: add SQL below. Do not edit after it has been applied.");
        else
            writer.WriteLine("-- Programmable object: use CREATE OR ALTER and add dependency declarations as needed.");

        return destination;
    }

    private static string NormalizeScriptName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new CliException("A script name is required.");

        name = name.Replace('\\', '/');

        if (Path.IsPathRooted(name) || name.Split('/').Any(segment => segment is ".." or "." or "") || name.Contains(':'))
            throw new CliException("Use a relative script name within the selected category.");

        return name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) ? name : name + ".sql";
    }

    private static void EnsureNoSymbolicLinkDirectories(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
        {
            if (directory.LinkTarget is not null)
                throw new CliException("Script directories must not be symbolic links.");
        }
    }
}

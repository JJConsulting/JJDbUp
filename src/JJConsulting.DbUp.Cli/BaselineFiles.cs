using System.Text;

namespace JJConsulting.DbUp.Cli;

internal sealed class BaselineFiles
{
    private readonly string _destination;

    public BaselineFiles(string scriptsPath, string name)
    {
        _destination = Path.Combine(Path.GetFullPath(scriptsPath), "Migrations", BaselineService.NormalizeName(name));
        EnsureDestinationAbsent();
    }

    public BaselineFileResult Save(BaselineGenerationResult generated, bool dryRun)
    {
        if (Path.GetFileName(_destination) != generated.ScriptName)
            throw new CliException("The generated migration name does not match its destination.");
        EnsureDestinationAbsent();
        if (!dryRun)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_destination)!);
            using var stream = new FileStream(_destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(generated.Sql);
        }
        return new BaselineFileResult(_destination, dryRun);
    }

    private void EnsureDestinationAbsent()
    {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(_destination)!); directory is not null; directory = directory.Parent)
            if (directory.LinkTarget is not null)
                throw new CliException("Migration directories must not be symbolic links.");
        if (File.Exists(_destination) || Directory.Exists(_destination) || new FileInfo(_destination).LinkTarget is not null)
            throw new CliException("The migration destination already exists.");
    }
}

internal sealed record BaselineFileResult(string Path, bool DryRun);

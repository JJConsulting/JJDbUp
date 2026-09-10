using System;
using System.Collections.Generic;
using System.Text;

namespace JJDbUpMigrator.Config;

public sealed class DatabaseMigrationException : Exception
{
    public string Stage { get; }

    public string ScriptName { get; }

    public DatabaseMigrationException(
        string stage,
        string scriptName,
        Exception innerException)
        : base(
            BuildMessage(stage, scriptName),
            innerException)
    {
        Stage = stage;
        ScriptName = scriptName;
    }

    private static string BuildMessage(
        string stage,
        string scriptName)
    {
        if (string.IsNullOrWhiteSpace(scriptName))
        {
            return string.Format(
                "Database migration failed during '{0}'.",
                stage);
        }

        return string.Format(
            "Database migration failed during '{0}'. Script: '{1}'.",
            stage,
            scriptName);
    }
}

